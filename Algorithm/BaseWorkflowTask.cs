using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VisionTool.Enums;
using VisionTool.Interfaces;
using VisionTool.Models;

namespace VisionTool.Algorithm
{
    /// <summary>
    /// 工作流任务的抽象基类，提供通用实现和状态管理。
    /// </summary>
    public abstract class BaseWorkflowTask : IWorkflowTask
    {
        /// <summary>
        /// 任务名称
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// 任务状态
        /// </summary>
        public WorkflowState State { get; private set; }

        /// <summary>
        /// 依赖任务名称
        /// </summary>
        public IEnumerable<string> Dependencies { get; private set; }

        /// <summary>
        /// 存储任务的最新结果
        /// </summary>
        protected InspectionStepResult _latestResult;

        protected BaseWorkflowTask(string name, IEnumerable<string> dependencies = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Dependencies = dependencies ?? Enumerable.Empty<string>();
            State = WorkflowState.Pending;
            _latestResult = new InspectionStepResult { IsSuccess = false, Message = "还没有执行。" };
        }

        /// <summary>
        /// 执行任务
        /// </summary>
        /// <param name="context"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public abstract Task<WorkflowState> ExecuteAsync(WorkflowExecutionContext context, CancellationToken cancellationToken);

        /// <summary>
        /// 本步骤体是否已经做到"每帧状态自持"：结果只写进**本帧上下文**（<c>context.StepOutputs[Name]</c>），
        /// 且不依赖实例字段保存任何与帧有关的数据。
        /// <para>
        /// false（默认）时，引擎会用**步骤级互斥门**把"同一个实例被两帧同时执行"串起来——因为
        /// <c>_latestResult</c>、算法实例、模块内的帧内缓存都是实例级的，两帧同时进同一个步骤体必然互相踩，
        /// 结果是静默出错。代价是两帧在同名步骤上无法并行（多帧收益被这个门封顶）。
        /// </para>
        /// <para>
        /// true 表示该步骤已按帧隔离，引擎不再串行它——这是多帧真正拿到吞吐的地方。
        /// 改这个标记之前必须先做到：① 结果写本帧上下文；② 模块里"每帧结果"不再落在共享 Param 上
        /// （清单见主程序 <c>AlgorithmParallel.FrameConcurrency</c>）；③ 实例字段两帧共用是安全的。
        /// </para>
        /// </summary>
        public virtual bool SupportsFrameConcurrency => false;

        /// <summary>
        /// 获取任务结果
        /// </summary>
        /// <returns></returns>
        public InspectionStepResult GetLatestResult()
        {
            return _latestResult;
        }

        /// <summary>
        /// 取走最新结果：返回当前结果并把字段置空（原子交换）。
        /// 为什么需要：引擎把结果发布到**帧上下文**后，结果的所有权归那一帧（由该帧上下文统一释放），
        /// 而步骤体下次执行的开头会 <c>_latestResult?.Dispose()</c>。不置空的话，第二帧执行同一实例时
        /// 会释放掉第一帧仍在被读的结果（HALCON 里是访问冲突，不是托管异常）。取走后两帧各持有自己那一份。
        /// 字段为空时返回一个"还没有执行"占位结果（与构造时的语义一致），保证下游拿到的结果对象非空。
        /// </summary>
        public InspectionStepResult TakeLatestResult()
        {
            return Interlocked.Exchange(ref _latestResult, null)
                ?? new InspectionStepResult { IsSuccess = false, Message = "还没有执行。" };
        }

        /// <summary>
        /// 更新任务状态
        /// </summary>
        /// <param name="newState"></param>
        public void UpdateState(WorkflowState newState)
        {
            State = newState;
        }

        /// <summary>
        ///  获取所有缺陷算法名
        /// </summary>
        /// <returns></returns>
        public abstract DefectNameCollection GetDefectAlgorithmName();

        /// <summary>
        /// 任务初始化
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public abstract bool Initialize(string path = null);

        /// <summary>
        /// 创建任务的默认参数对象
        /// 默认实现返回基础参数类型，派生类应重写此方法返回其特定的参数类型
        /// </summary>
        /// <returns>任务所需类型的参数对象</returns>
        public virtual BaseParam CreateDefaultParameters()
        {
            return new BaseParam();
        }
    }
}