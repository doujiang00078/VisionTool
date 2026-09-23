using HalconDotNet;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VisionTool.Algorithm;
using VisionTool.Enums;
using VisionTool.Interfaces;
using VisionTool.Models;

namespace VisionTool.Services
{
	/// <summary>
	/// 提供与视觉检测相关的应用服务。
	/// </summary>
	public class InspectionService : IDisposable
	{
		/// <summary>
		/// 制程名称
		/// </summary>
		public string ProcessName;

		/// <summary>
		/// 检测流程
		/// </summary>
		private InspectionProgram _inspectionProgram;

		/// <summary>
		/// 取消令牌。读写全部走 <see cref="Interlocked"/>/<see cref="Volatile"/>：
		/// 写入发生在 _inspectionLock 内（每次检测开始），而 <see cref="StopInspection"/> 与
		/// <see cref="Dispose"/> 可能在任意线程被调用——不加保护就会对"已被替换或已释放"的
		/// CTS 调 Cancel/Dispose（ObjectDisposedException，或把新一轮检测误取消）。
		/// </summary>
		private CancellationTokenSource _cancellationTokenSource;

		/// <summary>
		/// 已释放标志：1 = 已 Dispose。销毁后到达的检测请求直接丢弃（停止流程中的正常竞争）。
		/// </summary>
		private int _disposed;

		/// <summary>
		/// 同一 InspectionService 实例的**帧并发门**：同时最多允许 N 帧进入检测（N = 构造参数
		/// <c>maxConcurrentFrames</c>，默认 1 = 完全串行）。
		/// 历史上它是 SemaphoreSlim(1,1)，用来兜住"共享 step 实例的 _latestResult 被另一帧释放"。
		/// 现在这两层都已经在引擎侧处理：① 每帧一份运行状态与帧上下文（帧间不串数据）；
		/// ② 每个步骤实例一道互斥门 + 结果取走（<c>BaseWorkflowTask.TakeLatestResult</c>，结果所有权随帧走）。
		/// 剩下的正确性前提是**模块内不得把每帧结果写在共享 Param 上**（清单见主程序
		/// AlgorithmParallel.FrameConcurrency），该前提未完成前，主程序会把生效并发度夹到 1。
		/// </summary>
		private readonly SemaphoreSlim _inspectionLock;

		/// <summary>
		/// 当工作流任务状态变化时触发，供UI订阅。
		/// </summary>
		public event Action<WorkflowTaskStatus> OnWorkflowTaskStatusChanged;

		/// <summary>
		/// 当所有任务完成时触发
		/// </summary>
		public event Action<IEnumerable<WorkflowTaskStatus>, long> OnWorkflowTaskFinishStatus;

		/// <summary>
		/// 帧内改动了跨帧复用的参数对象时触发（见 <see cref="InspectionProgram.OnSharedParameterMutated"/>）。
		/// 生产侧接到日志上即可；排障时看到这条就说明有模块把"每帧产物"放在了共享参数上。
		/// </summary>
		public event Action<string> OnSharedParameterMutated;

		/// <summary>
		/// 构造函数。
		/// </summary>
		/// <param name="scheduler">
		/// 步骤体执行器；null 表示线程池（历史行为）。生产程序注入专用调度器，避免步骤体
		/// 与通讯/取图/存图/UI 争线程池线程；注意不能注入"算法分支预算"本身（会自锁）。
		/// </param>
		/// <param name="maxConcurrentFrames">
		/// 帧并发度：同时最多允许几帧进入检测（默认 1 = 串行，现状行为）。>1 之前必须确认
		/// "模块每帧结果不再写共享 Param"（见 <c>AlgorithmParallel.FrameConcurrency</c> 的清单）。
		/// </param>
		public InspectionService(Dictionary<string, BaseParam> initialAlgorithmParameters, List<IWorkflowTask> inspectionSteps, HObject modeImage, string path, TaskScheduler scheduler = null, int maxConcurrentFrames = 1)
		{
			_inspectionLock = new SemaphoreSlim(Math.Max(1, maxConcurrentFrames), Math.Max(1, maxConcurrentFrames));
			_inspectionProgram = new InspectionProgram(initialAlgorithmParameters, inspectionSteps, modeImage, path, scheduler, maxConcurrentFrames);
			_inspectionProgram.OnWorkflowTaskStatusChanged += (status) => OnWorkflowTaskStatusChanged?.Invoke(status);
			_inspectionProgram.OnWorkflowTaskFinishStatus += (status, time) => OnWorkflowTaskFinishStatus?.Invoke(status, time);
			_inspectionProgram.OnSharedParameterMutated += (message) => OnSharedParameterMutated?.Invoke(message);
		}

		/// <summary>
		/// 替换取消令牌：把新令牌发布出去，并取消+释放旧令牌。
		/// 必须在持有 <see cref="_inspectionLock"/> 时调用——此时上一轮检测已经返回（引擎会等所有
		/// 算子和 finally 退出才返回），所以释放旧令牌是安全的；顺序上先发布新的再处理旧的，
		/// 避免中间出现"字段为 null"导致 StopInspection 空转。
		/// </summary>
		private CancellationTokenSource ReplaceCancellationSource(int timeoutMs)
		{
			var next = timeoutMs > 0
				? new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs))
				: new CancellationTokenSource();
			var previous = Interlocked.Exchange(ref _cancellationTokenSource, next);
			previous?.Cancel();
			previous?.Dispose();
			return next;
		}

		/// <summary>
		/// 取消当前检测，吞掉与 Dispose 竞争产生的异常。
		/// 停止路径不应该因为"令牌刚好被释放"或"回调抛异常"而失败——那种情况下该次检测已经结束。
		/// </summary>
		private static void TryCancel(CancellationTokenSource source)
		{
			if (source == null) return;
			try
			{
				source.Cancel();
			}
			catch (ObjectDisposedException) { }
			catch (AggregateException) { }
		}

		/// <summary>
		/// 启动实时检测（生产模式），返回**本帧**的结果列表。
		/// 为什么按帧返回：多帧并发时"最近一帧"未必是这一次调用跑的那一帧，跑完再去
		/// <see cref="GetTaskResults"/> 读可能拿到另一帧的结果（区域已被别的帧释放或替换）。
		/// 结果对象的存活期是帧级的：帧 k 的结果到帧 k+2 开始时释放。
		/// </summary>
		/// <param name="currentImage">当前图像</param>
		/// <param name="timeoutMs">超时时间（毫秒），0表示不设超时</param>
		/// <param name="runType">运行类型</param>
		public async Task<List<InspectionStepResult>> StartLiveInspection(HObject currentImage, int timeoutMs = 0, RunType runType = RunType.当前图)
		{
			if (Volatile.Read(ref _disposed) != 0)
			{
				System.Diagnostics.Debug.WriteLine("InspectionService: 已释放，忽略实时检测请求。");
				return null;
			}

			await _inspectionLock.WaitAsync().ConfigureAwait(false);
			try
			{
				var cts = ReplaceCancellationSource(timeoutMs);
				System.Diagnostics.Debug.WriteLine(timeoutMs > 0
					? $"InspectionService: 开始实时检测，超时时间: {timeoutMs}ms."
					: "InspectionService: 开始实时检测（无超时限制）.");

				return await _inspectionProgram.StartInspectionFrameAsync(currentImage, cts.Token, runType, timeoutMs);
			}
			finally
			{
				_inspectionLock.Release();
			}
		}

		/// <summary>
		/// 启动调试模式，运行到指定任务。
		/// </summary>
		/// <param name="debugImage">用于调试的图像</param>
		/// <param name="runToTaskName">运行到该任务名称</param>
		/// <param name="timeoutMs">超时时间（毫秒），0表示不设超时</param>
		/// <param name="runType">运行类型</param>
		public async Task RunWorkflowToStep(HObject debugImage, string runToTaskName, int timeoutMs = 0, RunType runType = RunType.当前图)
		{
			if (Volatile.Read(ref _disposed) != 0)
			{
				System.Diagnostics.Debug.WriteLine("InspectionService: 已释放，忽略调试运行请求。");
				return;
			}

			await _inspectionLock.WaitAsync().ConfigureAwait(false);
			try
			{
				var cts = ReplaceCancellationSource(timeoutMs);
				System.Diagnostics.Debug.WriteLine(timeoutMs > 0
					? $"InspectionService: 在调试模式下运行任务“{runToTaskName}”，超时时间: {timeoutMs}ms."
					: $"InspectionService: 在调试模式下运行任务“{runToTaskName}”（无超时限制）.");

				await _inspectionProgram.RunToStepAsync(debugImage, runToTaskName, cts.Token, runType);
			}
			finally
			{
				_inspectionLock.Release();
			}
		}

		/// <summary>
		/// 执行单个任务（用于单步调试）。
		/// </summary>
		/// <param name="taskName">要执行的任务名称</param>
		/// <param name="debugImage">当前调试图像</param>
		/// <param name="timeoutMs">超时时间（毫秒），0表示不设超时</param>
		/// <param name="runType">运行类型</param>
		public async Task ExecuteSingleStep(string taskName, HObject debugImage, int timeoutMs = 0, RunType runType = RunType.当前图)
		{
			if (Volatile.Read(ref _disposed) != 0)
			{
				System.Diagnostics.Debug.WriteLine("InspectionService: 已释放，忽略单步执行请求。");
				return;
			}

			await _inspectionLock.WaitAsync().ConfigureAwait(false);
			try
			{
				var cts = ReplaceCancellationSource(timeoutMs);
				System.Diagnostics.Debug.WriteLine(timeoutMs > 0
					? $"InspectionService: 正在执行单步“{taskName}”，超时时间: {timeoutMs}ms."
					: $"InspectionService: 正在执行单步“{taskName}”（无超时限制）.");

				await _inspectionProgram.ExecuteSingleStepAsync(taskName, debugImage, cts.Token, runType);
			}
			finally
			{
				_inspectionLock.Release();
			}
		}

		/// <summary>
		/// 停止当前正在进行的工作流。可在任意线程调用（等价于 UI 的"停止"按钮）。
		/// </summary>
		public void StopInspection()
		{
			TryCancel(Volatile.Read(ref _cancellationTokenSource));
			System.Diagnostics.Debug.WriteLine("InspectionService: Inspection cancelled.");
		}

		/// <summary>
		/// 获取所有任务的当前状态。
		/// </summary>
		public IEnumerable<WorkflowTaskStatus> GetWorkflowTaskStatuses()
		{
			return _inspectionProgram.GetTaskStatuses();
		}

		/// <summary>
		/// 获取所有算法参数的列表。
		/// </summary>
		public Dictionary<string, BaseParam> GetAlgorithmParameters()
		{
			return _inspectionProgram.GetAllAlgorithmParameters();
		}

		/// <summary>
		/// 获取指定任务的算法参数（**内部实例，只读用**）。
		/// 需要编辑请用 <see cref="GetEditableTaskAlgorithmParameters"/>，改完用 <see cref="UpdateAlgorithmParameter"/> 提交。
		/// </summary>
		public BaseParam GetTaskAlgorithmParameters(string taskName)
		{
			return _inspectionProgram.GetAlgorithmParameters(taskName);
		}

		/// <summary>
		/// 获取指定任务参数的**深拷贝**，供 UI 编辑（改副本不影响正在跑的引擎；提交用 <see cref="UpdateAlgorithmParameter"/>）。
		/// </summary>
		public BaseParam GetEditableTaskAlgorithmParameters(string taskName)
		{
			return _inspectionProgram.GetEditableAlgorithmParameters(taskName);
		}

		/// <summary>
		/// 更新算法参数。
		/// </summary>
		/// <summary>
		/// 更新算法参数（**帧间生效**；加锁原子替换，见 InspectionProgram.UpdateAlgorithmParameter）。
		/// </summary>
		public void UpdateAlgorithmParameter(string name, BaseParam baseParam)
		{
			_inspectionProgram.UpdateAlgorithmParameter(name, baseParam);
		}

		/// <summary>
		/// 获取指定任务的最新结果。
		/// </summary>
		public InspectionStepResult GetTaskResult(string taskName)
		{
			return _inspectionProgram.GetStepResult(taskName);
		}

		/// <summary>
		/// 获取所有任务的最新结果
		/// </summary>
		/// <returns></returns>
		public List<InspectionStepResult> GetTaskResults()
		{
			return _inspectionProgram.GetStepResults();
		}

		/// <summary>
		/// 设置模板图
		/// </summary>
		/// <param name="hObject"></param>
		/// <returns></returns>
		public bool SetModeImage(HObject hObject)
		{
			return _inspectionProgram.SetModeImage(hObject);
		}

		/// <summary>
		/// 获取模板图（返回副本，调用方负责 Dispose）
		/// </summary>
		/// <returns>模板图像的副本；若模板图未初始化则返回 null</returns>
		public HObject GetModeImage()
		{
			var img = _inspectionProgram.GetModeImage();
			return img?.IsInitialized() == true ? img.CopyObj(1, -1) : null;
		}

		/// <summary>
		/// 获取当前多通道图像集合（**借用语义**：内部实例，只保证当前帧内有效；跨帧请自行 <c>CopyObj(1, -1)</c>）。
		/// </summary>
		public HObjectMap GetImage()
		{
			return _inspectionProgram.GetImage();
		}

		public string GetPath()
		{
			return _inspectionProgram.GetPath();
		}

		/// <summary>
		/// 获取当前流程缺陷名称列表
		/// </summary>
		/// <returns></returns>
		public DefectNameCollection GetDefectAlgorithmName(bool isCustom = false)
		{
			return _inspectionProgram.GetDefectAlgorithmName(isCustom);
		}

		/// <summary>
		/// 设置串行执行模式：任务按拓扑顺序逐个执行，per-step 内存增量精确归属
		/// </summary>
		public void SetSerialExecution(bool serial)
		{
			_inspectionProgram.SetSerialExecution(serial);
		}

		/// <summary>
		/// 释放资源。幂等；会先取消并等待正在进行的检测退出，再释放引擎与 HALCON 资源。
		/// 若等待超时（HALCON 算子不可中断，可能正在跑秒级算子），则**只记日志、不释放**
		/// engine/program——宁可泄漏也不能在算子运行中释放它持有的非托管资源（否则是访问冲突）。
		/// </summary>
		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) != 0)
				return;

			TryCancel(Volatile.Read(ref _cancellationTokenSource));

			var owned = false;
			try
			{
				// 等本次检测完全退出（引擎会等所有算子与 finally 退出后才返回）
				owned = _inspectionLock.Wait(TimeSpan.FromSeconds(30));
				if (!owned)
				{
					System.Diagnostics.Debug.WriteLine(
						"InspectionService.Dispose: 等了 30 秒仍有检测在运行（HALCON 算子不可中断），"
						+ "本次不释放引擎与图像资源，避免访问冲突；请在停止检测后再释放。");
					return;
				}

				_cancellationTokenSource?.Dispose();
				_cancellationTokenSource = null;
				_inspectionProgram?.Dispose(); // 释放 InspectionProgram 内部的图像资源
			}
			finally
			{
				if (owned)
					_inspectionLock.Dispose();
			}

			System.Diagnostics.Debug.WriteLine("InspectionService disposed.");
		}
	}
}
