using HalconDotNet;
using System;
using System.Collections.Concurrent;
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
		/// 帧上下文的创建标记：只用于把"共享构造"与公开的单参构造
		/// <see cref="WorkflowExecutionContext(HObject)"/> 在编译期区分开。
		/// </summary>
		private enum FrameContextMarker
		{
			FrameContext
		}

		/// <summary>
		/// 共享上下文：帧上下文（见 <see cref="CreateFrameContext"/>）
		/// 把长生命周期资源（模板图 ModeImage、参数、Path）的归属权交给它，自己只持有本帧的资源。
		/// null 表示本实例自己就是归属者——原有单参构造的行为完全不变。
		/// </summary>
		private readonly WorkflowExecutionContext _shared;

		/// <summary>
		/// 运行图像类型，指定当前工作流使用的图像来源。
		/// </summary>
		/// <value>默认值为 RunType.当前图</value>
		public RunType RunImageType { get; set; } = RunType.当前图;

		/// <summary>
		/// 模板图像的私有字段。
		/// </summary>
		private HObject _modeImage;

		/// <summary>
		/// 模板图替换 + 延迟释放用的锁。切换模板图是操作员动作（冷路径），用锁保证
		/// "替换 + 旧图入待释放队列"是原子的，避免并发切换丢图（泄漏）。
		/// </summary>
		private readonly object _modeImageLock = new object();

		/// <summary>
		/// 待释放的旧模板图。为什么延迟：UI 可以在线设置模板图（InspectionService.SetModeImage），
		/// 而算法线程此刻可能正在读旧模板图；立即 Dispose 会让它拿到已释放的 HObject——
		/// 在 HALCON 里表现为访问冲突（进程崩溃），而不是托管异常。
		/// 统一在**帧间**释放（WorkflowEngine.ResetWorkflowState 调用 ReleaseDeferredImages），
		/// 与 _previousOutputs 同一个释放点：此时上一帧的所有算子都已退出。
		/// </summary>
		private readonly ConcurrentQueue<HObject> _pendingImageRelease = new ConcurrentQueue<HObject>();

		/// <summary>
		/// 工作路径的私有字段（仅归属者使用；帧上下文转发给共享上下文，见 <see cref="Path"/>）。
		/// </summary>
		private string _path = "";

		/// <summary>
		/// 工作路径。帧上下文读写都转发给共享上下文——算法模块用它定位配方/配置文件，
		/// 必须与共享上下文（长生命周期的那一份）一致。
		/// </summary>
		public string Path
		{
			get => _shared != null ? _shared.Path : _path;
			set
			{
				if (_shared != null) _shared.Path = value;
				else _path = value;
			}
		}

		/// <summary>
		/// 获取或设置模板图像。
		/// </summary>
		/// <remarks>
		/// 设置新图像时**不立即释放**旧图像，而是交给帧间统一释放（见 <see cref="ReleaseDeferredImages"/>）。
		/// 模板图像通常用作参考标准或比对基准。
		/// 帧上下文读写都转发给共享上下文：模板图是长生命周期资源（一份），不是每帧一份。
		/// </remarks>
		public HObject ModeImage
		{
			get => _shared != null ? _shared.ModeImage : _modeImage;
			set
			{
				if (_shared != null)
				{
					_shared.ModeImage = value;
					return;
				}
				lock (_modeImageLock)
				{
					var previous = _modeImage;
					_modeImage = value;
					if (previous != null && !ReferenceEquals(previous, value))
						_pendingImageRelease.Enqueue(previous);
				}
			}
		}

		/// <summary>
		/// 帧间释放：把延迟下来的旧模板图统一 Dispose。由 <see cref="Algorithm.WorkflowEngine"/>
		/// 在每帧开始（上一帧所有算子已退出）时调用。可在任意线程调用。
		/// 帧上下文转发给共享上下文——延迟队列属于模板图（长生命周期资源）的所有者。
		/// </summary>
		public void ReleaseDeferredImages()
		{
			if (_shared != null)
			{
				_shared.ReleaseDeferredImages();
				return;
			}
			while (_pendingImageRelease.TryDequeue(out var image))
				image?.Dispose();
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
				if (value is null)
					_image.Clear();
				else
					_image.SetAll(value);
			}
		}

		/// <summary>
		/// 参数容器的写锁与"写时复制"快照。
		/// 为什么要这样：算法步骤在各自线程上并发读 Parameters/Parameters1（20+ 个模块都读），
		/// 而写入可能来自 UI 线程（改参数、设模板图）。普通 Dictionary 的并发读写会结构损坏，
		/// 读方甚至可能死循环——所以写入在锁内重建一份新字典后原子发布，读方永远拿到完整快照，
		/// 无需加锁（与 Models/DisposableMap 的做法一致）。
		/// </summary>
		private readonly object _parameterLock = new object();
		private Dictionary<string, object> _parameters = new Dictionary<string, object>();
		private volatile Dictionary<string, object> _parametersSnapshot = new Dictionary<string, object>();
		private Dictionary<string, BaseParam> _parameters1 = new Dictionary<string, BaseParam>();
		private volatile Dictionary<string, BaseParam> _parameters1Snapshot = new Dictionary<string, BaseParam>();

		/// <summary>
		/// 获取共享参数字典（**只读视图**，写请用 <see cref="SetObjectParameter"/>）。
		/// </summary>
		/// <remarks>
		/// 用于存储工作流执行过程中的临时数据和共享变量。
		/// 键为参数名称，值为任意类型的对象。
		/// 帧上下文转发给共享上下文：参数是长生命周期资源（一份），不随帧复制。
		/// </remarks>
		public Dictionary<string, object> Parameters => _shared != null ? _shared.Parameters : _parametersSnapshot;

		/// <summary>
		/// 获取所有任务的参数配置（**只读视图**，写请用 <see cref="SetParameter"/>）。
		/// </summary>
		/// <remarks>
		/// 存储每个任务的参数对象，继承自 BaseParam。
		/// 键为任务标识符，值为对应的参数配置对象。
		/// 注意：这里发布的是"字典结构"的快照，**参数对象本身**仍是同一个引用，
		/// 若 UI 在运行中直接改参数对象的属性，算法线程仍会读到变化（属于阶段 3 的边界问题）。
		/// 帧上下文转发给共享上下文：参数是长生命周期资源（一份），不随帧复制。
		/// </remarks>
		public Dictionary<string, BaseParam> Parameters1 => _shared != null ? _shared.Parameters1 : _parameters1Snapshot;

		/// <summary>
		/// 获取或设置每个任务步骤的执行结果。
		/// </summary>
		/// <remarks>
		/// 键为任务步骤的标识符，值为该步骤的执行结果对象。
		/// 包含了任务的调试对象、图像、区域等输出数据。
		/// </remarks>
		public ConcurrentDictionary<string, InspectionStepResult> StepOutputs { get; set; }

		/// <summary>
		/// 初始化 WorkflowExecutionContext 类的新实例。
		/// </summary>
		/// <param name="initialImage">初始模板图像，将被设置为 ModeImage。</param>
		/// <remarks>
		/// 构造函数会初始化所有字典属性，准备好用于工作流执行。
		/// 本实例即长生命周期资源的归属者（模式图/参数/路径都归自己）。
		/// </remarks>
		public WorkflowExecutionContext(HObject initialImage)
		{
			ModeImage = initialImage;
			StepOutputs = new ConcurrentDictionary<string, InspectionStepResult>();
		}

		/// <summary>
		/// 创建帧上下文：与 <paramref name="shared"/> 共享长生命周期资源，自己独占本帧的可变状态。
		/// </summary>
		/// <param name="shared">
		/// 共享上下文（通常是引擎构造时传入的那一个）。模式图、参数、工作路径都转发给它。
		/// </param>
		/// <remarks>
		/// 用途：同一 <c>WorkflowEngine</c> 实例并发跑多帧。每帧一份本实例，于是
		/// 「多通道图 <see cref="Image"/> / <see cref="CurrentImage"/> / <see cref="StepOutputs"/> /
		/// <see cref="RunImageType"/>」是每帧独立的（帧间不串数据），而
		/// 「<see cref="ModeImage"/> / <see cref="Parameters"/> / <see cref="Parameters1"/> /
		/// <see cref="Path"/>」仍只有一份（长生命周期资源的所有权在 shared 上，本实例只转发、不释放）。
		///
		/// 这里用工厂方法而不是 <c>WorkflowExecutionContext(WorkflowExecutionContext)</c> 构造函数：
		/// 两者都是"单参引用类型"，会让既有的 <c>new WorkflowExecutionContext(null)</c>
		/// （测试与主程序多处）在编译期产生二义性，破坏"现有调用方零影响"。
		/// </remarks>
		public static WorkflowExecutionContext CreateFrameContext(WorkflowExecutionContext shared)
		{
			return new WorkflowExecutionContext(shared, FrameContextMarker.FrameContext);
		}

		/// <summary>
		/// 帧上下文专用构造（私有）：见 <see cref="CreateFrameContext"/>。
		/// </summary>
		private WorkflowExecutionContext(WorkflowExecutionContext shared, FrameContextMarker _)
		{
			_shared = shared ?? throw new ArgumentNullException(nameof(shared));
			StepOutputs = new ConcurrentDictionary<string, InspectionStepResult>();
		}

		/// <summary>
		/// 写入共享参数（写时复制后原子发布）。
		/// 帧上下文转发给共享上下文：参数只有一份，帧内写入必须对同一帧的其它步骤可见。
		/// </summary>
		public void SetObjectParameter(string key, object value)
		{
			if (_shared != null)
			{
				_shared.SetObjectParameter(key, value);
				return;
			}
			lock (_parameterLock)
			{
				var copy = new Dictionary<string, object>(_parameters) { [key] = value };
				_parameters = copy;
				_parametersSnapshot = copy;
			}
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
		/// 帧上下文转发给共享上下文：参数只有一份（写时复制后原子发布，下一帧读到新实例）。
		/// </remarks>
		public void SetParameter(string key, BaseParam value)
		{
			if (_shared != null)
			{
				_shared.SetParameter(key, value);
				return;
			}
			lock (_parameterLock)
			{
				var copy = new Dictionary<string, BaseParam>(_parameters1) { [key] = value };
				_parameters1 = copy;
				_parameters1Snapshot = copy;
			}
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
		/// 帧上下文（见 <see cref="CreateFrameContext"/>）只释放
		/// 本帧独占的 Image/StepOutputs；模板图与参数归共享上下文所有，**绝不**在这里释放。
		/// </remarks>
		public void Dispose()
		{
			// 注意：不释放 CurrentImage，它是外部传入的引用，由调用者(PrecessRun)管理
			_currentImage = null;

			if (_shared != null)
			{
				// 帧上下文：只释放本帧独占的资源。模板图 / 参数 / 路径是共享的长生命周期资源，
				// 释放权在 shared 手上（本实例的 ModeImage 只是转发，参数写入也落在 shared 上）。
				_image?.Dispose();

				foreach (var kvp in StepOutputs)
				{
					kvp.Value?.Dispose();
				}
				StepOutputs.Clear();
				return;
			}

			// 释放延迟下来的旧模板图，再释放当前模板图
			ReleaseDeferredImages();

			// 释放模板图像
			_modeImage?.Dispose();
			_modeImage = null;

			// 释放图像集合（存储分拆后的多通道图像）
			_image?.Dispose();

			// 释放算法参数中的非托管资源（锁内遍历底层字典，避免与写入竞争）
			lock (_parameterLock)
			{
				foreach (var kvp in _parameters1)
				{
					if (kvp.Value is IDisposable disposableParam)
					{
						disposableParam.Dispose();
					}
				}
				_parameters1 = new Dictionary<string, BaseParam>();
				_parameters1Snapshot = _parameters1;
			}

			foreach (var kvp in StepOutputs)
			{
				kvp.Value?.Dispose();
			}
			StepOutputs.Clear();
		}
	}
}