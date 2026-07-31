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
		/// 内存监控器（可空，未启用时为 null）
		/// </summary>
		public MemoryMonitor MemoryMonitor { get; private set; }

		/// <summary>
		/// 取消令牌
		/// </summary>
		private CancellationTokenSource _cancellationTokenSource;

		/// <summary>
		/// 当工作流任务状态变化时触发，供UI订阅。
		/// </summary>
		public event Action<WorkflowTaskStatus> OnWorkflowTaskStatusChanged;

		/// <summary>
		/// 当所有任务完成时触发
		/// </summary>
		public event Action<IEnumerable<WorkflowTaskStatus>, long> OnWorkflowTaskFinishStatus;

		public InspectionService(Dictionary<string, BaseParam> initialAlgorithmParameters, List<IWorkflowTask> inspectionSteps, HObject modeImage, string path)
		{
			_inspectionProgram = new InspectionProgram(initialAlgorithmParameters, inspectionSteps, modeImage, path);
			_inspectionProgram.OnWorkflowTaskStatusChanged += (status) => OnWorkflowTaskStatusChanged?.Invoke(status);
			_inspectionProgram.OnWorkflowTaskFinishStatus += (status, time) => OnWorkflowTaskFinishStatus?.Invoke(status, time);
		}

		/// <summary>
		/// 启动实时检测（生产模式）。
		/// </summary>
		/// <param name="currentImage">当前图像</param>
		/// <param name="timeoutMs">超时时间（毫秒），0表示不设超时</param>
		/// <param name="runType">运行类型</param>
		public async Task StartLiveInspection(HObject currentImage, int timeoutMs = 0, RunType runType = RunType.当前图)
		{
			_cancellationTokenSource?.Cancel(); // 取消之前的操作
			_cancellationTokenSource?.Dispose();

			// 根据超时参数创建 CancellationTokenSource
			if (timeoutMs > 0)
			{
				_cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
				System.Diagnostics.Debug.WriteLine($"InspectionService: 开始实时检测，超时时间: {timeoutMs}ms.");
			}
			else
			{
				_cancellationTokenSource = new CancellationTokenSource();
				System.Diagnostics.Debug.WriteLine("InspectionService: 开始实时检测（无超时限制）.");
			}

			await _inspectionProgram.StartInspectionAsync(currentImage, _cancellationTokenSource.Token, runType);
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
			_cancellationTokenSource?.Cancel(); // 取消之前的操作
			_cancellationTokenSource?.Dispose();

			// 根据超时参数创建 CancellationTokenSource
			if (timeoutMs > 0)
			{
				_cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
				System.Diagnostics.Debug.WriteLine($"InspectionService: 在调试模式下运行任务“{runToTaskName}”，超时时间: {timeoutMs}ms.");
			}
			else
			{
				_cancellationTokenSource = new CancellationTokenSource();
				System.Diagnostics.Debug.WriteLine($"InspectionService: 在调试模式下运行任务“{runToTaskName}”（无超时限制）.");
			}

			await _inspectionProgram.RunToStepAsync(debugImage, runToTaskName, _cancellationTokenSource.Token, runType);
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
			_cancellationTokenSource?.Cancel(); // 取消之前的操作
			_cancellationTokenSource?.Dispose();

			// 根据超时参数创建 CancellationTokenSource
			if (timeoutMs > 0)
			{
				_cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
				System.Diagnostics.Debug.WriteLine($"InspectionService: 正在执行单步“{taskName}”，超时时间: {timeoutMs}ms.");
			}
			else
			{
				_cancellationTokenSource = new CancellationTokenSource();
				System.Diagnostics.Debug.WriteLine($"InspectionService: 正在执行单步“{taskName}”（无超时限制）.");
			}

			await _inspectionProgram.ExecuteSingleStepAsync(taskName, debugImage, _cancellationTokenSource.Token, runType);
		}

		/// <summary>
		/// 停止当前正在进行的工作流。
		/// </summary>
		public void StopInspection()
		{
			_cancellationTokenSource?.Cancel();
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
		/// 获取指定任务的算法参数列表。
		/// </summary>
		/// <param name="taskName">任务名称（如"LocateObject"）</param>
		public BaseParam GetTaskAlgorithmParameters(string taskName)
		{
			return _inspectionProgram.GetAlgorithmParameters(taskName);
		}

		/// <summary>
		/// 更新算法参数。
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
		/// 启用内存监控，追踪每个检测步骤的 Halcon 原生内存增长
		/// </summary>
		/// <param name="logInterval">每 N 帧输出一次汇总日志（默认 100）</param>
		/// <param name="alertThresholdBytes">单步增量告警阈值（默认 10MB）</param>
		public void EnableMemoryMonitor(int logInterval = 100, long alertThresholdBytes = 10 * 1024 * 1024)
		{
			MemoryMonitor = new MemoryMonitor(logInterval, alertThresholdBytes);
			_inspectionProgram.SetMemoryMonitor(MemoryMonitor);
		}

		/// <summary>
		/// 获取内存监控统计（返回 null 如果未启用）
		/// </summary>
		public System.Collections.Concurrent.ConcurrentDictionary<string, StepMemoryStats> GetMemoryStats()
		{
			return MemoryMonitor?.GetAllStats();
		}

		/// <summary>
		/// 设置串行执行模式：任务按拓扑顺序逐个执行，per-step 内存增量精确归属
		/// </summary>
		public void SetSerialExecution(bool serial)
		{
			_inspectionProgram.SetSerialExecution(serial);
		}

		/// <summary>
		/// 释放资源
		/// </summary>
		public void Dispose()
		{
			_cancellationTokenSource?.Dispose();
			_inspectionProgram?.Dispose(); // 释放 InspectionProgram 内部的图像资源
			System.Diagnostics.Debug.WriteLine("InspectionService disposed.");
		}
	}
}