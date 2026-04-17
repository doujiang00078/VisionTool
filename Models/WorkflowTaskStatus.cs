using VisionTool.Enums;

namespace VisionTool.Models
{
    /// <summary>
    /// 工作流中单个任务的当前状态信息。
    /// </summary>
    public class WorkflowTaskStatus
    {
        /// <summary>
        /// 任务名称
        /// </summary>
        public string TaskName { get; set; }

        /// <summary>
        /// 任务状态
        /// </summary>
        public WorkflowState State { get; set; }

        /// <summary>
        /// 额外状态信息
        /// </summary>
        public string StatusMessage { get; set; }

        /// <summary>
        /// 最近一次执行的详细结果
        /// </summary>
        public InspectionStepResult LatestStepResult { get; set; }

        // Override ToString() for ListBox display
        public override string ToString()
        {
            return $"{TaskName} [{State}]";
        }
    }
}