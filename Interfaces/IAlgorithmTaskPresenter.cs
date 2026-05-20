using HalconDotNet;
using System;
using System.Collections.Generic;
using VisionTool.Models;

namespace VisionTool.Interfaces
{
	/// <summary>
	/// 所有算法任务独立子界面的Presenter通用接口。
	/// </summary>
	public interface IAlgorithmTaskPresenter : IDisposable
	{
		/// <summary>
		/// 获取任务名称
		/// </summary>
		string TaskName { get; }

		/// <summary>
		/// 初始化 Presenter，并提供初始数据。
		/// </summary>
		void Initialize(InspectionServiceContext serviceContext);

		/// <summary>
		/// 当该任务被选中时，通知Presenter激活并获取最新数据。
		/// </summary>
		void Activate();

		/// <summary>
		/// 当该任务不再被选中时，通知Presenter去激活。
		/// </summary>
		void Deactivate();

		/// <summary>
		/// 当其关联任务的状态或结果更新时，由主Presenter调用。
		/// </summary>
		void HandleTaskStatusUpdate(WorkflowTaskStatus status);

		/// <summary>
		/// 加载图像
		/// </summary>
		/// <param name="path">路径</param>
		/// <returns></returns>
		HObject LoadImage(string path);

		/// <summary>
		/// 显示图像
		/// </summary>
		/// <param name="imageHObject"></param>
		void ShowImage(HObject imageHObject);

		/// <summary>
		/// 请求主界面显示当前任务的图像和叠加对象。
		/// </summary>
		event Action<HObject, List<HObject>> OnRequestMainImageDisplay;

		/// <summary>
		/// 请求运行到此任务
		/// </summary>
		event Action<string> OnRunToThisTaskRequested;

		/// <summary>
		/// 请求加载图片并运行到此任务
		/// </summary>
		event Action<string> OnLoadAndRunToThisTaskRequested;

		/// <summary>
		/// 请求加载多张图片并运行到此任务
		/// </summary>
		event Action<string, string[]> OnLoadAndRunToThisTaskRequesteds;

		/// <summary>
		/// 保存参数
		/// </summary>
		event Action<KeyValuePair<string, BaseParam>> SaveParameter;

		/// <summary>
		/// 返回上一级
		/// </summary>
		event Action<string> OnBackRequested;

		/// <summary>
		/// 指示是否允许在保存配方时保存该模块的参数。
		/// 若返回 false，主模块执行"保存配方"时将跳过该模块的参数。
		/// </summary>
		bool CanSaveParameters { get; }
	}

	/// <summary>
	/// 注入给IAlgorithmTaskPresenter的上下文，避免直接依赖整个InspectionService
	/// </summary>
	public class InspectionServiceContext
	{
		/// <summary>
		/// 检测服务实例
		/// </summary>
		private readonly Services.InspectionService _inspectionService;

		/// <summary>
		/// 任务名称
		/// </summary>
		private readonly string _taskName;

		/// <summary>
		/// 初始化检测服务上下文
		/// </summary>
		/// <param name="inspectionService">检测服务实例</param>
		/// <param name="taskName">任务名称</param>
		public InspectionServiceContext(Services.InspectionService inspectionService, string taskName)
		{
			_inspectionService = inspectionService;
			_taskName = taskName;
		}

		/// <summary>
		/// 获取任务参数
		/// </summary>
		/// <returns>任务算法参数</returns>
		public BaseParam GetTaskParameters(string taskName = "")
		{
			// 实际中可能需要根据 TaskName 过滤参数
			return _inspectionService.GetTaskAlgorithmParameters(taskName == "" ? _taskName : taskName);
		}

		/// <summary>
		/// 获取任务结果
		/// </summary>
		/// <returns>检测步骤结果</returns>
		public InspectionStepResult GetTaskResult(string taskName = "")
		{
			return _inspectionService.GetTaskResult(taskName == "" ? _taskName : taskName);
		}

		/// <summary>
		/// 更新算法参数
		/// </summary>
		/// <param name="name">参数名称</param>
		/// <param name="baseParam">参数对象</param>
		public void UpdateAlgorithmParameter(string name, BaseParam baseParam)
		{
			_inspectionService.UpdateAlgorithmParameter(name, baseParam);
		}

		/// <summary>
		/// 获取模式图像
		/// </summary>
		/// <returns>模式图像对象</returns>
		public HObject GetModeImage()
		{
			return _inspectionService.GetModeImage();
		}

		public Dictionary<string, HObject> GetImage()
		{
			return _inspectionService.GetImage();
		}

		// 任何子Presenter需要的InspectionService方法都可以通过这里暴露
	}
}