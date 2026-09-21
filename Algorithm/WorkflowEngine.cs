using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        /// 入度数组：记录每个任务还有多少依赖未完成
        /// 使用数组而非字典，减少查找开销，支持原子操作
        /// </summary>
        private int[] _currentInDegrees;

        /// <summary>
        /// 工作流执行上下文：存储参数和任务输出结果
        /// </summary>
        private WorkflowExecutionContext _context;

        /// <summary>
        /// 工作流完成信号：用于等待整个工作流执行完成
        /// </summary>
        private TaskCompletionSource<bool> _workflowTcs;

        /// <summary>
        /// 剩余未处理任务计数器：使用原子操作，当递减到0时工作流结束
        /// </summary>
        private int _remainingTaskCount;

        /// <summary>
        /// 状态变更通知队列：批量处理状态通知，减少线程切换开销
        /// </summary>
        private readonly ConcurrentQueue<WorkflowTaskStatus> _statusQueue = new();

        /// <summary>
        /// 通知处理标志：确保同时只有一个线程在处理通知队列
        /// </summary>
        private int _notifyPending;

        /// <summary>
        /// 取消标志：使用原子操作标记工作流是否已被取消
        /// </summary>
        private int _cancelled;

        /// <summary>
        /// 取消状态属性：简化取消检查逻辑
        /// </summary>
        private bool IsCancelled => Volatile.Read(ref _cancelled) == 1;

        /// <summary>
        /// 始终运行任务集合：即使上游任务失败，这些任务仍会被调度执行
        /// </summary>
        private readonly HashSet<string> _alwaysRunTasks = new();

        /// <summary>
        /// 登记与停止调度使用同一把锁。任务先计数再入线程池，直到其执行和下游调度
        /// 全部退出才减计数，避免 Task.WhenAll 快照遗漏后来加入的任务。
        /// </summary>
        private readonly object _taskSchedulingLock = new();
        private readonly HashSet<string> _scheduledTaskNames = new();
        private TaskCompletionSource<bool> _tasksDrained;
        private int _activeTaskCount;
        private bool _schedulingStopped;

        /// <summary>
        /// 当前帧已完成步骤快照。
        /// 只有 task completed 后才写入；下游任务从 snapshot 读取依赖结果，
        /// 避免 _context.StepOutputs 被新帧替换时影响仍在并发读取的旧任务。
        /// </summary>
        private volatile ConcurrentDictionary<string, InspectionStepResult> _snapshotOutputs = new();

        /// <summary>
        /// 上一帧的 StepOutputs，在下一帧 ResetWorkflowState 时统一释放。
        /// 解决 UI/异步消费者仍持有旧 result 引用时，ProcessTaskAsync 提前 Dispose 导致的 use-after-free。
        /// </summary>
        private readonly ConcurrentBag<InspectionStepResult> _previousOutputs = new();

        /// <summary>
        /// 强制串行执行模式：所有任务按依赖拓扑顺序逐个执行，不并行。
        /// </summary>
        public bool ForceSerialExecution { get; set; }

        #endregion 字段定义

        // 事件定义
        public event Action<WorkflowTaskStatus> OnTaskStatusChanged;

        public event Action<IEnumerable<WorkflowTaskStatus>, long> OnTaskFinishStatus;

        /// <summary>
        /// 构造函数：初始化工作流引擎
        /// </summary>
        /// <param name="context">工作流执行上下文</param>
        public WorkflowEngine(WorkflowExecutionContext context)
        {
            _tasks = new ConcurrentDictionary<string, IWorkflowTask>();
            _downstreamDependencies = new ConcurrentDictionary<string, List<string>>();
            _taskNameToIndex = new ConcurrentDictionary<string, int>();
            _context = context ?? throw new ArgumentNullException(nameof(context));

            // 【性能优化】预热线程池：设置最小线程数为CPU核心数的4倍（至少16个）
            // 避免任务启动时因线程池线程不足而产生延迟
            var minThreads = Math.Max(Environment.ProcessorCount * 4, 16);
            ThreadPool.SetMinThreads(minThreads, minThreads);
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
        /// 启动工作流执行：并行调度任务，自动处理依赖关系
        /// </summary>
        /// <param name="cancellationToken">取消令牌，用于超时或手动取消</param>
        /// <param name="runToTaskName">指定运行到某个任务，不指定则执行全部</param>
        /// <returns>异步任务，工作流完成时返回</returns>
        public async Task StartWorkflowAsync(CancellationToken cancellationToken, string runToTaskName = null, int timeoutMs = 0)
        {
            var time = Stopwatch.StartNew();

            // 只在本帧开始时已处于串行模式时保留监控器快照。
            var serialExecution = ForceSerialExecution;

            // 步骤1：重置所有任务状态，初始化入度数组
            ResetWorkflowState();

            // 步骤2：初始化工作流控制信号
            // 使用 RunContinuationsAsynchronously 防止死锁
            _workflowTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _remainingTaskCount = _tasks.Count;
            Interlocked.Exchange(ref _cancelled, 0);

            // 步骤3：注册取消回调，处理超时或手动取消
            using var reg = cancellationToken.Register(() =>
            {
                // 使用 CAS 操作确保只执行一次
                if (Interlocked.CompareExchange(ref _cancelled, 1, 0) == 0)
                {
                    // 将所有待处理和就绪的任务标记为已取消
                    foreach (var task in _tasks.Values.Where(t =>
                        t.State == WorkflowState.Pending || t.State == WorkflowState.Ready))
                    {
                        SetTaskCancelled(task);
                    }
                    _workflowTcs.TrySetCanceled();
                }
            });

            // 步骤4：寻找起始节点（入度为0的任务，即没有依赖的任务）
            var readyTasks = new List<IWorkflowTask>();
            foreach (var task in _tasks.Values)
            {
                if (_taskNameToIndex.TryGetValue(task.Name, out var idx) && _currentInDegrees[idx] == 0)
                {
                    readyTasks.Add(task);
                }
            }

            // 验证工作流是否有效
            if (readyTasks.Count == 0 && _tasks.Count > 0)
            {
                throw new InvalidOperationException("工作流存在循环依赖或无起始节点。");
            }

            // 登记根任务前初始化本帧排空信号。
            lock (_taskSchedulingLock)
            {
                _scheduledTaskNames.Clear();
                _activeTaskCount = 0;
                _schedulingStopped = false;
                _tasksDrained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            try
            {
                if (_tasks.Count == 0)
                {
                    _workflowTcs.TrySetResult(true);
                }
                else if (serialExecution)
                {
                    // 串行模式：按拓扑顺序逐个执行
                    await RunSerialAsync(readyTasks, cancellationToken, runToTaskName).ConfigureAwait(false);
                    _workflowTcs.TrySetResult(true);
                }
                else
                {
                    // 步骤5：并行启动所有起始任务
                    foreach (var task in readyTasks)
                        ScheduleTask(task, cancellationToken, runToTaskName);
                }

                // 步骤6：等待工作流完成信号
                await _workflowTcs.Task.ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                // 忽略取消异常，使用正常流程结束
            }
            finally
            {
                Task drained;
                lock (_taskSchedulingLock)
                {
                    _schedulingStopped = true;
                    if (_activeTaskCount == 0)
                        _tasksDrained.TrySetResult(true);
                    drained = _tasksDrained.Task;
                }
                // CancellationToken 无法中断已经进入 HALCON 的同步算子。
                // 即使超时，也必须等所有算子和 finally 退出，调用方才可释放本帧输入。
                await drained.ConfigureAwait(false);

                // 所有本帧任务结束后再采样
                time.Stop();
                NotifyFinishStatus(time.ElapsedMilliseconds);
                Debug.WriteLine("工作流结束。");
            }
        }

        private void ScheduleTask(IWorkflowTask task, CancellationToken token, string runToTaskName)
        {
            lock (_taskSchedulingLock)
            {
                if (_schedulingStopped || IsCancelled || token.IsCancellationRequested
                    || _workflowTcs.Task.IsCompleted || task.State == WorkflowState.Skipped
                    || !_scheduledTaskNames.Add(task.Name))
                    return;

                UpdateTaskState(task, WorkflowState.Ready);
                _activeTaskCount++;
                try
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await ProcessTaskAsync(task, token, runToTaskName).ConfigureAwait(false);
                        }
                        finally
                        {
                            CompleteScheduledTask();
                        }
                    });
                }
                catch (Exception ex)
                {
                    CompleteScheduledTask();
                    _workflowTcs.TrySetException(ex);
                }
            }
        }

        private void CompleteScheduledTask()
        {
            lock (_taskSchedulingLock)
            {
                if (--_activeTaskCount == 0 && _schedulingStopped)
                    _tasksDrained.TrySetResult(true);
            }
        }

        /// <summary>
        /// 任务执行核心方法：执行单个任务并处理后续调度
        /// 【极速模式】使用多重优化措施减少延迟
        /// </summary>
        /// <param name="task">要执行的任务</param>
        /// <param name="token">取消令牌</param>
        /// <param name="runToTaskName">目标任务名，达到后停止</param>
        private async Task ProcessTaskAsync(IWorkflowTask task, CancellationToken token, string runToTaskName)
        {
            if (_workflowTcs.Task.IsCompleted || IsCancelled) return;

            var taskStartTime = Stopwatch.GetTimestamp();

            try
            {
                if (token.IsCancellationRequested || IsCancelled)
                {
                    SetTaskCancelled(task);
                    return;
                }

                UpdateTaskState(task, WorkflowState.Running);
                EnqueueStatusNotification(task.Name, WorkflowState.Running);

                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 任务 {task.Name} 开始执行 (线程:{Thread.CurrentThread.ManagedThreadId})");

                WorkflowState newState;
                try
                {
                    newState = await task.ExecuteAsync(_context, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    newState = WorkflowState.Cancelled;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"任务 {task.Name} 异常: {ex.Message}");
                    newState = WorkflowState.Failed;
                }

                var elapsed = (Stopwatch.GetTimestamp() - taskStartTime) * 1000.0 / Stopwatch.Frequency;
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 任务 {task.Name} 完成，耗时: {elapsed:F1}ms (线程:{Thread.CurrentThread.ManagedThreadId})");

                var result = task.GetLatestResult();

                if (result != null)
                {
                    _snapshotOutputs[task.Name] = result;
                }

                var context = _context;
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
                    if (runToTaskName != null && task.Name == runToTaskName)
                    {
                        _workflowTcs.TrySetResult(true);
                        return;
                    }
                    ScheduleDownstream(task.Name, token, runToTaskName);
                }
                else
                {
                    SkipDownstream(task.Name, token, runToTaskName);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"调度器内部错误: {ex}");
                _workflowTcs.TrySetException(ex);
            }
            finally
            {
                if (Interlocked.Decrement(ref _remainingTaskCount) == 0)
                {
                    _workflowTcs.TrySetResult(true);
                }
            }
        }

        /// <summary>
        /// 串行执行模式：按拓扑顺序逐个执行任务。
        /// 每个任务独占执行，内存增量可精确归属到单个步骤。
        /// </summary>
        private async Task RunSerialAsync(List<IWorkflowTask> readyTasks, CancellationToken token, string runToTaskName)
        {
            // 用队列维护就绪任务，每次取一个执行
            var readyQueue = new Queue<IWorkflowTask>(readyTasks);

            while (readyQueue.Count > 0)
            {
                if (token.IsCancellationRequested || IsCancelled) break;

                var task = readyQueue.Dequeue();
                UpdateTaskState(task, WorkflowState.Running);
                EnqueueStatusNotification(task.Name, WorkflowState.Running);

                var taskStartTime = Stopwatch.GetTimestamp();

                // 执行任务
                WorkflowState newState;
                try
                {
                    newState = await task.ExecuteAsync(_context, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    newState = WorkflowState.Cancelled;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"串行任务 {task.Name} 异常: {ex.Message}");
                    newState = WorkflowState.Failed;
                }

                var elapsed = (Stopwatch.GetTimestamp() - taskStartTime) * 1000.0 / Stopwatch.Frequency;
                Debug.WriteLine($"[串行] {task.Name} 完成，耗时: {elapsed:F1}ms");

                // 存储结果
                var result = task.GetLatestResult();
                if (result != null)
                {
                    _snapshotOutputs[task.Name] = result;
                }
                if (_context.StepOutputs.TryGetValue(task.Name, out var oldResult) && oldResult != null
                    && !ReferenceEquals(oldResult, result))
                    _previousOutputs.Add(oldResult);
                _context.StepOutputs[task.Name] = result;

                EnqueueStatusNotification(task.Name, newState, result);
                Interlocked.Decrement(ref _remainingTaskCount);

                if (newState == WorkflowState.Completed)
                {
                    // 如果是目标任务，直接结束
                    if (runToTaskName != null && task.Name == runToTaskName)
                        return;

                    // 将新就绪的下游任务加入队列
                    if (_downstreamDependencies.TryGetValue(task.Name, out var children))
                    {
                        foreach (var childName in children)
                        {
                            if (_taskNameToIndex.TryGetValue(childName, out var idx))
                            {
                                if (Interlocked.Decrement(ref _currentInDegrees[idx]) == 0
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
                                    && Interlocked.Decrement(ref _currentInDegrees[idx]) == 0)
                                {
                                    UpdateTaskState(childTask, WorkflowState.Ready);
                                    readyQueue.Enqueue(childTask);
                                }
                                continue;
                            }

                            if (childTask.State != WorkflowState.Pending && childTask.State != WorkflowState.Ready) continue;
                            UpdateTaskState(childTask, WorkflowState.Skipped);
                            EnqueueStatusNotification(childTask.Name, WorkflowState.Skipped, null, "上游失败已跳过");
                            Interlocked.Decrement(ref _remainingTaskCount);

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
        /// <param name="parentTaskName">父任务名称</param>
        /// <param name="token">取消令牌</param>
        /// <param name="runToTaskName">目标任务名</param>
        private void ScheduleDownstream(string parentTaskName, CancellationToken token, string runToTaskName)
        {
            // 早期退出：已取消或没有下游任务
            if (IsCancelled || token.IsCancellationRequested) return;
            if (!_downstreamDependencies.TryGetValue(parentTaskName, out var children)) return;

            // 多个父任务会同时到达这里，缓冲区必须属于本次调用。
            var readyTasks = new List<IWorkflowTask>(children.Count);

            // 遍历所有子任务，检查入度
            foreach (var childName in children)
            {
                if (!_taskNameToIndex.TryGetValue(childName, out var idx)) continue;

                // 【关键逻辑】原子递减入度，当入度为0时表示所有依赖已完成
                if (Interlocked.Decrement(ref _currentInDegrees[idx]) == 0
                    && _tasks.TryGetValue(childName, out var childTask)
                    && !IsCancelled)
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
                    ScheduleTask(task, token, runToTaskName);
                }
            }
        }

        /// <summary>
        /// 跳过下游任务：当任务失败或取消时，递归标记所有下游任务为已跳过
        /// 对于标记为始终运行的任务，改为递减其入度并在就绪时正常调度
        /// 【性能优化】使用迭代替代递归，避免栈溢出
        /// </summary>
        /// <param name="parentTaskName">父任务名称</param>
        /// <param name="token">取消令牌</param>
        /// <param name="runToTaskName">目标任务名</param>
        private void SkipDownstream(string parentTaskName, CancellationToken token, string runToTaskName)
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
                            && Interlocked.Decrement(ref _currentInDegrees[idx]) == 0
                            && !IsCancelled)
                        {
                            ScheduleTask(childTask, token, runToTaskName);
                        }
                        continue;
                    }

                    // 与登记任务互斥，防止多个失败父节点重复跳过同一个子节点，
                    // 或把已经开始执行的子节点同时计为 Skipped 和 Completed。
                    lock (_taskSchedulingLock)
                    {
                        if (_scheduledTaskNames.Contains(childName)
                            || (childTask.State != WorkflowState.Pending && childTask.State != WorkflowState.Ready))
                            continue;
                        UpdateTaskState(childTask, WorkflowState.Skipped);
                    }
                    EnqueueStatusNotification(childTask.Name, WorkflowState.Skipped, null, "上游失败已跳过");

                    // 递减剩余任务数
                    if (Interlocked.Decrement(ref _remainingTaskCount) == 0)
                        _workflowTcs.TrySetResult(true);

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
                        Debug.WriteLine($"状态通知异常: {ex.Message}");
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
        /// <param name="time">工作流执行总耗时（毫秒）</param>
        private void NotifyFinishStatus(long time)
        {
            var handler = OnTaskFinishStatus;
            if (handler != null)
            {
                // 仅使用本次运行写入的输出结果构建快照，过滤未执行任务
                var snapshot = _context.StepOutputs
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
        private void ResetWorkflowState()
        {
            //_context.StepOutputs.Clear();

            // 每帧重建 snapshot，避免跨帧残留
            _snapshotOutputs = new ConcurrentDictionary<string, InspectionStepResult>();

            // 释放上一帧的旧结果（此时 UI 已完成渲染，可以安全释放）
            while (_previousOutputs.TryTake(out var old))
                old?.Dispose();

            // 复用入度数组而非每帧分配
            int count = _tasks.Count;
            if (_currentInDegrees == null || _currentInDegrees.Length != count)
                _currentInDegrees = new int[count];
            else
                Array.Clear(_currentInDegrees, 0, count);

            foreach (var task in _tasks.Values)
            {
                UpdateTaskState(task, WorkflowState.Pending);
                // 计算任务的依赖数量（即入度），优先用 Count 属性避免 LINQ 分配
                int dependencyCount = (task.Dependencies as IList<string>)?.Count
                    ?? task.Dependencies?.Count() ?? 0;

                if (_taskNameToIndex.TryGetValue(task.Name, out var idx))
                {
                    _currentInDegrees[idx] = dependencyCount;
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
        private void SetTaskCancelled(IWorkflowTask task)
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
                var newState = await task.ExecuteAsync(_context, cancellationToken).ConfigureAwait(false);
                var result = task.GetLatestResult();
                if (result != null)
                {
                    _snapshotOutputs[task.Name] = result;
                }
                if (_context.StepOutputs.TryGetValue(task.Name, out var oldResult) && oldResult != null
                    && !ReferenceEquals(oldResult, result))
                    _previousOutputs.Add(oldResult);
                _context.StepOutputs[task.Name] = result;
                EnqueueStatusNotification(task.Name, newState, result);
            }
            catch (Exception ex)
            {
                EnqueueStatusNotification(task.Name, WorkflowState.Failed, null, ex.Message);
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
            _workflowTcs?.TrySetCanceled();

            // 先刷新通知队列，确保所有 HObject 资源被正确释放
            FlushStatusNotifications();

            // 清理快照引用，避免引用上下文已释放对象
            _snapshotOutputs?.Clear();
            _snapshotOutputs = null;

            // 释放暂存的旧结果
            while (_previousOutputs.TryTake(out var old))
                old?.Dispose();

            // 释放上下文（内部会释放 StepOutputs 中所有 InspectionStepResult 的 HObject 资源）
            _context?.Dispose();
            _context = null;

            _tasks.Clear();
            _downstreamDependencies.Clear();
            _taskNameToIndex.Clear();
            if (_currentInDegrees != null)
                Array.Clear(_currentInDegrees, 0, _currentInDegrees.Length);
            _currentInDegrees = null;
        }

        #endregion 公共接口
    }
}
