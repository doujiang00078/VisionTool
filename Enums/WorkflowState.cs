namespace VisionTool.Enums
{
    /// <summary>
    /// 工作流任务的状态
    /// </summary>
    public enum WorkflowState
    {
        /// <summary>
        /// 待执行（已注册，但依赖未满足）
        /// </summary>
        Pending,

        /// <summary>
        /// 可执行（所有依赖已满足）
        /// </summary>
        Ready,

        /// <summary>
        /// 正在执行
        /// </summary>
        Running,

        /// <summary>
        /// 执行成功
        /// </summary>
        Completed,

        /// <summary>
        /// 执行失败
        /// </summary>
        Failed,

        /// <summary>
        /// 被跳过（上游任务失败或被取消导致）
        /// </summary>
        Skipped,

        /// <summary>
        /// 被取消
        /// </summary>
        Cancelled
    }
}