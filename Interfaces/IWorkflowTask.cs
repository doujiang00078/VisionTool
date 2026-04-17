using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VisionTool.Enums;
using VisionTool.Models;

namespace VisionTool.Interfaces
{
    /// <summary>
    /// 定义工作流中可执行任务的接口
    /// </summary>
    public interface IWorkflowTask
    {
        /// <summary>
        /// 任务名称
        /// </summary>
        string Name { get; }

        /// <summary>
        /// 任务状态
        /// </summary>
        WorkflowState State { get; }

        /// <summary>
        /// 依赖任务名称
        /// </summary>
        IEnumerable<string> Dependencies { get; }

        /// <summary>
        /// 执行任务的异步方法。
        /// </summary>
        /// <param name="context">工作流上下文，包含共享数据和参数</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>任务最终状态</returns>
        Task<WorkflowState> ExecuteAsync(WorkflowExecutionContext context, CancellationToken cancellationToken);

        /// <summary>
        /// 获取任务执行的详细结果，用于调试或可视化。
        /// </summary>
        InspectionStepResult GetLatestResult();

        /// <summary>
        /// 更新任务的状态。通常由 WorkflowEngine 调用。
        /// </summary>
        void UpdateState(WorkflowState newState);

        /// <summary>
        /// 获取所有缺陷算法名
        /// </summary>
        /// <returns></returns>
        DefectNameCollection GetDefectAlgorithmName();

        /// <summary>
        /// 初始化
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        bool Initialize(string path = null);

        /// <summary>
        /// 创建任务的默认参数对象
        /// 每个任务可以根据自己的需求返回相应类型的参数实例
        /// </summary>
        /// <returns>任务所需类型的参数对象</returns>
        BaseParam CreateDefaultParameters();
    }
}