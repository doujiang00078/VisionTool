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
		/// 工作流执行上下文，包含图像、参数、执行结果等共享数据
		/// </summary>
		private WorkflowExecutionContext _context;

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
		/// 初始化 InspectionProgram 类的新实例。
		/// 允许从外部提供初始算法参数和要注册的检测步骤。
		/// </summary>
		/// <param name="initialAlgorithmParameters">可选的初始算法参数字典，用于设置或覆盖默认参数。如果为null，则只使用默认参数。</param>
		/// <param name="inspectionSteps">要注册到工作流引擎的检测步骤列表。这些步骤将按照添加顺序依次执行。</param>
		/// <param name="modeImage">模板图像，用于比对和定位等操作。</param>
		/// <param name="path">初始化路径，用于各个检测步骤的初始化操作。</param>
		public InspectionProgram(Dictionary<string, BaseParam> initialAlgorithmParameters, List<IWorkflowTask> inspectionSteps, HObject modeImage, string path)
		{
			// 创建工作流执行上下文并传入模板图像
			_context = new WorkflowExecutionContext(modeImage);

			// 初始化工作流引擎
			_workflowEngine = new WorkflowEngine(_context);

			// 订阅工作流引擎的状态变化事件，并转发给外部订阅者
			_workflowEngine.OnTaskStatusChanged += (status) => OnWorkflowTaskStatusChanged?.Invoke(status);
			_workflowEngine.OnTaskFinishStatus += (status, time) => OnWorkflowTaskFinishStatus?.Invoke(status, time);

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
							_context.Parameters1[taskName] = initialAlgorithmParameters[taskName];
						}
						else
						{
							// 如果没有提供参数，让任务自己创建默认参数实例（会返回其特定的参数类型）
							_context.Parameters1[taskName] = step.CreateDefaultParameters();
						}
					}
				}
			}
		}

		/// <summary>
		/// 启动实时检测工作流（在实际生产中可能由相机事件触发循环调用）。
		/// 该方法会根据运行类型（模板图或当前图）处理图像，然后执行完整的检测流程。
		/// </summary>
		/// <param name="initialImage">当前帧图像（可能是多通道图像）</param>
		/// <param name="cancellationToken">取消令牌，用于中止检测流程</param>
		/// <param name="runType">运行类型，指定使用模板图还是当前图进行检测。默认为当前图。</param>
		public async Task StartInspectionAsync(HObject initialImage, CancellationToken cancellationToken, RunType runType = RunType.当前图)
		{
			// 设置运行类型和当前图像
			_context.RunImageType = runType;
			// 注意：不要在这里释放图像！图像的生命周期由调用者(PrecessRun)管理
			// 释放旧图像会导致其他线程（存图、UI显示）正在使用的图像被意外释放
			_context.CurrentImage = initialImage; // 只更新引用，不释放

			// 根据运行类型处理图像通道
			switch (runType)
			{
				case RunType.模板图:
					// 使用模板图像进行检测
					_context.Image = BuildImageChannelDictionary(_context?.ModeImage, "模板图像");
					break;

				case RunType.当前图:
					// 使用当前输入图像进行检测
					_context.Image = BuildImageChannelDictionary(initialImage, "原始图像");
					break;

				default:
					break;
			}

			// 清除 ReadImageStep 的 Path 参数，因为图像已经从外部传入
			if (_context.Parameters.ContainsKey("ReadImage_Path"))
			{
				_context.Parameters["ReadImage_Path"] = null;
			}

			// 启动工作流执行
			await _workflowEngine.StartWorkflowAsync(cancellationToken);
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
				_context.Parameters["ReadImage_Path"] = "Image/test_image.bmp";
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
				_context.Parameters["ReadImage_Path"] = "Image/test_image.bmp";
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
		private static Dictionary<string, HObject> BuildImageChannelDictionary(HObject image, string prefix)
		{
			if (image == null || !image.IsInitialized())
			{
				return null;
			}

			//兼容concat、compose3图像
			HOperatorSet.CountChannels(image, out var countChannels);
			if (countChannels.I == 3)
			{
				HOperatorSet.ImageToChannels(image, out image);
			}
			HOperatorSet.CountObj(image, out var count);

			if (count <= 0)
			{
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

			var imageDict = new Dictionary<string, HObject>
			{
				{ $"{prefix}红色通道", ch1 },
				{ $"{prefix}绿色通道", ch2 },
				{ $"{prefix}蓝色通道", ch3 },
				{ $"{prefix}暗红通道", ch4 }
			};

			return imageDict;
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
		/// 获取指定任务的最新结果（用于调试显示）。
		/// </summary>
		/// <param name="taskName">任务名称</param>
		/// <returns>该任务的执行结果，如果任务不存在则返回null</returns>
		public InspectionStepResult GetStepResult(string taskName)
		{
			_context.StepOutputs.TryGetValue(taskName, out var result);
			return result;
		}

		/// <summary>
		/// 获取所有任务的最新结果（用于调试显示）。
		/// </summary>
		/// <returns>所有检测步骤的执行结果列表</returns>
		public List<InspectionStepResult> GetStepResults()
		{
			var result = _context.StepOutputs.Values.ToList();
			return result;
		}

		/// <summary>
		/// 设置模板图像。
		/// 用于更新检测流程中使用的参考模板。
		/// </summary>
		/// <param name="hObject">新的模板图像</param>
		/// <returns>设置成功返回true</returns>
		public bool SetModeImage(HObject hObject)
		{
			_context.ModeImage?.Dispose(); // 释放旧的模板图像
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

		public Dictionary<string, HObject> GetImage()
		{
			return _context.Image;
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
		/// 获取指定任务的算法参数。
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
		/// 获取所有算法参数的字典副本，用于保存或外部查看。
		/// 外部组件可以通过此方法获取当前的参数集合，并自行实现持久化逻辑（如序列化到文件）。
		/// </summary>
		public Dictionary<string, BaseParam> GetAllAlgorithmParameters()
		{
			// 返回一个新的字典副本，以防止外部直接修改内部的参数字典。
			return new Dictionary<string, BaseParam>(_context.Parameters1);
		}

		/// <summary>
		/// 更新算法参数。
		/// 通过工作流引擎更新指定任务的参数配置。
		/// </summary>
		/// <param name="name">参数名称（任务名称）</param>
		/// <param name="baseParam">新的参数对象</param>
		public void UpdateAlgorithmParameter(string name, BaseParam baseParam)
		{
			// 通过 WorkflowEngine 更新参数，WorkflowEngine 会将更新同步到 _context.Parameters1。
			_workflowEngine.SetParameter(name, baseParam);
		}

		/// <summary>
		/// 实现IDisposable来清理资源。
		/// 释放工作流引擎、执行上下文及其关联的HObject图像资源，防止内存泄漏。
		/// </summary>
		public void Dispose()
		{
			_workflowEngine?.Dispose(); // WorkflowEngine会清理其上下文和任务结果
			_workflowEngine = null;
			_context?.Dispose(); // 确保 WorkflowExecutionContext 及其HObject图像资源也被释放
			_context = null;
		}
	}
}