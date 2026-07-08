using HalconDotNet;
using System;
using System.Collections.Generic;
using VisionTool.Enums;

namespace VisionTool.Models
{
	/// <summary>
	/// 工作流执行上下文，用于在任务之间传递共享数据。
	/// </summary>
	/// <remarks>
	/// 该类负责管理工作流执行过程中的所有图像资源和参数数据。
	/// 包含模板图像、当前图像、多通道图像集合以及任务执行参数等。
	/// 实现了 IDisposable 接口，确保 HObject 图像资源能够正确释放，避免内存泄漏。
	/// </remarks>
	public class WorkflowExecutionContext : IDisposable
	{
		/// <summary>
		/// 运行图像类型，指定当前工作流使用的图像来源。
		/// </summary>
		/// <value>默认值为 RunType.当前图</value>
		public RunType RunImageType { get; set; } = RunType.当前图;

		/// <summary>
		/// 模板图像的私有字段。
		/// </summary>
		private HObject _modeImage;

		public string Path = "";

		/// <summary>
		/// 获取或设置模板图像。
		/// </summary>
		/// <remarks>
		/// 设置新图像时会自动释放旧图像资源，防止内存泄漏。
		/// 模板图像通常用作参考标准或比对基准。
		/// </remarks>
		public HObject ModeImage
		{
			get => _modeImage;
			set
			{
				// 确保旧图像被释放
				_modeImage?.Dispose();
				_modeImage = value;
			}
		}

		/// <summary>
		/// 当前图像的私有字段。
		/// </summary>
		private HObject _currentImage;

		/// <summary>
		/// 获取或设置当前正在处理的图像。
		/// </summary>
		/// <remarks>
		/// 注意：此属性只保存对外部图像的引用，不负责图像的生命周期管理。
		/// 图像的释放由调用者（PrecessRun）负责，避免多线程环境下的意外释放问题。
		/// </remarks>
		public HObject CurrentImage
		{
			get => _currentImage;
			set
			{
				// 注意：不要在这里释放图像！
				// 图像的生命周期由调用者(PrecessRun)管理
				// 在多线程环境下，上一个cell的图像可能还在被其他线程使用（存图、UI显示）
				_currentImage = value;
			}
		}

		/// <summary>
		/// 图像集合的私有字段。
		/// </summary>
		private readonly HObjectMap _image = [];

		/// <summary>
		/// 获取或设置当前图像集合（多通道图像）。
		/// </summary>
		/// <remarks>
		/// 通常用于存储四个通道的图像数据（如 RGBA 或其他多通道格式）。
		/// 设置新集合时会自动释放旧集合中所有图像的资源。
		/// 字典的键为通道名称，值为对应的 HObject 图像对象。
		/// </remarks>
		public HObjectMap Image
		{
			get => _image;
			set
			{
				_image.SetAll(value);
			}
		}

		/// <summary>
		/// 获取或设置共享参数字典。
		/// </summary>
		/// <remarks>
		/// 用于存储工作流执行过程中的临时数据和共享变量。
		/// 键为参数名称，值为任意类型的对象。
		/// </remarks>
		public Dictionary<string, object> Parameters { get; set; }

		/// <summary>
		/// 获取或设置所有任务的参数配置。
		/// </summary>
		/// <remarks>
		/// 存储每个任务的参数对象，继承自 BaseParam。
		/// 键为任务标识符，值为对应的参数配置对象。
		/// </remarks>
		public Dictionary<string, BaseParam> Parameters1 { get; set; }

		/// <summary>
		/// 获取或设置每个任务步骤的执行结果。
		/// </summary>
		/// <remarks>
		/// 键为任务步骤的标识符，值为该步骤的执行结果对象。
		/// 包含了任务的调试对象、图像、区域等输出数据。
		/// </remarks>
		public Dictionary<string, InspectionStepResult> StepOutputs { get; set; }

		/// <summary>
		/// 初始化 WorkflowExecutionContext 类的新实例。
		/// </summary>
		/// <param name="initialImage">初始模板图像，将被设置为 ModeImage。</param>
		/// <remarks>
		/// 构造函数会初始化所有字典属性，准备好用于工作流执行。
		/// </remarks>
		public WorkflowExecutionContext(HObject initialImage)
		{
			ModeImage = initialImage;
			Parameters = new Dictionary<string, object>();
			StepOutputs = new Dictionary<string, InspectionStepResult>();
			Parameters1 = new Dictionary<string, BaseParam>();
		}

		/// <summary>
		/// 从共享参数字典中获取指定键的参数值，并转换为指定类型。
		/// </summary>
		/// <typeparam name="T">期望返回的参数类型。</typeparam>
		/// <param name="key">参数的键名。</param>
		/// <returns>
		/// 如果找到该键，返回转换后的参数值；
		/// 如果未找到或转换失败，返回类型 T 的默认值。
		/// </returns>
		/// <remarks>
		/// 此方法使用 Convert.ChangeType 进行类型转换。
		/// 对于复杂类型或转换失败的情况，可能需要额外的错误处理。
		/// </remarks>
		public T GetParameter<T>(string key)
		{
			if (Parameters.TryGetValue(key, out object value))
			{
				// 注意：这里需要考虑类型转换失败的情况，实际应用中应更健壮
				return (T)Convert.ChangeType(value, typeof(T)); // 使用 Convert.ChangeType 处理常见类型转换
			}
			return default(T);
		}

		/// <summary>
		/// 设置或更新指定键的任务参数。
		/// </summary>
		/// <param name="key">参数的键名（通常为任务标识符）。</param>
		/// <param name="value">要设置的参数对象，必须继承自 BaseParam。</param>
		/// <remarks>
		/// 如果键已存在，将覆盖原有值；如果不存在，将添加新的键值对。
		/// </remarks>
		public void SetParameter(string key, BaseParam value)
		{
			Parameters1[key] = value;
		}

		/// <summary>
		/// 释放工作流执行上下文使用的所有非托管资源。
		/// </summary>
		/// <remarks>
		/// 该方法会释放以下资源：
		/// <list type="bullet">
		/// <item><description>当前图像 (CurrentImage)</description></item>
		/// <item><description>所有步骤输出中的调试对象 (DebugObjects)</description></item>
		/// <item><description>所有步骤输出中的图像对象 (Img)</description></item>
		/// <item><description>所有步骤输出中的区域对象 (Reg)</description></item>
		/// <item><description>所有步骤输出中的其他对象 (Other)</description></item>
		/// </list>
		/// 建议在工作流执行完成后及时调用此方法以释放内存。
		/// 注意：CurrentImage 不在此处释放，因为它是外部传入的引用，由调用者管理生命周期。
		/// </remarks>
		public void Dispose()
		{
			// 注意：不释放 CurrentImage，它是外部传入的引用，由调用者(PrecessRun)管理
			_currentImage = null;

			// 释放模板图像
			_modeImage?.Dispose();
			_modeImage = null;

			foreach (var kvp in StepOutputs)
			{
				kvp.Value?.Dispose();
			}
			StepOutputs.Clear();
		}
	}
}