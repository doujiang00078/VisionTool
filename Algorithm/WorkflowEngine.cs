using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using VisionTool.Enums;
using VisionTool.Interfaces;
using VisionTool.Models;

namespace VisionTool.Algorithm
{
    /// <summary>
    /// 工作流引擎
    /// </summary>
    public class WorkflowEngine : IDisposable
    {
        #region 字段定义

        /// <summary>
        /// 任务字典：存储所有工作流任务，Key=任务名称
        /// </summary>
        private readonly ConcurrentDictionary<string, IWorkflowTask> _tasks;

        /// <summary>
        /// 下游依赖图：用于快速查找某任务完成后需要触发的子任务
        /// Key=父任务名, Value=依赖该父任务的所有子任务名称列表
        /// </summary>
        private readonly ConcurrentDictionary<string, List<string>> _downstreamDependencies;

        /// <summary>
        /// 任务名到索引的映射：用于快速访问入度数组
        /// 避免每次都进行字典查找，提升性能
        /// </summary>
        private readonly ConcurrentDictionary<string, int> _taskNameToIndex;

        /// <summary>
        /// 工作流执行上下文：存储参数和任务输出结果
        /// </summary>
        private WorkflowExecutionContext _context;

        /// <summary>
        /// 状态变更通知队列：批量处理状态通知，减少线程切换开销
        /// </summary>
        private readonly ConcurrentQueue<WorkflowTaskStatus> _statusQueue = new();

        /// <summary>
        /// 通知处理标志：确保同时只有一个线程在处理通知队列
        /// </summary>
        private int _notifyPending;

        /// <summary>
        /// 始终运行任务集合：即使上游任务失败，这些任务仍会被调度执行
        /// </summary>
        private readonly HashSet<string> _alwaysRunTasks = new();

        /// <summary>
        /// 上一帧的 StepOutputs，在下一帧 ResetWorkflowState 时统一释放。
        /// 解决 UI/异步消费者仍持有旧 result 引用时，ProcessTaskAsync 提前 Dispose 导致的 use-after-free。
        /// 注意：这是**引擎级**（跨帧）状态，不随 <see cref="WorkflowRun"/> 走——按帧隔离的释放窗口
        /// 才有意义（本帧开始释放上一帧的结果）。
        /// </summary>
        private readonly ConcurrentBag<InspectionStepResult> _previousOutputs = new();

        /// <summary>
        /// 在飞运行集合：一帧 = 一个 <see cref="WorkflowRun"/>。仅用于生命周期管理
        /// （Dispose 时取消所有还在等待的运行；原来只有单个 _workflowTcs 可取消）。
        /// </summary>
        private readonly ConcurrentDictionary<WorkflowRun, byte> _activeRuns = new();

        /// <summary>
        /// 每个步骤实例的互斥门（Key = 任务名）。只对**没有声明** <see cref="BaseWorkflowTask.SupportsFrameConcurrency"/>
        /// 的步骤生效。
        /// 为什么需要：步骤实例由 <see cref="AddTask"/> 注册后**跨帧复用**，而步骤体里全是实例状态
        /// （<c>_latestResult</c>、算法实例、帧内临时缓存）。两帧同时进同一个步骤体必然互相踩，
        /// 结果是"静默出错结果"而不是变慢。门覆盖整段「等待 → 执行 → 取走结果」，
        /// 于是 <see cref="TakeResult"/> 取结果时不会被另一帧的步骤体插入。
        /// 代价：同一个步骤在两帧之间串行——**多帧的吞吐收益被这个门封顶**（一帧的并行宽度 ≤ 门限定的同名步骤数）。
        /// 因此"把帧内状态搬进帧上下文"（声明 SupportsFrameConcurrency）才是多帧真正的推进方式，
        /// 清单见主程序 <c>AlgorithmParallel.FrameConcurrency</c>。
        /// </summary>
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _stepGates = new();

        /// <summary>
        /// 强制串行执行模式：所有任务按依赖拓扑顺序逐个执行，不并行。
        /// </summary>
        public bool ForceSerialExecution { get; set; }

        /// <summary>
        /// 步骤体执行器。默认线程池（保持历史行为）；应用侧可以注入一个**专用调度器**，
        /// 让步骤体不再与通讯、取图、存图、UI 争线程池线程。
        /// 注意：注入的调度器**不能**是"算法分支预算"本身——步骤占住预算槽后，
        /// 步骤内部的并行分支会排不到线程而自锁（见主程序 AlgorithmTaskScheduler 的约束与用例）。
        /// 语义：调度器限制的是**同时在跑**的步骤数（线程占用），不是"在飞"的步骤数——
        /// 步骤体若 await 让出线程，在飞数可以超过线程数；生产的步骤体是同步 HALCON 调用，一直占着线程。
        /// </summary>
        private readonly TaskScheduler _scheduler;

        #endregion 字段定义

        #region 帧级运行状态

        /// <summary>
        /// 一次运行的帧级可变状态。之所以单独成类：这些状态原本全部落在 <see cref="WorkflowEngine"/>
        /// 实例字段上，同一实例并发跑两帧时会互相踩（后一帧的 ResetWorkflowState 清掉前一帧的入度/登记集合，
        /// 帧一起提交到同一个完成信号上）。搬进来之后，引擎实例上只剩配置与跨帧状态，每帧一份 Run。
        /// 生命周期：<see cref="StartWorkflowAsync(WorkflowExecutionContext, CancellationToken, string, int)"/>
        /// 创建，本帧所有任务与下游调度全部退出后废弃。
        /// </summary>
        private sealed class WorkflowRun
        {
            public WorkflowRun(WorkflowExecutionContext context, CancellationToken token, string runToTaskName)
            {
                Context = context;
                Token = token;
                RunToTaskName = runToTaskName;
                ScheduledTaskNames = new HashSet<string>();
            }

            /// <summary>
            /// 本帧的执行上下文（每帧一份，见 <see cref="WorkflowExecutionContext"/> 的帧上下文构造）。
            /// </summary>
            public WorkflowExecutionContext Context { get; }

            /// <summary>
            /// 本帧的取消令牌。
            /// </summary>
            public CancellationToken Token { get; }

            /// <summary>
            /// 本帧的目标任务名：到达该任务即认为本帧完成。
            /// </summary>
            public string RunToTaskName { get; }

            /// <summary>
            /// 本帧完成信号：用于等待本帧整个工作流执行完成。
            /// </summary>
            public TaskCompletionSource<bool> WorkflowTcs;

            /// <summary>
            /// 本帧剩余未处理任务计数器：使用原子操作，当递减到0时本帧结束。
            /// </summary>
            public int RemainingTaskCount;

            /// <summary>
            /// 本帧入度数组：记录每个任务还有多少依赖未完成。
            /// 使用数组而非字典，减少查找开销，支持原子操作。
            /// </summary>
            public int[] CurrentInDegrees;

            /// <summary>
            /// 本帧取消标志：使用原子操作标记本帧是否已被取消。
            /// </summary>
            public int Cancelled;

            /// <summary>
            /// 本帧取消状态属性：简化取消检查逻辑。
            /// </summary>
            public bool IsCancelled => Volatile.Read(ref Cancelled) == 1;

            /// <summary>
            /// 本帧已登记调度的任务名：防止同一任务被重复调度。
            /// </summary>
            public readonly HashSet<string> ScheduledTaskNames;

            /// <summary>
            /// 本帧登记与停止调度使用的锁。任务先计数再入线程池，直到其执行和下游调度
            /// 全部退出才减计数，避免 Task.WhenAll 快照遗漏后来加入的任务。
            /// </summary>
            public readonly object SchedulingLock = new();

            /// <summary>
            /// 本帧排空信号：本帧所有已登记任务退出后完成。
            /// </summary>
            public TaskCompletionSource<bool> TasksDrained;

            /// <summary>
            /// 本帧在飞任务计数（在 <see cref="SchedulingLock"/> 内访问）。
            /// </summary>
            public int ActiveTaskCount;

            /// <summary>
            /// 本帧是否已停止登记新任务（本帧收尾置位）。
            /// </summary>
            public bool SchedulingStopped;

            /// <summary>
            /// 本帧开始时的"共享参数快照"（见 <see cref="SnapshotSharedParameters"/>）。
            /// 帧结束时与当时的参数比对，报出"帧内改动了跨帧复用参数"的写法。
            /// </summary>
            public List<SharedParameterEntry> SharedParameterSnapshot;
        }

        /// <summary>
        /// 共享参数快照里的一项：成员路径（如 <c>InParam.ResultDict</c>）、是否已标注
        /// <see cref="FrameScopedAttribute"/>、当前值（标量比值，其它比引用）、集合长度。
        /// </summary>
        private readonly struct SharedParameterEntry
        {
            public SharedParameterEntry(string path, bool frameScoped, bool isScalar, bool isCollection, object value, int length)
            {
                Path = path;
                FrameScoped = frameScoped;
                IsScalar = isScalar;
                IsCollection = isCollection;
                Value = value;
                Length = length;
            }

            public readonly string Path;
            public readonly bool FrameScoped;
            public readonly bool IsScalar;
            public readonly bool IsCollection;
            public readonly object Value;

            /// <summary>集合元素的个数（-1 表示非集合；数组用 Length）。</summary>
            public readonly int Length;
        }

        /// <summary>参数对象上被跟踪的一个成员（反射结果按类型缓存，见 <see cref="GetParameterMembers"/>）。</summary>
        private sealed class ParameterMember
        {
            public string Path;
            public bool FrameScoped;
            public bool IsScalar;
            public bool IsCollection;
            public Func<object, object> Get;
        }

        #endregion 帧级运行状态

        // 事件定义
        public event Action<WorkflowTaskStatus> OnTaskStatusChanged;

        public event Action<IEnumerable<WorkflowTaskStatus>, long> OnTaskFinishStatus;

        /// <summary>
        /// 构造函数：初始化工作流引擎
        /// </summary>
        /// <param name="context">工作流执行上下文</param>
        /// <param name="scheduler">
        /// 步骤体执行器；null 表示线程池（历史行为）。
        /// 注入专用调度器可把"步骤级"执行与线程池隔离（现场帧[2300] 的池饥饿即步骤体与
        /// 取图/存图/UI 共用池所致）。
        /// </param>
        public WorkflowEngine(WorkflowExecutionContext context, TaskScheduler scheduler = null)
        {
            _tasks = new ConcurrentDictionary<string, IWorkflowTask>();
            _downstreamDependencies = new ConcurrentDictionary<string, List<string>>();
            _taskNameToIndex = new ConcurrentDictionary<string, int>();
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _scheduler = scheduler ?? TaskScheduler.Default;

            // 这里曾调用 ThreadPool.SetMinThreads(核数×4)：那是**进程级全局副作用**，会把整个
            // 线程池的最小线程数抬到 4 倍逻辑核（20 核机 = 80 条，且 min 以内的线程创建不受
            // 注入节流），与本程序"通讯/取图走 Highest 专用线程、算法走共享预算、存图走
            // BelowNormal"的优先级分层直接冲突——现场单帧 5~7 秒、取图被推迟到秒级都发生在
            // 这种超订下。线程池策略属于应用组合根（主程序 App 启动处），库不得改写；
            // 步骤级线程不足应当通过注入专用执行器解决（见 scheduler 参数）。
        }

        /// <summary>
        /// 添加任务到工作流
        /// </summary>
        /// <param name="task">要添加的任务</param>
        /// <exception cref="ArgumentException">任务名称重复时抛出</exception>
        public void AddTask(IWorkflowTask task)
        {
            // 检查任务名是否重复
            if (!_tasks.TryAdd(task.Name, task))
            {
                throw new ArgumentException($"任务名称 '{task.Name}' 已经存在。");
            }

            // 为任务分配数组索引，用于快速访问入度数组
            _taskNameToIndex[task.Name] = _tasks.Count - 1;

            // 构建下游依赖图：记录每个父任务的子任务列表
            // 注意：AddTask通常在工作流启动前的单线程环境中调用
            if (task.Dependencies != null)
            {
                foreach (var dependencyName in task.Dependencies)
                {
                    _downstreamDependencies.AddOrUpdate(dependencyName,
                        // 如果父任务不存在，创建新列表
                        new List<string> { task.Name },
                        // 如果父任务已存在，将当前任务加入其子任务列表
                        (key, list) =>
                        {
                            lock (list) { list.Add(task.Name); }
                            return list;
                        });
                }
            }
        }

        /// <summary>
        /// 启动工作流执行：并行调度任务，自动处理依赖关系。
        /// 使用引擎构造时传入的上下文（原有行为，现有调用方零影响）。
        /// </summary>
        /// <param name="cancellationToken">取消令牌，用于超时或手动取消</param>
        /// <param name="runToTaskName">指定运行到某个任务，不指定则执行全部</param>
        /// <returns>异步任务，工作流完成时返回</returns>
        public async Task StartWorkflowAsync(CancellationToken cancellationToken, string runToTaskName = null, int timeoutMs = 0)
        {
            await StartWorkflowAsync(_context, cancellationToken, runToTaskName, timeoutMs).ConfigureAwait(false);
        }

        /// <summary>
        /// 启动工作流执行（用调用方提供的帧上下文）：并行调度任务，自动处理依赖关系。
        /// 同一引擎实例可以并发跑多帧——每帧的入度数组、完成信号、登记集合、任务计数都在
        /// 本次运行的 <see cref="WorkflowRun"/> 里，引擎实例上不再有"当前这一帧"的字段。
        /// </summary>
        /// <param name="context">本次运行的帧上下文</param>
        /// <param name="cancellationToken">取消令牌，用于超时或手动取消</param>
        /// <param name="runToTaskName">指定运行到某个任务，不指定则执行全部</param>
        /// <returns>异步任务，工作流完成时返回</returns>
        public async Task StartWorkflowAsync(WorkflowExecutionContext context, CancellationToken cancellationToken, string runToTaskName = null, int timeoutMs = 0)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            var time = Stopwatch.StartNew();

            // 只在本帧开始时已处于串行模式时保留监控器快照。
            var serialExecution = ForceSerialExecution;

            var run = new WorkflowRun(context, cancellationToken, runToTaskName);
            _activeRuns[run] = 0;

            // 步骤1：重置所有任务状态，初始化入度数组
            ResetWorkflowState(run);

            // 步骤1.5：帧首快照共享参数（帧尾比对，见 ReportSharedParameterMutations）。
            // 只对**当前图**运行检测：模板图运行的本职就是"把模板基准写进参数"（基准点/印空区域/模板中心…），
            // 那是契约内的跨帧状态，不是每帧产物；对它检测只会刷一串误报（预热 run、模板提取 run 都属此类）。
            if (DetectSharedParameterMutation && run.Context?.RunImageType == RunType.当前图)
                run.SharedParameterSnapshot = SnapshotSharedParameters(run.Context);

            // 步骤2：初始化工作流控制信号
            // 使用 RunContinuationsAsynchronously 防止死锁
            run.WorkflowTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            run.RemainingTaskCount = _tasks.Count;
            Interlocked.Exchange(ref run.Cancelled, 0);

            // 步骤3：注册取消回调，处理超时或手动取消
            using var reg = cancellationToken.Register(() =>
            {
                // 使用 CAS 操作确保只执行一次
                if (Interlocked.CompareExchange(ref run.Cancelled, 1, 0) == 0)
                {
                    // 将所有待处理和就绪的任务标记为已取消
                    foreach (var task in _tasks.Values.Where(t =>
                        t.State == WorkflowState.Pending || t.State == WorkflowState.Ready))
                    {
                        SetTaskCancelled(run, task);
                    }
                    run.WorkflowTcs.TrySetCanceled();
                }
            });

            // 步骤4：寻找起始节点（入度为0的任务，即没有依赖的任务）
            var readyTasks = new List<IWorkflowTask>();
            foreach (var task in _tasks.Values)
            {
                if (_taskNameToIndex.TryGetValue(task.Name, out var idx) && run.CurrentInDegrees[idx] == 0)
                {
                    readyTasks.Add(task);
                }
            }

            // 验证工作流是否有效
            if (readyTasks.Count == 0 && _tasks.Count > 0)
            {
                _activeRuns.TryRemove(run, out _);
                throw new InvalidOperationException("工作流存在循环依赖或无起始节点。");
            }

            // 登记根任务前初始化本帧排空信号。
            lock (run.SchedulingLock)
            {
                run.ScheduledTaskNames.Clear();
                run.ActiveTaskCount = 0;
                run.SchedulingStopped = false;
                run.TasksDrained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            try
            {
                if (_tasks.Count == 0)
                {
                    run.WorkflowTcs.TrySetResult(true);
                }
                else if (serialExecution)
                {
                    // 串行模式：按拓扑顺序逐个执行
                    await RunSerialAsync(run, readyTasks).ConfigureAwait(false);
                    run.WorkflowTcs.TrySetResult(true);
                }
                else
                {
                    // 步骤5：并行启动所有起始任务
                    foreach (var task in readyTasks)
                        ScheduleTask(run, task);
                }

                // 步骤6：等待工作流完成信号
                await run.WorkflowTcs.Task.ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                // 忽略取消异常，使用正常流程结束
            }
            finally
            {
                Task drained;
                lock (run.SchedulingLock)
                {
                    run.SchedulingStopped = true;
                    if (run.ActiveTaskCount == 0)
                        run.TasksDrained.TrySetResult(true);
                    drained = run.TasksDrained.Task;
                }
                // CancellationToken 无法中断已经进入 HALCON 的同步算子。
                // 即使超时，也必须等所有算子和 finally 退出，调用方才可释放本帧输入。
                await drained.ConfigureAwait(false);

                // 所有本帧任务结束后再采样
                time.Stop();
                NotifyFinishStatus(run, time.ElapsedMilliseconds);

                // 帧尾比对：报出"帧内改动了跨帧复用的参数"（不报错、只串帧的那一类写法）
                if (DetectSharedParameterMutation)
                    ReportSharedParameterMutations(run, run.SharedParameterSnapshot);
                run.SharedParameterSnapshot = null;

                _activeRuns.TryRemove(run, out _);
                // 本帧是最后一个在飞帧时补放延迟的旧模板图（多帧并发期间被跳过的那些）。
                if (_activeRuns.IsEmpty)
                    run.Context?.ReleaseDeferredImages();
                Debug.WriteLine("工作流结束。");
            }
        }

        private void ScheduleTask(WorkflowRun run, IWorkflowTask task)
        {
            lock (run.SchedulingLock)
            {
                if (run.SchedulingStopped || run.IsCancelled || run.Token.IsCancellationRequested
                    || run.WorkflowTcs.Task.IsCompleted || task.State == WorkflowState.Skipped
                    || !run.ScheduledTaskNames.Add(task.Name))
                    return;

                UpdateTaskState(task, WorkflowState.Ready);
                run.ActiveTaskCount++;
                try
                {
                    // 用 _scheduler 启动而不是 Task.Run：步骤体（HALCON、CPU 密集、可能跑数百毫秒）
                    // 不应与通讯/取图/存图/UI 争线程池线程。DenyChildAttach 与 Task.Run 语义一致；
                    // Unwrap 让 await 拿到内层 Task（与 Task.Run(async …) 等价）。
                    _ = Task.Factory.StartNew(async () =>
                    {
                        try
                        {
                            await ProcessTaskAsync(run, task).ConfigureAwait(false);
                        }
                        finally
                        {
                            CompleteScheduledTask(run);
                        }
                    }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, _scheduler).Unwrap();
                }
                catch (Exception ex)
                {
                    CompleteScheduledTask(run);
                    run.WorkflowTcs.TrySetException(ex);
                }
            }
        }

        private void CompleteScheduledTask(WorkflowRun run)
        {
            lock (run.SchedulingLock)
            {
                if (--run.ActiveTaskCount == 0 && run.SchedulingStopped)
                    run.TasksDrained.TrySetResult(true);
            }
        }

        /// <summary>
        /// 任务执行核心方法：执行单个任务并处理后续调度
        /// 【极速模式】使用多重优化措施减少延迟
        /// </summary>
        /// <param name="run">本次运行（帧级状态）</param>
        /// <param name="task">要执行的任务</param>
        private async Task ProcessTaskAsync(WorkflowRun run, IWorkflowTask task)
        {
            if (run.WorkflowTcs.Task.IsCompleted || run.IsCancelled) return;

            try
            {
                if (run.Token.IsCancellationRequested || run.IsCancelled)
                {
                    SetTaskCancelled(run, task);
                    return;
                }

                UpdateTaskState(task, WorkflowState.Running);
                EnqueueStatusNotification(task.Name, WorkflowState.Running);

                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 任务 {task.Name} 开始执行 (线程:{Thread.CurrentThread.ManagedThreadId})");

                var (newState, result) = await ExecuteTaskAsync(run.Context, run.Token, task).ConfigureAwait(false);

                // 把本步骤的终态落回任务：步骤体自己不一定写（抛异常提前退出时肯定没写），
                // 不写的话失败步骤会一直显示"运行中"（状态查询与 UI 看的就是这个字段）。
                UpdateTaskState(task, newState);

                var context = run.Context;
                if (context != null)
                {
                    if (context.StepOutputs.TryGetValue(task.Name, out var oldResult) && oldResult != null
                        && !ReferenceEquals(oldResult, result))
                    {
                        _previousOutputs.Add(oldResult);
                    }
                    context.StepOutputs[task.Name] = result;
                }

                EnqueueStatusNotification(task.Name, newState, result);

                if (newState == WorkflowState.Completed)
                {
                    if (run.RunToTaskName != null && task.Name == run.RunToTaskName)
                    {
                        run.WorkflowTcs.TrySetResult(true);
                        return;
                    }
                    ScheduleDownstream(run, task.Name);
                }
                else
                {
                    SkipDownstream(run, task.Name);
                }
            }
            catch (Exception ex)
            {
                // 这里的两步顺序不能颠倒、也不能被日志的异常打断：TrySetException 是"本帧一定收尾"的唯一保证。
                LogSafely($"调度器内部错误: {DescribeException(ex)}");
                run.WorkflowTcs.TrySetException(ex);
            }
            finally
            {
                if (Interlocked.Decrement(ref run.RemainingTaskCount) == 0)
                {
                    run.WorkflowTcs.TrySetResult(true);
                }
            }
        }

        /// <summary>
        /// 执行步骤体并取走结果：整段持有本步骤的互斥门（见 <see cref="_stepGates"/>）。
        /// 取消只可能发生在等待门期间（令牌已取消时 <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/>
        /// 直接抛出）：此时本步骤没有执行，按"已取消"返回，由调用方走下游跳过分支。
        /// </summary>
        /// <param name="context">本帧的上下文（步骤体写入结果的目标容器）</param>
        /// <param name="token">本帧的取消令牌</param>
        /// <param name="task">要执行的步骤</param>
        /// <param name="serial">串行模式的日志前缀，仅影响调试输出</param>
        private async Task<(WorkflowState State, InspectionStepResult Result)> ExecuteTaskAsync(
            WorkflowExecutionContext context, CancellationToken token, IWorkflowTask task, bool serial = false)
        {
            // 已声明"每帧状态自持"的步骤不需要门（两帧可以同时在跑同一个步骤，这正是多帧收益的来源）。
            // 非 BaseWorkflowTask 的自定义实现一律按"需要门"处理（保守：宁可串行也不能静默出错）。
            var gate = RequiresStepGate(task)
                ? _stepGates.GetOrAdd(task.Name, _ => new SemaphoreSlim(1, 1))
                : null;
            if (gate != null)
            {
                try
                {
                    await gate.WaitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return (WorkflowState.Cancelled, null);
                }
            }

            try
            {
                var taskStartTime = Stopwatch.GetTimestamp();
                WorkflowState newState;
                try
                {
                    newState = await task.ExecuteAsync(context, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    newState = WorkflowState.Cancelled;
                }
                catch (Exception ex)
                {
                    LogSafely($"{(serial ? "串行任务 " : "任务 ")}{task.Name} 异常: {DescribeException(ex)}");
                    newState = WorkflowState.Failed;
                }

                var elapsed = (Stopwatch.GetTimestamp() - taskStartTime) * 1000.0 / Stopwatch.Frequency;
                Debug.WriteLine(serial
                    ? $"[串行] {task.Name} 完成，耗时: {elapsed:F1}ms"
                    : $"[{DateTime.Now:HH:mm:ss.fff}] 任务 {task.Name} 完成，耗时: {elapsed:F1}ms (线程:{Thread.CurrentThread.ManagedThreadId})");

                return (newState, TakeResult(context, task));
            }
            finally
            {
                gate?.Release();
            }
        }

        /// <summary>
        /// 该步骤是否需要步骤级互斥门（见 <see cref="_stepGates"/>）。
        /// </summary>
        private static bool RequiresStepGate(IWorkflowTask task) =>
            task is BaseWorkflowTask baseTask ? !baseTask.SupportsFrameConcurrency : true;

        #region 共享参数突变检测

        /// <summary>
        /// 是否检测"帧内改动了跨帧复用的参数对象"（默认开）。
        /// 关掉可省下每帧每参数一次的字段比对（微秒级），现场排障时建议保持开启。
        /// </summary>
        public bool DetectSharedParameterMutation { get; set; } = true;

        /// <summary>
        /// 帧内改动了**跨帧复用**的参数对象时触发；消息形如 <c>[栅线提取] BCGridLineParam.Wins</c>
        /// （第一次出现即报，之后同一步骤同一字段不再重复刷屏）。
        /// <para>
        /// 为什么要有它：把"每帧产物"写进跨帧复用的参数对象，是**不报错、不崩溃**的写法 ——
        /// 每制程并行跑多帧时表现为"结果/界面偶尔不对"，或者界面拿到已被释放的 HObject。
        /// 这类字段靠人工 review 很容易漏（本仓库 2026-09 一次全仓审计才找出 4~5 处），
        /// 因此由引擎在帧边界自动比对，让新写的模块**第一次跑就被指名道姓地报出来**。
        /// </para>
        /// <para>
        /// 修法二选一：① 该字段确实是每帧产物 → 标注 <see cref="FrameScopedAttribute"/>，
        /// 并保证读取方从帧上下文取（<c>StepOutputs[步骤名]</c> / <c>InspectionStepResult.FramePayload</c>）；
        /// ② 干脆把数据搬进帧上下文，参数对象只留配方与只读快照。
        /// </para>
        /// </summary>
        public event Action<string> OnSharedParameterMutated;

        /// <summary>已报过的"参数.字段"，避免每帧重复刷屏。</summary>
        private readonly ConcurrentDictionary<string, byte> _reportedParameterMutations = new();

        /// <summary>成员访问器缓存（按类型；反射只做一次）。</summary>
        private static readonly ConcurrentDictionary<Type, ParameterMember[]> _parameterMemberCache = new();

        /// <summary>参数对象内展开的层数（1 = 只看参数自己的成员；2 = 再看一层嵌套参数对象）。</summary>
        private const int MaxParameterDepth = 2;

        /// <summary>单个参数对象最多跟踪的成员数（防御异常深/宽的对象图）。</summary>
        private const int MaxMembersPerParameter = 4000;

        /// <summary>
        /// 取本帧开始时的共享参数快照（<c>Parameters1</c> 里的每个参数对象逐成员取值）。
        /// 采用**分层比较**：标量/字符串比值；数组与集合只比"引用 + 长度"；HObject/HTuple 等只比引用。
        /// 因此成本只与**字段个数**有关（这几百个），与配方里那些大数组的元素规模无关。
        /// </summary>
        private List<SharedParameterEntry> SnapshotSharedParameters(WorkflowExecutionContext context)
        {
            var snapshot = new List<SharedParameterEntry>();
            var parameters = context?.Parameters1;
            if (parameters == null) return snapshot;

            foreach (var pair in parameters)
            {
                var target = pair.Value;
                if (target == null) continue;

                snapshot.Add(new SharedParameterEntry($"参数[{pair.Key}]", false, false, false, target, -1));
                foreach (var member in GetParameterMembers(target.GetType()))
                {
                    object value;
                    try { value = member.Get(target); }
                    catch { continue; }   // 取不到的成员（索引器/懒加载异常）直接跳过，不为诊断功能添麻烦

                    var length = member.IsCollection ? GetCollectionLength(value) : -1;
                    snapshot.Add(new SharedParameterEntry($"参数[{pair.Key}].{member.Path}", member.FrameScoped,
                        member.IsScalar, member.IsCollection, value, length));
                }
            }
            return snapshot;
        }

        /// <summary>
        /// 比对帧首/帧尾快照，报出"未标注 <see cref="FrameScopedAttribute"/> 却在帧内被改写"的字段。
        /// 标量比值；集合比"引用变化或长度变化"；其它比引用（HObject/HTuple 被换掉即报）。
        /// </summary>
        private void ReportSharedParameterMutations(WorkflowRun run, List<SharedParameterEntry> before)
        {
            if (before == null || before.Count == 0) return;

            var after = SnapshotSharedParameters(run.Context);
            for (var i = 0; i < before.Count && i < after.Count; i++)
            {
                var oldEntry = before[i];
                var newEntry = after[i];
                if (!string.Equals(oldEntry.Path, newEntry.Path, StringComparison.Ordinal)) continue;
                if (oldEntry.FrameScoped) continue;   // 已声明"每帧字段"，不报

                bool mutated;
                if (oldEntry.IsScalar)
                    mutated = !Equals(oldEntry.Value, newEntry.Value);
                else if (oldEntry.IsCollection)
                    mutated = !ReferenceEquals(oldEntry.Value, newEntry.Value) || oldEntry.Length != newEntry.Length;
                else
                    mutated = !ReferenceEquals(oldEntry.Value, newEntry.Value);

                // "空 → 非空"是首次加载/懒补齐（例如首帧从配方把基准点读进参数），不是每帧改写，不算突变。
                if (mutated && oldEntry.Value == null && newEntry.Value != null) mutated = false;

                if (!mutated) continue;
                if (!_reportedParameterMutations.TryAdd(newEntry.Path, 0)) continue;   // 同类只报一次

                // 路径本身就带步骤名（Parameters1 以步骤名为键），例如：参数[栅线提取].Wins
                var message = newEntry.Path;
                try { OnSharedParameterMutated?.Invoke(message); } catch { }
                LogSafely($"帧内改动了跨帧复用的参数：{message}"
                    + "（三种改法：① 该字段确实每帧变 → 标注 FrameScopedAttribute 并让读取方走帧上下文；"
                    + "② 数据该由帧上下文承载 → 搬进 StepOutputs/InspectionStepResult.FramePayload；"
                    + "③ 只是每帧把同样的值重建成新集合 → 改生产者：没变就别换引用，配方字段不要每帧 new）");
            }
        }

        /// <summary>按类型构建成员访问器（缓存）：标量、集合单独记；嵌套参数对象再展开一层。</summary>
        private static ParameterMember[] GetParameterMembers(Type type) =>
            _parameterMemberCache.GetOrAdd(type, t => BuildMembers(t, string.Empty, 0, new HashSet<Type>()));

        private static ParameterMember[] BuildMembers(Type type, string prefix, int depth, HashSet<Type> visited)
        {
            var members = new List<ParameterMember>();
            if (type == null || depth > MaxParameterDepth || !visited.Add(type)) return members.ToArray();

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
                var getter = property;
                AddMember(members, prefix, property.Name, property.PropertyType,
                    target => getter.GetValue(target),
                    property.GetCustomAttribute<FrameScopedAttribute>() != null, depth, visited);
                if (members.Count > MaxMembersPerParameter) break;
            }
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var captured = field;
                AddMember(members, prefix, field.Name, field.FieldType,
                    target => captured.GetValue(target),
                    field.GetCustomAttribute<FrameScopedAttribute>() != null, depth, visited);
                if (members.Count > MaxMembersPerParameter) break;
            }
            return members.ToArray();
        }

        private static void AddMember(List<ParameterMember> members, string prefix, string name, Type memberType,
            Func<object, object> getter, bool frameScoped, int depth, HashSet<Type> visited)
        {
            var path = prefix.Length == 0 ? name : $"{prefix}.{name}";
            if (IsScalar(memberType))
            {
                members.Add(new ParameterMember { Path = path, FrameScoped = frameScoped, IsScalar = true, Get = getter });
                return;
            }
            if (IsCollection(memberType))
            {
                members.Add(new ParameterMember { Path = path, FrameScoped = frameScoped, IsCollection = true, Get = getter });
                return;
            }
            if (depth >= MaxParameterDepth || IsOpaqueReference(memberType))
            {
                // 叶子引用（HObject/HTuple 等）或被截断的层级：只比引用
                members.Add(new ParameterMember { Path = path, FrameScoped = frameScoped, Get = getter });
                return;
            }

            // 再展开一层（例如参数对象内嵌的子参数对象：MainLineParam / AdjColorNode …）
            var childVisited = new HashSet<Type>(visited);
            foreach (var child in BuildMembers(memberType, path, depth + 1, childVisited))
            {
                var childGet = child.Get;
                members.Add(new ParameterMember
                {
                    Path = child.Path,
                    FrameScoped = frameScoped || child.FrameScoped,
                    IsScalar = child.IsScalar,
                    IsCollection = child.IsCollection,
                    Get = target => childGet(getter(target))
                });
            }
        }

        private static bool IsScalar(Type type)
        {
            var t = Nullable.GetUnderlyingType(type) ?? type;
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)
                || t == typeof(DateTime) || t == typeof(TimeSpan) || t == typeof(Guid);
        }

        private static bool IsCollection(Type type)
        {
            if (type.IsArray) return true;
            if (typeof(System.Collections.ICollection).IsAssignableFrom(type)) return true;
            return type.GetInterfaces().Any(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICollection<>));
        }

        private static bool IsOpaqueReference(Type type) =>
            typeof(IDisposable).IsAssignableFrom(type)
            || type.Namespace != null && type.Namespace.StartsWith("HalconDotNet", StringComparison.Ordinal);

        private static int GetCollectionLength(object value)
        {
            if (value == null) return -1;
            if (value is Array array) return array.Length;
            if (value is System.Collections.ICollection collection) return collection.Count;
            return -1;
        }

        #endregion 共享参数突变检测

        /// <summary>
        /// 把异常转成日志文本。为什么必须防御：**异常对象本身可能不可读**——实测 HALCON 的
        /// <c>HTupleAccessException</c>（元组类型错配、对象已释放等状态）连 <c>ToString()</c> 都会抛。
        /// 引擎的错误路径只要因此再抛一次异常，<c>WorkflowTcs</c> 就永远拿不到完成信号，
        /// 现场表现是**整帧永久卡住**（而不是"失败然后跳过下游"）。所以这里绝不允许再抛。
        /// </summary>
        private static string DescribeException(Exception ex)
        {
            try { return ex?.Message ?? "<无异常信息>"; }
            catch { return "<异常对象不可读>"; }
        }

        /// <summary>日志写入同样不允许把异常带进错误路径（Debug.WriteLine 走的是可插拔监听器）。</summary>
        private static void LogSafely(string message)
        {
            try { Debug.WriteLine(message); }
            catch { }
        }

        /// <summary>
        /// 取走本次执行的结果。
        /// 两条来源：① 步骤体自己写进 <c>context.StepOutputs[自己名字]</c> 的那一份——这是**多帧安全**
        /// 的路径（每帧一个容器），也是"模块把每帧结果从共享 Param 挪进帧上下文"的迁移入口；
        /// ② 任务实例上的"最新结果"（历史路径）。
        /// ②走 <see cref="BaseWorkflowTask.TakeLatestResult"/> 把字段**取走**（置空）：结果发布到帧上下文后，
        /// 所有权归那一帧（由帧上下文统一释放）；步骤体下次执行开头的 <c>_latestResult?.Dispose()</c>
        /// 若还能看到上一帧的那一份，就会把它释放掉——并发跑帧时表现为随机的访问冲突。
        /// 非 <see cref="BaseWorkflowTask"/> 的自定义实现没有该能力，退回只读取值（保持兼容）。
        /// </summary>
        private static InspectionStepResult TakeResult(WorkflowExecutionContext context, IWorkflowTask task)
        {
            // 先把实例上的"最新结果"取走（置空），无论最终用哪一份——否则它会被下次执行开头的 Dispose 释放，
            // 或者反过来被误当成别人的结果。
            var latest = task is BaseWorkflowTask baseTask ? baseTask.TakeLatestResult() : task.GetLatestResult();

            if (context != null && context.StepOutputs.TryGetValue(task.Name, out var written) && written != null)
                return written;

            return latest;
        }

        /// <summary>
        /// 串行执行模式：按拓扑顺序逐个执行任务。
        /// 每个任务独占执行，内存增量可精确归属到单个步骤。
        /// </summary>
        private async Task RunSerialAsync(WorkflowRun run, List<IWorkflowTask> readyTasks)
        {
            // 用队列维护就绪任务，每次取一个执行
            var readyQueue = new Queue<IWorkflowTask>(readyTasks);

            while (readyQueue.Count > 0)
            {
                if (run.Token.IsCancellationRequested || run.IsCancelled) break;

                var task = readyQueue.Dequeue();
                UpdateTaskState(task, WorkflowState.Running);
                EnqueueStatusNotification(task.Name, WorkflowState.Running);

                // 执行任务
                var (newState, result) = await ExecuteTaskAsync(run.Context, run.Token, task, serial: true).ConfigureAwait(false);
                UpdateTaskState(task, newState);

                // 存储结果
                if (run.Context.StepOutputs.TryGetValue(task.Name, out var oldResult) && oldResult != null
                    && !ReferenceEquals(oldResult, result))
                    _previousOutputs.Add(oldResult);
                run.Context.StepOutputs[task.Name] = result;

                EnqueueStatusNotification(task.Name, newState, result);
                Interlocked.Decrement(ref run.RemainingTaskCount);

                if (newState == WorkflowState.Completed)
                {
                    // 如果是目标任务，直接结束
                    if (run.RunToTaskName != null && task.Name == run.RunToTaskName)
                        return;

                    // 将新就绪的下游任务加入队列
                    if (_downstreamDependencies.TryGetValue(task.Name, out var children))
                    {
                        foreach (var childName in children)
                        {
                            if (_taskNameToIndex.TryGetValue(childName, out var idx))
                            {
                                if (Interlocked.Decrement(ref run.CurrentInDegrees[idx]) == 0
                                    && _tasks.TryGetValue(childName, out var childTask))
                                {
                                    UpdateTaskState(childTask, WorkflowState.Ready);
                                    readyQueue.Enqueue(childTask);
                                }
                            }
                        }
                    }
                }
                else
                {
                    // 任务失败/取消：跳过下游（alwaysRun 的任务仍入队）
                    if (_downstreamDependencies.TryGetValue(task.Name, out var children))
                    {
                        var stack = new Stack<string>(children);
                        while (stack.Count > 0)
                        {
                            var childName = stack.Pop();
                            if (!_tasks.TryGetValue(childName, out var childTask)) continue;

                            if (_alwaysRunTasks.Contains(childName))
                            {
                                if (_taskNameToIndex.TryGetValue(childName, out var idx)
                                    && Interlocked.Decrement(ref run.CurrentInDegrees[idx]) == 0)
                                {
                                    UpdateTaskState(childTask, WorkflowState.Ready);
                                    readyQueue.Enqueue(childTask);
                                }
                                continue;
                            }

                            if (childTask.State != WorkflowState.Pending && childTask.State != WorkflowState.Ready) continue;
                            UpdateTaskState(childTask, WorkflowState.Skipped);
                            EnqueueStatusNotification(childTask.Name, WorkflowState.Skipped, null, "上游失败已跳过");
                            Interlocked.Decrement(ref run.RemainingTaskCount);

                            if (_downstreamDependencies.TryGetValue(childName, out var grandchildren))
                                foreach (var name in grandchildren) stack.Push(name);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 调度下游任务：当任务完成时，检查并启动所有就绪的子任务
        /// 【性能优化】使用数组预分配和批量调度
        /// </summary>
        /// <param name="run">本次运行（帧级状态）</param>
        /// <param name="parentTaskName">父任务名称</param>
        private void ScheduleDownstream(WorkflowRun run, string parentTaskName)
        {
            // 早期退出：已取消或没有下游任务
            if (run.IsCancelled || run.Token.IsCancellationRequested) return;
            if (!_downstreamDependencies.TryGetValue(parentTaskName, out var children)) return;

            // 多个父任务会同时到达这里，缓冲区必须属于本次调用。
            var readyTasks = new List<IWorkflowTask>(children.Count);

            // 遍历所有子任务，检查入度
            foreach (var childName in children)
            {
                if (!_taskNameToIndex.TryGetValue(childName, out var idx)) continue;

                // 【关键逻辑】原子递减入度，当入度为0时表示所有依赖已完成
                if (Interlocked.Decrement(ref run.CurrentInDegrees[idx]) == 0
                    && _tasks.TryGetValue(childName, out var childTask)
                    && !run.IsCancelled)
                {
                    readyTasks.Add(childTask);
                }
            }

            // 批量启动所有就绪的任务
            if (readyTasks.Count > 0)
            {
#if DEBUG
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {parentTaskName} 完成，触发 {readyTasks.Count} 个下游任务");
#endif
                foreach (var task in readyTasks)
                {
                    ScheduleTask(run, task);
                }
            }
        }

        /// <summary>
        /// 跳过下游任务：当任务失败或取消时，递归标记所有下游任务为已跳过
        /// 对于标记为始终运行的任务，改为递减其入度并在就绪时正常调度
        /// 【性能优化】使用迭代替代递归，避免栈溢出
        /// </summary>
        /// <param name="run">本次运行（帧级状态）</param>
        /// <param name="parentTaskName">父任务名称</param>
        private void SkipDownstream(WorkflowRun run, string parentTaskName)
        {
            if (_downstreamDependencies.TryGetValue(parentTaskName, out var children))
            {
                // 使用栈实现迭代深度优先遍历，替代递归调用
                var stack = new Stack<string>(children);
                while (stack.Count > 0)
                {
                    var childName = stack.Pop();
                    if (!_tasks.TryGetValue(childName, out var childTask)) continue;

                    // 始终运行的任务：递减入度，入度归零时正常调度，不标记为跳过
                    if (_alwaysRunTasks.Contains(childName))
                    {
                        if (_taskNameToIndex.TryGetValue(childName, out var idx)
                            && Interlocked.Decrement(ref run.CurrentInDegrees[idx]) == 0
                            && !run.IsCancelled)
                        {
                            ScheduleTask(run, childTask);
                        }
                        continue;
                    }

                    // 与登记任务互斥，防止多个失败父节点重复跳过同一个子节点，
                    // 或把已经开始执行的子节点同时计为 Skipped 和 Completed。
                    lock (run.SchedulingLock)
                    {
                        if (run.ScheduledTaskNames.Contains(childName)
                            || (childTask.State != WorkflowState.Pending && childTask.State != WorkflowState.Ready))
                            continue;
                        UpdateTaskState(childTask, WorkflowState.Skipped);
                    }
                    EnqueueStatusNotification(childTask.Name, WorkflowState.Skipped, null, "上游失败已跳过");

                    // 递减剩余任务数
                    if (Interlocked.Decrement(ref run.RemainingTaskCount) == 0)
                        run.WorkflowTcs.TrySetResult(true);

                    // 将当前任务的下游任务加入栈，继续处理
                    if (_downstreamDependencies.TryGetValue(childName, out var grandchildren))
                        foreach (var name in grandchildren) stack.Push(name);
                }
            }
        }

        #region 高性能辅助方法

        /// <summary>
        /// 将状态通知加入队列
        /// 【性能优化】批量处理通知，减少线程切换和事件调用开销
        /// </summary>
        private void EnqueueStatusNotification(string taskName, WorkflowState state, InspectionStepResult result = null, string msg = null)
        {
            var handler = OnTaskStatusChanged;
            if (handler == null) return;

            var status = new WorkflowTaskStatus
            {
                TaskName = taskName,
                State = state,
                LatestStepResult = result,
                StatusMessage = msg
            };

            _statusQueue.Enqueue(status);

            // 使用 CAS 确保同时只有一个线程在处理通知队列
            if (Interlocked.CompareExchange(ref _notifyPending, 1, 0) == 0)
            {
                ThreadPool.UnsafeQueueUserWorkItem(_ => FlushStatusNotifications(), null);
            }
        }

        /// <summary>
        /// 批量刷新状态通知：从队列中取出所有通知并触发事件
        /// 【性能优化】双重检查锁机制，防止丢失通知
        /// </summary>
        private void FlushStatusNotifications()
        {
            var handler = OnTaskStatusChanged;
            if (handler == null)
            {
                Interlocked.Exchange(ref _notifyPending, 0);
                return;
            }

            do
            {
                // 处理队列中的所有通知
                while (_statusQueue.TryDequeue(out var status))
                {
                    try
                    {
                        handler.Invoke(status);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"状态通知异常: {DescribeException(ex)}");
                    }
                }

                // 重置标志
                Interlocked.Exchange(ref _notifyPending, 0);

                // 双重检查：防止在重置标志后有新通知入队而被丢失
            } while (!_statusQueue.IsEmpty && Interlocked.CompareExchange(ref _notifyPending, 1, 0) == 0);
        }

        /// <summary>
        /// 通知工作流完成状态
        /// </summary>
        /// <param name="run">本次运行（帧级状态）</param>
        /// <param name="time">工作流执行总耗时（毫秒）</param>
        private void NotifyFinishStatus(WorkflowRun run, long time)
        {
            var handler = OnTaskFinishStatus;
            if (handler != null)
            {
                // 仅使用本次运行写入的输出结果构建快照，过滤未执行任务
                var snapshot = run.Context.StepOutputs
                    .Where(kv => kv.Value != null)
                    .Select(kv =>
                    {
                        _tasks.TryGetValue(kv.Key, out var task);
                        return new WorkflowTaskStatus
                        {
                            TaskName = kv.Key,
                            State = task?.State ?? WorkflowState.Pending,
                            LatestStepResult = kv.Value
                        };
                    })
                    .ToList();

                // 异步通知，不阻塞主流程
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    FlushStatusNotifications(); // 先刷新剩余通知
                    handler.Invoke(snapshot, time);
                }, null);
            }
            else
            {
                // 即使没有处理程序也要刷新队列，避免内存泄漏
                ThreadPool.UnsafeQueueUserWorkItem(_ => FlushStatusNotifications(), null);
            }
        }

        /// <summary>
        /// 重置工作流状态：将所有任务重置为Pending，重新计算入度
        /// </summary>
        /// <param name="run">本次运行（帧级状态）</param>
        private void ResetWorkflowState(WorkflowRun run)
        {
            //_context.StepOutputs.Clear();

            // 先释放"再上一帧"的旧结果（此时 UI 已渲染完，可以安全释放）。
            // 释放窗口仍是两帧：本帧开始时释放上上帧的，本帧结果要到下下帧开始才释放。
            while (_previousOutputs.TryTake(out var old))
                old?.Dispose();

            // 帧隔离：把刚结束那一帧的结果移入"待释放"，再清空 StepOutputs。
            // 这样本帧的读者（UI 的 GetStepResult / 下游统计）只会看到**本帧已完成**的步骤，
            // 不会把上一帧的结果当成新结果（未执行/被跳过的步骤表现为缺失，而不是读到旧数据）。
            // 注意顺序：必须先释放再搬移，否则会把刚展示过的那一帧结果提前一帧释放。
            var stepOutputs = run.Context?.StepOutputs;
            if (stepOutputs != null)
            {
                foreach (var kvp in stepOutputs)
                {
                    if (kvp.Value != null)
                        _previousOutputs.Add(kvp.Value);
                }
                stepOutputs.Clear();
            }

            // 释放延迟下来的旧模板图（UI 在线换模板时，帧内不释放，改在这里统一释放）。
            // 多帧并发下必须先确认"没有别的帧在飞"：旧模板图可能正被另一帧的算子读着，
            // 这时释放它就是访问冲突（单帧时天然满足——下一帧开始时上一帧已经全部退出）。
            // 被跳过的那些在最后一帧退出时补放（见 StartWorkflowAsync 的 finally）。
            if (_activeRuns.Count <= 1)
                run.Context?.ReleaseDeferredImages();

            // 复用入度数组而非每帧分配
            int count = _tasks.Count;
            if (run.CurrentInDegrees == null || run.CurrentInDegrees.Length != count)
                run.CurrentInDegrees = new int[count];
            else
                Array.Clear(run.CurrentInDegrees, 0, count);

            foreach (var task in _tasks.Values)
            {
                UpdateTaskState(task, WorkflowState.Pending);
                // 计算任务的依赖数量（即入度），优先用 Count 属性避免 LINQ 分配
                int dependencyCount = (task.Dependencies as IList<string>)?.Count
                    ?? task.Dependencies?.Count() ?? 0;

                if (_taskNameToIndex.TryGetValue(task.Name, out var idx))
                {
                    run.CurrentInDegrees[idx] = dependencyCount;
                }
            }
        }

        /// <summary>
        /// 更新任务状态
        /// </summary>
        private void UpdateTaskState(IWorkflowTask task, WorkflowState state) => task.UpdateState(state);

        /// <summary>
        /// 设置任务为已取消状态
        /// </summary>
        /// <param name="run">
        /// 本次运行（帧级状态）。任务状态本身是引擎级的（任务对象由 AddTask 注册、跨帧复用），
        /// 这里带 run 只为保持"运行期状态一律显式传入"的统一签名。
        /// </param>
        /// <param name="task">要标记为已取消的任务</param>
        private void SetTaskCancelled(WorkflowRun run, IWorkflowTask task)
        {
            UpdateTaskState(task, WorkflowState.Cancelled);
            EnqueueStatusNotification(task.Name, WorkflowState.Cancelled, null, "已超时取消");
        }

        #endregion 高性能辅助方法

        #region 公共接口

        /// <summary>
        /// 执行单个任务：不考虑依赖关系，直接执行指定任务
        /// </summary>
        /// <param name="taskName">要执行的任务名称</param>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task ExecuteSingleTaskAsync(string taskName, CancellationToken cancellationToken)
        {
            if (!_tasks.TryGetValue(taskName, out var task))
                throw new ArgumentException($"未找到任务 '{taskName}'。");

            EnqueueStatusNotification(task.Name, WorkflowState.Running);
            try
            {
                var (newState, result) = await ExecuteTaskAsync(_context, cancellationToken, task).ConfigureAwait(false);
                UpdateTaskState(task, newState);
                if (_context.StepOutputs.TryGetValue(task.Name, out var oldResult) && oldResult != null
                    && !ReferenceEquals(oldResult, result))
                    _previousOutputs.Add(oldResult);
                _context.StepOutputs[task.Name] = result;
                EnqueueStatusNotification(task.Name, newState, result);
            }
            catch (Exception ex)
            {
                EnqueueStatusNotification(task.Name, WorkflowState.Failed, null, DescribeException(ex));
            }
            finally
            {
                FlushStatusNotifications();
            }
        }

        /// <summary>
        /// 获取所有任务的当前状态
        /// </summary>
        /// <returns>任务状态列表</returns>
        public IEnumerable<WorkflowTaskStatus> GetTaskStatuses()
        {
            return _tasks.Values.Select(t => new WorkflowTaskStatus
            {
                TaskName = t.Name,
                State = t.State,
                LatestStepResult = t.GetLatestResult()
            }).ToList();
        }

        /// <summary>
        /// 获取所有缺陷检测算法名称
        /// </summary>
        /// <returns>算法名称集合（去重）</returns>
        public DefectNameCollection GetDefectAlgorithmName(bool isCustom = false)
        {
            const string recipeEditorDetectionKey = "自定义算法";
            var tasks = _tasks.Values.AsEnumerable();
            if (isCustom)
            {
                tasks = tasks.Where(t => !string.Equals(t.Name, recipeEditorDetectionKey, StringComparison.Ordinal));
            }
            return DefectNameCollection.Merge(tasks.Select(t => t.GetDefectAlgorithmName()));
        }

        /// <summary>
        /// 将指定任务标记为始终运行：即使其上游依赖中有任务失败，该任务仍会被调度执行
        /// </summary>
        /// <param name="taskName">任务名称</param>
        /// <exception cref="ArgumentException">任务不存在时抛出</exception>
        public void SetTaskAlwaysRun(string taskName)
        {
            if (!_tasks.ContainsKey(taskName))
                throw new ArgumentException($"未找到任务 '{taskName}'。");
            _alwaysRunTasks.Add(taskName);
        }

        /// <summary>
        /// 设置工作流上下文参数
        /// </summary>
        public void SetParameter(string key, BaseParam value) => _context.SetParameter(key, value);

        /// <summary>
        /// 获取工作流上下文参数
        /// </summary>
        public T GetParameter<T>(string key) => _context.GetParameter<T>(key);

        /// <summary>
        /// 释放资源：清理任务、上下文和相关资源
        /// </summary>
        public void Dispose()
        {
            // 取消所有还在等待的运行（原来是单个 _workflowTcs）
            foreach (var run in _activeRuns.Keys)
                run.WorkflowTcs?.TrySetCanceled();

            // 先刷新通知队列，确保所有 HObject 资源被正确释放
            FlushStatusNotifications();

            // 释放暂存的旧结果
            while (_previousOutputs.TryTake(out var old))
                old?.Dispose();

            // 释放上下文（内部会释放 StepOutputs 中所有 InspectionStepResult 的 HObject 资源）
            _context?.Dispose();
            _context = null;

            _tasks.Clear();
            _downstreamDependencies.Clear();
            _taskNameToIndex.Clear();
        }

        #endregion 公共接口
    }
}
