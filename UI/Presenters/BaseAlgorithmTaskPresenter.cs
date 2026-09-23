using HalconDotNet;
using MstImagelib;
using MstPicbox;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using VisionTool.Interfaces;
using VisionTool.Models;

namespace VisionTool.UI.Presenters
{
	/// <summary>
	/// 所有算法任务子Presenter的抽象基类。
	/// 
	/// 该类提供了算法检测任务的MVP(Model-View-Presenter)模式实现，负责：
	/// - 管理View和Model之间的交互
	/// - 处理用户界面事件
	/// - 协调任务执行和结果显示
	/// - 管理任务参数的更新和保存
	/// 
	/// 子类应该继承此基类并实现特定的算法检测逻辑。
	/// </summary>
	public abstract class BaseAlgorithmTaskPresenter : IAlgorithmTaskPresenter
	{
		/// <summary>
		/// 关联的View接口实例，用于与UI层交互
		/// </summary>
		protected IAlgorithmTaskView _view;

		/// <summary>
		/// 服务上下文，提供对任务参数、结果和图像等数据的访问
		/// </summary>
		protected InspectionServiceContext _serviceContext;

		/// <summary>
		/// 持有View的实际UIElement实例
		/// </summary>
		protected UIElement _viewControl;

		/// <summary>
		/// 标记Presenter是否已初始化，确保初始化仅执行一次
		/// </summary>
		private bool _isInitialized;

		private bool _isActive = false;

		/// <summary>
		/// 任务的名称标识符
		/// </summary>
		public string TaskName { get; private set; }

		/// <inheritdoc/>
		public virtual bool CanSaveParameters => true;

		/// <summary>
		/// 主视图的 PicboxControl 引用（由主 Presenter 注入）。
		/// 非 null 时，ShowImage 统一操作主控件而非子视图本地控件。
		/// </summary>
		public PicboxControl? MainPicboxControl { get; set; }

		/// <summary>
		/// 当前持有的显示图像副本，用于生命周期管理
		/// </summary>
		private HObject _currentDisplayImage;

		/// <summary>
		/// 绑定到视图的**参数副本**（惰性创建，随 Presenter 存活）。
		/// 为什么缓存：① 视图是"就地改对象属性"的编辑方式，若每次刷新都给一份新副本，
		/// 用户正在输入的改动会被下一次刷新覆盖；② 深拷贝是 JSON 往返，不能每帧做一遍。
		/// 改动经视图 ParameterChanged → UpdateAlgorithmParameter 提交（加锁原子替换，下一帧生效）。
		/// </summary>
		private BaseParam _editableParameters;

		#region 公共方法

		/// <summary>
		/// 加载指定路径的图像
		/// </summary>
		/// <param name="path">图像文件路径</param>
		/// <returns>加载的HObject图像对象，若实现则返回，默认返回null</returns>
		/// <remarks>该方法作为待实现接口，子类可根据需要重写此方法以实现具体的图像加载逻辑</remarks>
		public HObject LoadImage(string path)
		{
			return null;
		}

		/// <summary>
		/// 显示离线加载的图片（管理传入图像的生命周期）
		/// </summary>
		/// <param name="imageHObject">待显示的Halcon图像对象（调用方转移所有权）</param>
		public void ShowImage(HObject imageHObject)
		{
			// 释放上一次持有的图像副本
			_currentDisplayImage?.Dispose();
			_currentDisplayImage = imageHObject;

			if (MainPicboxControl != null)
			{
				_view.ShowImage(imageHObject, MainPicboxControl);
			}
		}

		/// <summary>
		/// 初始化Presenter
		/// 此方法应仅调用一次，用于设置服务上下文和初始数据
		/// </summary>
		/// <param name="serviceContext">提供数据访问的服务上下文</param>
		/// <exception cref="ArgumentNullException">当serviceContext为null时抛出</exception>
		public void Initialize(InspectionServiceContext serviceContext)
		{
			// 确保初始化仅执行一次
			if (_isInitialized) return;

			_serviceContext = serviceContext ?? throw new ArgumentNullException(nameof(serviceContext));
			_isInitialized = true;

			// 初始化时使用最新数据更新View
			UpdateViewWithLatestData();
		}

		/// <summary>
		/// 激活Presenter
		/// 当对应的View被激活时调用，用于刷新UI数据
		/// </summary>
		public virtual void Activate()
		{
			_isActive = true;
		}

		/// <summary>
		/// 去激活Presenter
		/// 当对应的View被隐藏/切换时调用，用于清理临时状态
		/// </summary>
		public virtual void Deactivate()
		{
			_isActive = false;
		}

		/// <summary>
		/// 获取关联的View控件
		/// </summary>
		/// <returns>UIElement实例</returns>
		public UIElement GetViewControl() => _viewControl;

		/// <summary>
		/// 处理任务状态更新
		/// 确保所有UI更新在UI线程上执行，并通知View更新显示内容
		/// </summary>
		/// <param name="status">包含任务状态和结果的WorkflowTaskStatus对象</param>
		public virtual void HandleTaskStatusUpdate(WorkflowTaskStatus status)
		{
			if (!_isActive)
				return;
			// 确保UI更新在UI线程上进行
			if (!_viewControl.Dispatcher.CheckAccess())
			{
				_viewControl.Dispatcher.Invoke(new Action(() => HandleTaskStatusUpdate(status)));
				return;
			}

			// 更新状态消息
			_view.SetStatusMessage($"{status.State}: {status.StatusMessage}");

			// 如果有最新的步骤结果，则更新View
			if (status.LatestStepResult != null)
			{
				_view.UpdateStepSpecificData(status.LatestStepResult);
			}

			// 注意：以下代码被注释，原因是避免频繁的图像更新请求
			// 如需启用，应在任务完成时才请求更新图像，而非每次状态改变时
			// if (_viewControl.Parent != null && (status.State == Enums.WorkflowState.Completed || status.State == Enums.WorkflowState.Failed || status.State == Enums.WorkflowState.Skipped || status.State == Enums.WorkflowState.Cancelled))
			// {
			//     OnRequestMainImageUpdate(this, EventArgs.Empty);
			// }
		}

		/// <summary>
		/// 获取实际的IAlgorithmTaskView实例
		/// viewControl 本身实现了 IAlgorithmTaskView 接口
		/// </summary>
		/// <param name="viewControl">WPF UIElement 实例</param>
		public static IAlgorithmTaskView GetActualView(UIElement viewControl)
		{
			return (IAlgorithmTaskView)viewControl;
		}

		/// <summary>
		/// 释放Presenter的资源
		/// 解除所有事件订阅并清理关联的View控件
		/// </summary>
		/// <remarks>
		/// 建议在容器销毁Presenter时调用此方法，以避免内存泄漏。
		/// </remarks>
		public void Dispose()
		{
			// 释放持有的图像副本
			_currentDisplayImage?.Dispose();
			_currentDisplayImage = null;

			// 解除View的所有事件订阅
			_view.ParameterChanged -= OnParameterChanged;
			_view.RunToThisTaskClicked -= OnRunToThisTaskClicked;
			_view.LoadAndRunToThisTaskClicked -= OnLoadAndRunToThisTaskClicked;
			_view.LoadAndRunToThisTasksClicked -= OnLoadAndRunToThisTasksClicked;
			_view.SaveParameter -= SaveParameterClicked;

			// 释放UserControl及其子控件
			(_viewControl as IDisposable)?.Dispose();

			// 输出调试信息
			Debug.WriteLine($"Presenter {TaskName} 已释放。");
		}

		#endregion

		#region 事件

		/// <summary>
		/// 请求主界面更新图像显示事件
		/// </summary>
		public event Action<HObject, List<HObject>> OnRequestMainImageDisplay;

		/// <summary>
		/// 请求运行到当前任务的事件
		/// </summary>
		public event Action<string> OnRunToThisTaskRequested;

		/// <summary>
		/// 请求加载图像并运行到当前任务的事件
		/// </summary>
		public event Action<string> OnLoadAndRunToThisTaskRequested;

		/// <summary>
		/// 参数保存事件
		/// </summary>
		public event Action<KeyValuePair<string, BaseParam>> SaveParameter;

		/// <summary>
		/// 返回上一步操作的事件
		/// </summary>
		public event Action<string> OnBackRequested;

		public event Action<string, string[]> OnLoadAndRunToThisTaskRequesteds;

		#endregion

		#region 构造函数

		/// <summary>
		/// 初始化Presenter的构造函数
		/// </summary>
		/// <param name="taskName">任务名称，用于标识任务和事件的关联</param>
		/// <param name="view">实现IAlgorithmTaskView接口的View对象</param>
		/// <param name="viewControl">关联的Windows Forms UserControl实例</param>
		/// <exception cref="ArgumentNullException">当view或viewControl为null时抛出</exception>
		protected BaseAlgorithmTaskPresenter(string taskName, IAlgorithmTaskView view, UIElement viewControl)
		{
			TaskName = taskName;
			_view = view ?? throw new ArgumentNullException(nameof(view));
			_viewControl = viewControl ?? throw new ArgumentNullException(nameof(viewControl));

			// 订阅View的事件
			_view.ParameterChanged += OnParameterChanged;
			_view.RunToThisTaskClicked += OnRunToThisTaskClicked;
			_view.BackButtonClicked += OnBackButtonClicked;
			_view.LoadAndRunToThisTaskClicked += OnLoadAndRunToThisTaskClicked;
			_view.LoadAndRunToThisTasksClicked += OnLoadAndRunToThisTasksClicked;
			_view.SaveParameter += SaveParameterClicked;
		}

		#endregion

		#region 事件处理器

		/// <summary>
		/// 子界面"检测"按钮的事件处理器
		/// 触发向主Presenter传递的事件以执行当前任务
		/// </summary>
		/// <param name="sender">事件发送者</param>
		/// <param name="e">事件参数</param>
		private void OnRunToThisTaskClicked(object sender, EventArgs e)
		{
			OnRunToThisTaskRequested?.Invoke(TaskName);
		}

		/// <summary>
		/// 子界面"离线检测"按钮的事件处理器
		/// 触发向主Presenter传递的事件以加载图像并执行当前任务
		/// </summary>
		/// <param name="sender">事件发送者</param>
		/// <param name="e">事件参数</param>
		private void OnLoadAndRunToThisTaskClicked(object sender, EventArgs e)
		{
			OnLoadAndRunToThisTaskRequested?.Invoke(TaskName);
		}

		/// <summary>
		/// 子界面"离线检测"按钮的事件处理器
		/// 触发向主Presenter传递的事件以加载图像并执行当前任务
		/// </summary>
		/// <param name="sender">事件发送者</param>
		/// <param name="e">事件参数</param>
		private void OnLoadAndRunToThisTasksClicked(object sender, string[] filePaths)
		{
			OnLoadAndRunToThisTaskRequesteds?.Invoke(TaskName, filePaths);
		}

		/// <summary>
		/// "返回"按钮的事件处理器
		/// </summary>
		/// <param name="sender">事件发送者</param>
		/// <param name="e">事件参数</param>
		private void OnBackButtonClicked(object sender, EventArgs e)
		{
			OnBackRequested?.Invoke(TaskName);
		}

		/// <summary>
		/// 参数保存事件的处理器
		/// </summary>
		/// <param name="sender">事件发送者</param>
		/// <param name="e">包含参数名和参数值的KeyValuePair</param>
		private void SaveParameterClicked(object sender, KeyValuePair<string, BaseParam> e)
		{
			SaveParameter?.Invoke(e);
		}

		#endregion

		#region 保护方法

		/// <summary>
		/// 使用服务上下文中的最新数据更新View
		/// </summary>
		/// <remarks>
		/// 此方法获取：
		/// - 任务的所有参数并在View中显示
		/// - 当前模式的图像进行显示
		/// - 任务的执行结果进行显示
		/// </remarks>
		protected virtual void UpdateViewWithLatestData()
		{
			if (_serviceContext == null) return;

			// 获取任务参数并更新View
			// 用缓存的**副本**绑定视图：既不每帧深拷贝，也不会把用户正在改的输入刷掉
			var parameters = _editableParameters ??= _serviceContext.GetEditableTaskParameters();
			_view.UpdateParameters(parameters);

			if (MainPicboxControl != null)
			{
				ShowImage(_serviceContext.GetModeImage());
			}

			// 获取并显示任务结果
			var result = _serviceContext.GetTaskResult();
			if (result != null)
			{
				// 任务已执行且有结果
				_view.SetStatusMessage($"{result.EndTimestamp.ToShortTimeString()} - {result.Message}");
				_view.UpdateStepSpecificData(result);
			}
			else
			{
				// 任务未执行或无结果
				_view.SetStatusMessage("尚未执行或无结果");
				_view.UpdateStepSpecificData(new InspectionStepResult());
			}
		}

		/// <summary>
		/// 处理View中参数改变的事件
		/// </summary>
		/// <param name="sender">事件发送者</param>
		/// <param name="e">包含参数名和参数值的KeyValuePair</param>
		/// <remarks>
		/// 此方法负责：
		/// - 将参数变更同步到服务上下文
		/// - 更新UI提示信息
		/// </remarks>
		protected virtual void OnParameterChanged(object sender, KeyValuePair<string, BaseParam> e)
		{
			if (_serviceContext == null) return;

			// 更新服务上下文中的参数
			_serviceContext.UpdateAlgorithmParameter(e.Key, e.Value);

			// 更新UI提示用户参数已改变
			_view.SetStatusMessage($"参数 '{e.Key}' 已改变为 '{e.Value}'");
		}

		/// <summary>
		/// 请求更新主界面的图像显示
		/// 从任务结果中提取调试对象(图像和叠加信息)，并触发显示事件
		/// </summary>
		/// <param name="sender">事件发送者</param>
		/// <param name="e">事件参数</param>
		protected virtual void OnRequestMainImageUpdate(object sender, EventArgs e)
		{
			// 验证服务上下文是否已初始化，若未初始化则直接返回
			if (_serviceContext == null) return;

			// 从服务上下文中获取当前任务的执行结果
			var result = _serviceContext.GetTaskResult();

			// 只读一次列表：结果的 Dispose（Pipeline 线程）会把该字段原子换出为 null，
			// 分成多次读可能在中途变成 null（NRE）或换出成另一份列表。
			var debugObjects = result?.DebugObjects;

			// 检查任务结果是否存在且包含有效的调试对象
			if (debugObjects != null && debugObjects.Count > 0)
			{
				// 获取第一个已初始化的HObject作为要显示的主图像
				HObject imageToDisplay = debugObjects.FirstOrDefault(o => o.IsInitialized()) as HObject;

				// 过滤并复制所有已初始化的调试对象作为叠加层进行显示
				// 注意：这些副本的所有权转移给订阅者（OnRequestMainImageDisplay），由界面负责释放——
				// 目前调用链上没有释放点，属于潜在泄漏；因为本路径当前无写入方（DebugObjects 恒空），
				// 暂不改造，接线时请一并补上释放。
				List<HObject> overlaysToDisplay = debugObjects.Where(o => o != null && o.IsInitialized())
					.Select(o => o.CopyObj(1, -1)).ToList();

				// 触发事件，通知主界面更新显示的图像及其叠加层
				OnRequestMainImageDisplay?.Invoke(imageToDisplay, overlaysToDisplay);
			}
			else
			{
				// 若无有效的任务结果或调试对象，则通知主界面清除图像显示
				OnRequestMainImageDisplay?.Invoke(null, null);
			}
		}

		#endregion
	}
}
