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
        /// 获取任务结果
        /// </summary>
        /// <returns></returns>
        public InspectionStepResult GetLatestResult()
        {
            return _latestResult;
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