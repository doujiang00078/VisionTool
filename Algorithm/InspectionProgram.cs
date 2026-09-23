using HalconDotNet;
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
	/// 封装了一个完整的视觉检测工作流。
	/// 该类负责管理整个视觉检测流程，包括工作流引擎的初始化、任务的执行、参数的管理等。
	/// 支持实时检测模式、调试模式和单步执行模式。
	/// </summary>
	public class InspectionProgram : IDisposable
	{
		/// <summary>
		/// 工作流引擎，负责任务的调度和执行
		/// </summary>
		private WorkflowEngine _workflowEngine;

		/// <summary>
		/// 当前 WorkflowEngine 实例，供 step 层读取已完成依赖的快照。
		/// </summary>
		public WorkflowEngine CurrentEngine => _workflowEngine;

		/// <summary>
		/// 工作流执行上下文，包含图像、参数、执行结果等共享数据
		/// </summary>
		private WorkflowExecutionContext _context;

		/// <summary>
		/// 帧并发度（构造参数）：>1 时每帧用一份独立的帧上下文，结果按帧取回、按帧退役释放；
		/// 1 = 历史行为（所有帧共用长生命周期的 <see cref="_context"/>）。
		/// </summary>
		private readonly int _maxConcurrentFrames;

		#region 帧上下文生命周期（多帧）

		/// <summary>
		/// 已结束的帧上下文在释放前保留的帧数。
		/// 为什么不是"用完立刻释放"：结果的读者不止运行这一帧的调用方——UI 的异步渲染、
		/// 状态事件订阅方、以及主程序管线里排在算法块之后的块，都可能在帧结束后仍持有结果引用
		/// （<see cref="InspectionStepResult.Dispose"/> 会释放 DefectiveReg 等 HObject，提前释放是访问冲突）。
		/// 留两帧与引擎 <c>_previousOutputs</c> 的窗口一致：帧 k 的结果到帧 k+2 才释放。
		/// </summary>
		private const int RetainedFinishedFrames = 2;

		/// <summary>帧槽：一帧一个，记录它的帧上下文、开始序号、是否已结束。</summary>
		private sealed class FrameSlot
		{
			public WorkflowExecutionContext Context;
			public long Sequence;
			public bool Finished;
		}

		/// <summary>帧槽与"最近一帧"指针的保护锁（所有读写都在锁内）。</summary>
		private readonly object _frameLock = new object();

		private readonly List<FrameSlot> _frameSlots = new List<FrameSlot>();
		private long _frameSequence;
		private WorkflowExecutionContext _latestFrame;

		/// <summary>
		/// 结果读取路由：多帧时读"最近一帧"的帧上下文，单帧时就是共享上下文（历史行为）。
		/// 生产路径要用**本帧**的结果，请用 <see cref="StartInspectionFrameAsync"/> 的返回值——
		/// 多帧并发时"最近一帧"未必是刚跑完的那一帧。这里的"最近一帧"只服务 UI/调试这类读者。
		/// </summary>
		private WorkflowExecutionContext ReadContext
		{
			get
			{
				lock (_frameLock)
				{
					var latest = _latestFrame;
					return latest ?? _context;
				}
			}
		}

		/// <summary>
		/// 开始一帧：单帧模式返回共享上下文（历史行为），多帧模式新建一份帧上下文并登记。
		/// </summary>
		private WorkflowExecutionContext BeginFrame()
		{
			if (_maxConcurrentFrames <= 1)
			{
				PointReadsAtSharedContext();
				return _context;
			}

			var frame = WorkflowExecutionContext.CreateFrameContext(_context);
			lock (_frameLock)
			{
				_frameSlots.Add(new FrameSlot { Context = frame, Sequence = ++_frameSequence });
				_latestFrame = frame;
			}
			return frame;
		}

		/// <summary>
		/// 结束一帧：标记为已结束并回收更早的帧上下文。单帧模式不做任何事
		/// （共享上下文由 <see cref="Dispose"/> 释放，与历史行为一致）。
		/// </summary>
		private void EndFrame(WorkflowExecutionContext frame)
		{
			if (ReferenceEquals(frame, _context)) return;

			lock (_frameLock)
			{
				var slot = _frameSlots.Find(s => ReferenceEquals(s.Context, frame));
				if (slot != null) slot.Finished = true;
				TrimFinishedFrames();
			}
		}

		/// <summary>
		/// 回收已结束的帧上下文：保留最近 <see cref="RetainedFinishedFrames"/> 个**已结束**的帧，
		/// 加上所有**在飞**帧（在飞帧还在被算子读，释放即访问冲突），其余释放。
		/// 按开始序号排序而不是完成顺序：多帧下帧可能乱序完成，而"最近一帧"必然是序号最大的那个，
		/// 这样它永远落在保留集合里，不会被提前释放。
		/// </summary>
		private void TrimFinishedFrames()
		{
			// 必须持 _frameLock
			var finished = new List<FrameSlot>();
			foreach (var slot in _frameSlots)
			{
				if (slot.Finished) finished.Add(slot);
			}
			finished.Sort((a, b) => b.Sequence.CompareTo(a.Sequence));
			for (var i = RetainedFinishedFrames; i < finished.Count; i++)
			{
				_frameSlots.Remove(finished[i]);
				finished[i].Context.Dispose();
			}
		}

		/// <summary>
		/// 调试/单步路径跑在共享上下文上，把"最近一帧"指回它，
		/// 避免 UI 读到上一次生产帧的帧上下文（那会让调试显示永远不刷新）。
		/// </summary>
		private void PointReadsAtSharedContext()
		{
			lock (_frameLock)
			{
				_latestFrame = _context;
			}
		}

		#endregion 帧上下文生命周期（多帧）

		/// <summary>
		/// 当工作流中某个任务的状态发生变化时触发。
		/// 用于实时监控各个检测步骤的执行状态（如：等待、运行中、成功、失败等）。
		/// </summary>
		public event Action<WorkflowTaskStatus> OnWorkflowTaskStatusChanged;

		/// <summary>
		/// 当所有任务完成时触发。
		/// 提供所有任务的最终状态列表和总执行时间（毫秒）。
		/// 用于获取整个检测流程的完整执行结果。
		/// </summary>
		public event Action<IEnumerable<WorkflowTaskStatus>, long> OnWorkflowTaskFinishStatus;

		/// <summary>
		/// 帧内改动了**跨帧复用**的参数对象时触发（引擎的共享参数突变检测，消息形如
		/// <c>参数[栅线提取].Wins</c>，同类只报一次）。
		/// 生产侧必须把它接到日志上：这类写法不报错、不崩溃，只会在并行跑帧时偶尔串数据。
		/// 修法见 <see cref="VisionTool.Models.FrameScopedAttribute"/>。
		/// </summary>
		public event Action<string> OnSharedParameterMutated;

		/// <summary>
		/// 初始化 InspectionProgram 类的新实例。
		/// 允许从外部提供初始算法参数和要注册的检测步骤。
		/// </summary>
		/// <param name="initialAlgorithmParameters">可选的初始算法参数字典，用于设置或覆盖默认参数。如果为null，则只使用默认参数。</param>
		/// <param name="inspectionSteps">要注册到工作流引擎的检测步骤列表。这些步骤将按照添加顺序依次执行。</param>
		/// <param name="modeImage">模板图像，用于比对和定位等操作。</param>
		/// <param name="path">初始化路径，用于各个检测步骤的初始化操作。</param>
		/// <param name="scheduler">步骤体执行器；null 表示线程池（历史行为），见 <see cref="WorkflowEngine"/>。</param>
		/// <param name="maxConcurrentFrames">
		/// 帧并发度：同一引擎实例同时可跑的帧数，默认 1 = 串行（历史行为）。
		/// >1 时每帧一份帧上下文（<see cref="WorkflowExecutionContext.CreateFrameContext"/>），
		/// 于是「多通道图 / CurrentImage / 本帧结果」按帧隔离，而模板图、参数、工作路径仍只有一份。
		/// 注意：帧并发只是"不互相踩运行态"的**必要条件**，步骤体自身不得把每帧结果写在共享 Param 上
		/// （清单见主程序 AlgorithmParallel.FrameConcurrency），否则得到的是静默错误结果。
		/// </param>
		public InspectionProgram(Dictionary<string, BaseParam> initialAlgorithmParameters, List<IWorkflowTask> inspectionSteps, HObject modeImage, string path, TaskScheduler scheduler = null, int maxConcurrentFrames = 1)
		{
			_maxConcurrentFrames = Math.Max(1, maxConcurrentFrames);
			// 创建工作流执行上下文并传入模板图像
			_context = new WorkflowExecutionContext(modeImage);
			_context.Path = path;
			// 初始化工作流引擎
			_workflowEngine = new WorkflowEngine(_context, scheduler);

			// 订阅工作流引擎的状态变化事件，并转发给外部订阅者
			_workflowEngine.OnTaskStatusChanged += (status) => OnWorkflowTaskStatusChanged?.Invoke(status);
			_workflowEngine.OnTaskFinishStatus += (status, time) => OnWorkflowTaskFinishStatus?.Invoke(status, time);
			_workflowEngine.OnSharedParameterMutated += (message) => OnSharedParameterMutated?.Invoke(message);

			// 注册检测步骤到工作流引擎，并初始化每个步骤
			// 根据 inspectionSteps 列表里的任务名称来初始化参数
			if (inspectionSteps != null)
			{
				foreach (var step in inspectionSteps)
				{
					// 注册任务到工作流引擎
					_workflowEngine.AddTask(step);
					if (step.Name == "自定义算法") { _workflowEngine.SetTaskAlwaysRun("自定义算法"); }
					// 初始化任务
					step?.Initialize(path);

					// 根据任务名称初始化参数
					if (step != null)
					{
						string taskName = step.Name;

						// 如果 initialAlgorithmParameters 中有该任务的参数，使用它；否则让任务创建默认参数
						if (initialAlgorithmParameters != null && initialAlgorithmParameters.ContainsKey(taskName))
						{
							_context.SetParameter(taskName, initialAlgorithmParameters[taskName]);
						}
						else
						{
							// 如果没有提供参数，让任务自己创建默认参数实例（会返回其特定的参数类型）
							_context.SetParameter(taskName, step.CreateDefaultParameters());
						}
					}
				}
			}
		}

		/// <summary>
		/// 启动实时检测工作流（生产路径），并返回**本帧**的结果列表。
		/// 为什么返回值必须按帧取：多帧并发时"最近一帧"未必是刚跑完的这一帧——调用方如果跑完再去读
		/// <see cref="GetStepResults"/>，可能拿到另一帧的结果（区域被别的帧释放/替换）。
		/// 结果对象的生命周期仍是帧级：帧 k 的结果到帧 k+2 开始时释放（见 <see cref="RetainedFinishedFrames"/>）。
		/// </summary>
		/// <param name="initialImage">当前帧图像（可能是多通道图像）</param>
		/// <param name="cancellationToken">取消令牌，用于中止检测流程</param>
		/// <param name="runType">运行类型，指定使用模板图还是当前图进行检测。默认为当前图。</param>
		/// <param name="timeoutMs">超时（毫秒），0 = 不设超时</param>
		public async Task<List<InspectionStepResult>> StartInspectionFrameAsync(HObject initialImage, CancellationToken cancellationToken, RunType runType = RunType.当前图, int timeoutMs = 0)
		{
			var frame = BeginFrame();
			try
			{
				await RunInspectionAsync(frame, initialImage, cancellationToken, runType, timeoutMs).ConfigureAwait(false);
				return frame.StepOutputs.Values.ToList();
			}
			finally
			{
				EndFrame(frame);
			}
		}

		/// <summary>
		/// 启动实时检测工作流（兼容旧签名：丢弃结果列表）。
		/// 新代码请用 <see cref="StartInspectionFrameAsync"/> 直接拿本帧结果；只跑不问结果的调用方可以继续用这个。
		/// </summary>
		/// <param name="initialImage">当前帧图像（可能是多通道图像）</param>
		/// <param name="cancellationToken">取消令牌，用于中止检测流程</param>
		/// <param name="runType">运行类型，指定使用模板图还是当前图进行检测。默认为当前图。</param>
		public async Task StartInspectionAsync(HObject initialImage, CancellationToken cancellationToken, RunType runType = RunType.当前图, int timeoutMs = 0)
		{
			await StartInspectionFrameAsync(initialImage, cancellationToken, runType, timeoutMs).ConfigureAwait(false);
		}

		/// <summary>
		/// 在指定上下文上跑完一帧：设定图像来源、清掉 ReadImage 路径、启动引擎并等本帧所有算子退出。
		/// </summary>
		private async Task RunInspectionAsync(WorkflowExecutionContext context, HObject initialImage, CancellationToken cancellationToken, RunType runType, int timeoutMs)
		{
			// 设置运行类型和当前图像
			context.RunImageType = runType;
			// 注意：不要在这里释放图像！图像的生命周期由调用者(PrecessRun)管理
			// 释放旧图像会导致其他线程（存图、UI显示）正在使用的图像被意外释放
			context.CurrentImage = initialImage; // 只更新引用，不释放

			// 根据运行类型处理图像通道
			switch (runType)
			{
				case RunType.模板图:
					// 使用模板图像进行检测
					context.Image = BuildImageChannelDictionary(context.ModeImage, "模板图像");
					break;

				case RunType.当前图:
					// 使用当前输入图像进行检测
					context.Image = BuildImageChannelDictionary(initialImage, "原始图像");
					break;

				default:
					break;
			}

			// 清除 ReadImageStep 的 Path 参数，因为图像已经从外部传入
			if (context.Parameters.ContainsKey("ReadImage_Path"))
			{
				context.SetObjectParameter("ReadImage_Path", null);
			}

			// 启动工作流执行
			await _workflowEngine.StartWorkflowAsync(context, cancellationToken, null, timeoutMs).ConfigureAwait(false);
		}

		/// <summary>
		/// 启动调试模式，运行到指定任务。
		/// 用于在开发和调试阶段，逐步执行工作流到特定的检测步骤，方便问题定位。
		/// </summary>
		/// <param name="debugImage">用于调试的图像</param>
		/// <param name="runToTaskName">运行到指定任务名称（包含该任务）</param>
		/// <param name="cancellationToken">取消令牌</param>
		/// <param name="runType">运行类型，指定使用模板图还是当前图进行检测。默认为当前图。</param>
		public async Task RunToStepAsync(HObject debugImage, string runToTaskName, CancellationToken cancellationToken, RunType runType = RunType.当前图)
		{
			PointReadsAtSharedContext(); // 调试跑在共享上下文上，UI 显示要指向它
			_context.RunImageType = runType;
			_context.CurrentImage = debugImage; // 设置调试图像

			// 根据运行类型处理图像通道
			switch (runType)
			{
				case RunType.模板图:
					// 使用模板图像进行调试
					_context.Image = BuildImageChannelDictionary(_context?.ModeImage, "模板图像");
					break;

				case RunType.当前图:
					// 使用当前调试图像
					_context.Image = BuildImageChannelDictionary(debugImage, "原始图像");
					break;

				default:
					break;
			}

			// 调试模式下，可能需要恢复 ReadImageStep 的默认路径
			if (_context.Parameters.ContainsKey("ReadImage_Path"))
			{
				_context.SetObjectParameter("ReadImage_Path", "Image/test_image.bmp");
			}

			// 运行工作流到指定任务
			await _workflowEngine.StartWorkflowAsync(cancellationToken, runToTaskName);
		}

		/// <summary>
		/// 执行单个任务（用于单步调试）。
		/// 允许单独执行工作流中的某一个检测步骤，便于精细化调试和参数调优。
		/// </summary>
		/// <param name="taskName">要执行的任务名称</param>
		/// <param name="debugImage">当前调试图像</param>
		/// <param name="cancellationToken">取消令牌</param>
		/// <param name="runType">运行类型，指定使用模板图还是当前图进行检测。默认为当前图。</param>
		public async Task ExecuteSingleStepAsync(string taskName, HObject debugImage, CancellationToken cancellationToken, RunType runType = RunType.当前图)
		{
			PointReadsAtSharedContext(); // 调试跑在共享上下文上，UI 显示要指向它
			_context.RunImageType = runType;
			_context.CurrentImage = debugImage; // 设置调试图像

			// 根据运行类型处理图像通道
			switch (runType)
			{
				case RunType.模板图:
					_context.Image = BuildImageChannelDictionary(_context?.ModeImage, "模板图像");
					break;

				case RunType.当前图:
					_context.Image = BuildImageChannelDictionary(debugImage, "原始图像");
					break;

				default:
					break;
			}

			// 调试模式下，恢复 ReadImageStep 的默认路径
			if (_context.Parameters.ContainsKey("ReadImage_Path"))
			{
				_context.SetObjectParameter("ReadImage_Path", "Image/test_image.bmp");
			}

			// 执行单个任务
			await _workflowEngine.ExecuteSingleTaskAsync(taskName, cancellationToken);
		}

		/// <summary>
		/// 将输入图像统一映射为算法使用的4通道字典。
		/// 兼容规则：
		/// 1通道：复制到其余三个通道；
		/// 3通道：第4通道复制第1通道（红平面）；
		/// 4通道及以上：取前4通道。
		/// </summary>
		/// <param name="image">输入图像（多对象通道图）</param>
		/// <param name="prefix">键名前缀（模板图像/原始图像）</param>
		/// <returns>标准4通道字典，若输入无效则返回 null</returns>
		private static HObjectMap BuildImageChannelDictionary(HObject image, string prefix)
		{
			if (image == null || !image.IsInitialized())
			{
				return null;
			}

			//兼容concat、compose3图像
			HOperatorSet.CountChannels(image, out HTuple countChannels);
			try
			{
				HObject channelsImage = null;
				if (countChannels.I == 3)
				{
					channelsImage = image;
					HOperatorSet.ImageToChannels(channelsImage, out image);
				}
				HOperatorSet.CountObj(image, out HTuple count);
				try
				{
					if (count <= 0)
					{
						if (channelsImage != null) image.Dispose();
						return null;
					}

					HObject ch1 = image.SelectObj(1);
					HObject ch2;
					HObject ch3;
					HObject ch4;

					if (count >= 4)
					{
						ch2 = image.SelectObj(2);
						ch3 = image.SelectObj(3);
						ch4 = image.SelectObj(4);
					}
					else if (count == 3)
					{
						ch2 = image.SelectObj(2);
						ch3 = image.SelectObj(3);
						ch4 = ch1.CopyObj(1, -1);
					}
					else
					{
						// 1通道：复制三份到其他通道
						ch2 = ch1.CopyObj(1, -1);
						ch3 = ch1.CopyObj(1, -1);
						ch4 = ch1.CopyObj(1, -1);
					}

					// 释放 ImageToChannels 创建的中间容器对象
					if (channelsImage != null) image.Dispose();

					var imageDict = new HObjectMap
					{
						{ $"{prefix}红色通道", ch1 },
						{ $"{prefix}绿色通道", ch2 },
						{ $"{prefix}蓝色通道", ch3 },
						{ $"{prefix}暗红通道", ch4 }
					};

					return imageDict;
				}
				finally
				{
					count.Dispose();
				}
			}
			finally
			{
				countChannels.Dispose();
			}
		}

		/// <summary>
		/// 获取所有任务的当前状态。
		/// 返回工作流中每个检测步骤的执行状态信息。
		/// </summary>
		/// <returns>任务状态集合</returns>
		public IEnumerable<WorkflowTaskStatus> GetTaskStatuses()
		{
			return _workflowEngine.GetTaskStatuses();
		}

		/// <summary>
		/// 获取指定任务的最新结果（用于调试显示，读"最近一帧"）。
		/// 生产路径请用 <see cref="StartInspectionFrameAsync"/> 的返回值：多帧并发时这里未必是刚跑的那一帧。
		/// </summary>
		/// <param name="taskName">任务名称</param>
		/// <returns>该任务的执行结果，如果任务不存在则返回null</returns>
		public InspectionStepResult GetStepResult(string taskName)
		{
			var context = ReadContext;
			if (context == null) return null;
			context.StepOutputs.TryGetValue(taskName, out var result);
			return result;
		}

		/// <summary>
		/// 获取所有任务的最新结果（用于调试显示，读"最近一帧"；生产路径见 <see cref="StartInspectionFrameAsync"/>）。
		/// </summary>
		/// <returns>所有检测步骤的执行结果列表</returns>
		public List<InspectionStepResult> GetStepResults()
		{
			return ReadContext?.StepOutputs.Values.ToList() ?? new List<InspectionStepResult>();
		}

		/// <summary>
		/// 设置模板图像。
		/// 用于更新检测流程中使用的参考模板。
		/// </summary>
		/// <param name="hObject">新的模板图像</param>
		/// <returns>设置成功返回true</returns>
		public bool SetModeImage(HObject hObject)
		{
			// 旧模板图不在这里释放：由 WorkflowExecutionContext 延迟到帧间统一释放，
			// 否则 UI 在线换模板时，正在读旧模板图的算法线程会 use-after-free（HALCON 访问冲突）。
			_context.ModeImage = hObject;
			return true;
		}

		/// <summary>
		/// 获取当前的模板图像。
		/// </summary>
		/// <returns>模板图像对象</returns>
		public HObject GetModeImage()
		{
			return _context.ModeImage;
		}

		/// <summary>
		/// 获取当前图像集合（多通道）。
		/// **借用语义**：返回的是引擎内部实例，只保证"当前帧内有效"——下一帧开始时旧的多通道图会被释放。
		/// 跨帧使用（例如 UI 把它留给异步显示）必须自己 <c>CopyObj(1, -1)</c> 取副本；直接持有内部引用会在
		/// 下一帧读到已释放的 HObject（HALCON 访问冲突/算子在 catch 里被静默吞掉）。
		/// </summary>
		public HObjectMap GetImage()
		{
			return ReadContext?.Image;
		}

		public string GetPath()
		{
			return _context.Path;
		}

		/// <summary>
		/// 获取当前流程中所有缺陷检测算法的名称列表。
		/// </summary>
		/// <returns>缺陷算法名称集合</returns>
		public DefectNameCollection GetDefectAlgorithmName(bool isCustom = false)
		{
			return _workflowEngine.GetDefectAlgorithmName(isCustom);
		}

		/// <summary>
		/// 获取指定任务的算法参数（**内部实例，只读用**；每帧被 20+ 个模块并发读）。
		/// 需要编辑请用 <see cref="GetEditableAlgorithmParameters"/> 取副本，
		/// 改完用 <see cref="UpdateAlgorithmParameter"/> 提交（加锁原子替换，下一帧生效）。
		/// </summary>
		/// <param name="taskName">任务名称</param>
		/// <returns>该任务的参数对象，如果不存在则返回默认实例</returns>
		public BaseParam GetAlgorithmParameters(string taskName)
		{
			var param = new BaseParam(); // 创建一个默认实例
										 // 尝试获取指定任务的参数，如果存在则将其赋值给 param，否则 param 保持为默认实例
			return _context.Parameters1.TryGetValue(taskName, out param) ? param : param;
		}

		/// <summary>
		/// 获取指定任务参数的**深拷贝**，供 UI 编辑。
		/// 为什么必须给副本：<c>Parameters1</c> 里的是算法线程正在读的实例，UI 直接改它就是在帧中途
		/// 改运行中的参数（撕裂读、行为难复现）。改副本不影响引擎；要让改动生效，调用
		/// <see cref="UpdateAlgorithmParameter"/>（内部是加锁的原子替换，下一帧读到新实例）。
		/// </summary>
		public BaseParam GetEditableAlgorithmParameters(string taskName)
		{
			return GetAlgorithmParameters(taskName)?.DeepClone();
		}

		/// <summary>
		/// 获取所有算法参数的字典副本，用于保存或外部查看。
		/// 外部组件可以通过此方法获取当前的参数集合，并自行实现持久化逻辑（如序列化到文件）。
		/// </summary>
		public Dictionary<string, BaseParam> GetAllAlgorithmParameters()
		{
			// 返回一个新的字典副本，以防止外部直接修改内部的参数字典。
			return new Dictionary<string, BaseParam>(_context.Parameters1);
		}

		/// <summary>
		/// 更新算法参数（**帧间生效**）。
		/// 通过工作流引擎写入：加锁重建快照后原子替换，帧中途已在跑的算法线程继续用它们已读到的旧实例，
		/// 下一帧开始读到新实例——因此不会出现"帧中途参数被改一半"。
		/// </summary>
		/// <param name="name">参数名称（任务名称）</param>
		/// <param name="baseParam">新的参数对象</param>
		public void UpdateAlgorithmParameter(string name, BaseParam baseParam)
		{
			// 通过 WorkflowEngine 更新参数，WorkflowEngine 会将更新同步到 _context.Parameters1。
			_workflowEngine.SetParameter(name, baseParam);
		}

		/// <summary>
		/// 设置串行执行模式
		/// </summary>
		public void SetSerialExecution(bool serial)
		{
			_workflowEngine.ForceSerialExecution = serial;
		}

		/// <summary>
		/// 实现IDisposable来清理资源。
		/// 释放工作流引擎、执行上下文及其关联的HObject图像资源，防止内存泄漏。
		/// </summary>
		public void Dispose()
		{
			_workflowEngine?.Dispose(); // WorkflowEngine会清理其上下文和任务结果
			_workflowEngine = null;

			// 先取出要释放的帧上下文（在飞帧不释放：它还在被算子读，释放就是访问冲突）
			var frames = new List<WorkflowExecutionContext>();
			lock (_frameLock)
			{
				foreach (var slot in _frameSlots)
				{
					if (slot.Finished) frames.Add(slot.Context);
				}
				_frameSlots.Clear();
				_latestFrame = null;
			}
			foreach (var frame in frames)
				frame.Dispose();

			_context?.Dispose(); // 确保 WorkflowExecutionContext 及其HObject图像资源也被释放
			_context = null;
		}
	}
}
