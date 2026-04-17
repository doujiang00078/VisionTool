using HalconDotNet;
using System;
using System.Collections.Generic;
using VisionTool.Models;

namespace VisionTool.Interfaces
{
    /// <summary>
    /// 所有算法任务独立子界面的通用接口。
    /// 子界面本身是UserControl，但通过此接口与Presenter通信。
    /// </summary>
    public interface IAlgorithmTaskView
    {
        /// <summary>
        /// 更新界面上的参数显示
        /// </summary>
        /// <param name="parameters">要显示的参数对象</param>
        void UpdateParameters(BaseParam parameters);

        /// <summary>
        /// 更新检测步骤的特定数据（如检测结果、统计信息等）
        /// </summary>
        /// <param name="data">检测步骤结果数据</param>
        void UpdateStepSpecificData(InspectionStepResult data);

        /// <summary>
        /// 在界面上显示图像
        /// </summary>
        /// <param name="imaHObject">Halcon图像对象</param>
        void ShowImage(HObject imaHObject);

        /// <summary>
        /// 设置界面上的状态消息
        /// </summary>
        /// <param name="message">要显示的状态消息文本</param>
        void SetStatusMessage(string message);

        /// <summary>
        /// 当界面上的参数发生变化时触发此事件
        /// </summary>
        /// <remarks>事件参数：Key=参数名称，Value=新的参数值</remarks>
        event EventHandler<KeyValuePair<string, BaseParam>> ParameterChanged;

        /// <summary>
        /// 请求主界面更新图像显示（显示该任务的DebugObjects）
        /// </summary>
        /// <remarks>当需要在主界面的图像显示区域更新当前任务的调试图像时触发</remarks>
        event EventHandler RequestMainImageUpdate;

        /// <summary>
        /// “运行到此”按钮点击事件
        /// </summary>
        /// <remarks>从流程开始执行到当前任务</remarks>
        event EventHandler RunToThisTaskClicked;

        /// <summary>
        /// “加载并运行到此”按钮点击事件
        /// </summary>
        /// <remarks>先加载图像，然后从流程开始执行到当前任务</remarks>
        event EventHandler LoadAndRunToThisTaskClicked;

        event EventHandler<string[]> LoadAndRunToThisTasksClicked;

        /// <summary>
        /// 保存参数事件
        /// </summary>
        /// <remarks>事件参数：Key=参数名称，Value=要保存的参数值</remarks>
        event EventHandler<KeyValuePair<string, BaseParam>> SaveParameter;

        /// <summary>
        /// 返回上一级点击事件
        /// 当用户点击"返回上一级"按钮时触发
        /// </summary>
        event EventHandler BackButtonClicked;
    }
}