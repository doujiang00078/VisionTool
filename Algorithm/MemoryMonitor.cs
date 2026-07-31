using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using VisionTool.Models;

namespace VisionTool.Algorithm
{
	/// <summary>
	/// 工作流内存监控器 — 在 WorkflowEngine 的每个任务前后采样进程内存
	/// </summary>
	public class MemoryMonitor
	{
		private readonly ConcurrentDictionary<string, StepMemoryStats> _stats = new();
		private long _frameStartPrivateBytes;
		private long _frameStartUnmanagedBytes;
		private int _frameCount;
		private readonly int _logInterval;
		private readonly long _alertThresholdBytes;

		/// <summary>
		/// 单步执行完成时触发
		/// </summary>
		public event Action<string, StepMemoryRecord> OnStepRecorded;

		/// <summary>
		/// 一轮工作流完成时触发（frameCount, unmanagedBytes, frameDelta）
		/// </summary>
		public event Action<int, long, long> OnWorkflowFrameFinished;

		/// <summary>
		/// 内存告警事件（taskName, deltaBytes）
		/// </summary>
		public event Action<string, long> OnMemoryWarning;

		public MemoryMonitor(int logInterval = 100, long alertThresholdBytes = 10 * 1024 * 1024)
		{
			_logInterval = logInterval;
			_alertThresholdBytes = alertThresholdBytes;
		}

		public static MemorySnapshot TakeSnapshot() => MemorySnapshot.Take();

		/// <summary>
		/// 记录一个步骤的内存增量（使用 GC 堆增量）
		/// </summary>
		public void RecordStep(StepMemoryRecord record)
		{
			var stats = _stats.GetOrAdd(record.TaskName, name => new StepMemoryStats { TaskName = name });
			stats.Record(record.DeltaGcBytes);
			stats.LastGcHeapBytes = record.After.GcHeapBytes;

			OnStepRecorded?.Invoke(record.TaskName, record);

			if (record.DeltaGcBytes > _alertThresholdBytes)
			{
				OnMemoryWarning?.Invoke(record.TaskName, record.DeltaGcBytes);
			}
		}

		/// <summary>
		/// 一轮工作流开始时调用
		/// </summary>
		public void OnWorkflowStart()
		{
			var snap = TakeSnapshot();
			_frameStartPrivateBytes = snap.PrivateBytes;
			_frameStartUnmanagedBytes = snap.UnmanagedBytes;
			_frameCount++;
		}

		/// <summary>
		/// 一轮工作流结束时调用，输出汇总日志
		/// </summary>
		public void OnWorkflowFinish()
		{
			var current = TakeSnapshot();
			var frameDelta = current.UnmanagedBytes - _frameStartUnmanagedBytes;

			OnWorkflowFrameFinished?.Invoke(_frameCount, current.UnmanagedBytes, frameDelta);

			if (_logInterval > 0 && _frameCount % _logInterval == 0)
			{
				LogSummary(current, frameDelta);
			}
		}

		private void LogSummary(MemorySnapshot current, long frameDelta)
		{
			Debug.WriteLine($"[内存监控] === 第 {_frameCount} 帧 === 非托管: {FormatBytes(current.UnmanagedBytes)}, GC堆: {FormatBytes(current.GcHeapBytes)}, 帧净增: {FormatBytes(frameDelta)}");

			var ordered = _stats.Values.OrderByDescending(s => Math.Abs(s.AverageRecentDelta)).ToList();
			foreach (var s in ordered)
			{
				var tag = s.IsGrowing ? " ⚠️ 持续增长" : "";
				Debug.WriteLine($"[内存监控]   {s.TaskName,-15} avg {FormatBytes((long)s.AverageRecentDelta)}, max {FormatBytes(s.MaxSingleDeltaBytes)}, count {s.SampleCount}{tag}");
			}
		}

		public ConcurrentDictionary<string, StepMemoryStats> GetAllStats() => _stats;

		public void Reset()
		{
			_frameCount = 0;
			_frameStartPrivateBytes = 0;
			_frameStartUnmanagedBytes = 0;
			foreach (var stats in _stats.Values) stats.Reset();
			_stats.Clear();
		}

		public int FrameCount => _frameCount;

		public static string FormatBytes(long bytes)
		{
			if (bytes < 0) return "N/A";
			if (bytes < 1024) return $"{bytes}B";
			if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1}KB";
			if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1}MB";
			return $"{bytes / (1024.0 * 1024 * 1024):F2}GB";
		}
	}
}
