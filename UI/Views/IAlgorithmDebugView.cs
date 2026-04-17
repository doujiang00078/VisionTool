using HalconDotNet;
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using VisionTool.Models;

namespace VisionTool.UI.Views
{
    /// <summary>
    /// 定义算法调试主视图的接口。
    /// </summary>
    public interface IAlgorithmDebugView
    {
        /// <summary>
        /// 设置任务状态信息
        /// </summary>
        /// <param name="taskName">任务名称</param>
        /// <param name="status">任务状态</param>
        /// <param name="message">状态消息</param>
        void SetTaskStatus(string taskName, string status, string message);

        /// <summary>
        /// 更新任务步骤状态
        /// </summary>
        /// <param name="tasks">任务状态集合</param>  
        void UpdateTaskSteps(IEnumerable<WorkflowTaskStatus> tasks);

        /// <summary>
        /// 主图像显示
        /// </summary>
        /// <param name="image">要显示的图像对象</param>
        /// <param name="overlays">叠加层图形对象列表，用于显示检测结果等附加信息</param>
        void DisplayMainImage(HObject image, List<InspectionStepResult> overlays = null);

        /// <summary>
        /// 主图像显示
        /// </summary>
        /// <param name="image">要显示的图像对象</param>
        /// <param name="overlays">叠加层图形对象列表，用于显示检测结果等附加信息</param>
        void DisplayMainImage(HObject image, List<HObject> overlays);

        /// <summary>
        /// 显示整体检测结果
        /// </summary>
        /// <param name="result">检测结果信息</param>
        void DisplayOverallResult(string result);

        /// <summary>
        /// 显示消息对话框
        /// </summary>
        /// <param name="message">消息内容</param>
        /// <param name="title">对话框标题</param>
        void ShowMessage(string message, string title);

        /// <summary>
        /// 清除并显示新的子任务视图
        /// </summary>
        /// <param name="taskViewControl">要显示的任务详情视图控件</param>
        void ShowTaskDetailView(UserControl taskViewControl);

        /// <summary>
        /// 清除当前显示的子任务视图
        /// </summary>
        void ClearTaskDetailView();

        /// <summary>
        /// 展示所有任务完成的消息
        /// </summary>
        /// <param name="obj">所有任务的状态集合</param>
        void ShowAllTaskFinishMessage(IEnumerable<WorkflowTaskStatus> obj, long time);

        /// <summary>
        /// 返回到离线调试界面   
        /// </summary>
        /// <param name="taskName"></param>
        /// <returns></returns>
        void OnBackRequested(string taskName);

        /// <summary>
        /// 加载图片
        /// </summary>
        event EventHandler LoadImageClicked;

        /// <summary>
        /// 开始检测
        /// </summary>
        event EventHandler StartDebugClicked;

        /// <summary>
        /// 停止运行
        /// </summary>
        event EventHandler StopDebugClicked;

        /// <summary>
        /// 运行到指定任务
        /// </summary>
        event EventHandler<string> RunToStepClicked;

        /// <summary>
        ///  运行单步执行
        /// </summary>
        event EventHandler<string> ExecuteSingleStepClicked;

        /// <summary>
        /// 任务列表选中事件
        /// </summary>
        event EventHandler<string> TaskSelected;

        // ParameterChanged 事件将由子任务视图处理

        /// <summary>
        /// 保存所有参数事件
        /// </summary>
        event EventHandler SaveAllParameter;

        /// <summary>
        /// 是否使用当前图像（勾选复选框时返回 true）
        /// </summary>
        bool UseCurrentImage { get; }
    }
}