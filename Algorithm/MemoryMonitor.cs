using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using VisionTool.Models;

namespace VisionTool.Algorithm
{
	/// <summary>
	/// 工作流内存监控器 — 仅在串行工作流中记录各任务的进程非 GC 私有内存变化。
	/// </summary>
	public class MemoryMonitor
	{
		private readonly ConcurrentDictionary<string, StepMemoryStats> _stats = new();
		private readonly object _sessionLock = new();
		private long _frameStartPrivateBytes;
		private long _frameStartUnmanagedBytes;
		private int _frameCount;
		private readonly int _logInterval;
		private readonly long _alertThresholdBytes;
		private MemorySnapshot _sessionStartSnapshot;

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
			CaptureSessionStart();
		}

		public static MemorySnapshot TakeSnapshot() => MemorySnapshot.Take();

		/// <summary>
		/// 记录一个步骤的内存增量。调用方必须保证任务独占执行。
		/// </summary>
		public void RecordStep(StepMemoryRecord record)
		{
			var stats = _stats.GetOrAdd(record.TaskName, name => new StepMemoryStats { TaskName = name });
			stats.Record(record.DeltaNonGcPrivateBytes, record.After);

			OnStepRecorded?.Invoke(record.TaskName, record);

			if (record.DeltaNonGcPrivateBytes > _alertThresholdBytes)
			{
				OnMemoryWarning?.Invoke(record.TaskName, record.DeltaNonGcPrivateBytes);
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
			var sessionStart = SessionStartSnapshot;
			var totalGrowth = current.UnmanagedBytes - sessionStart.UnmanagedBytes;
			Debug.WriteLine($"[内存监控] === 第 {_frameCount} 帧 === 非GC私有内存开始值: {FormatBytes(sessionStart.UnmanagedBytes)}, 当前值: {FormatBytes(current.UnmanagedBytes)}, 累计净增长: {FormatSignedBytes(totalGrowth)}, GC堆: {FormatBytes(current.GcHeapBytes)}, 帧净增: {FormatSignedBytes(frameDelta)}");

			var ordered = _stats.Values.OrderByDescending(s => Math.Abs(s.AverageRecentDelta)).ToList();
			foreach (var s in ordered)
			{
				var tag = s.IsGrowing ? " ⚠️ 持续增长" : "";
				Debug.WriteLine($"[内存监控]   {s.TaskName,-15} 近50帧平均 {FormatBytes((long)s.AverageRecentDelta)}, count {s.SampleCount}{tag}");
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
			CaptureSessionStart();
		}

		public int FrameCount => _frameCount;

		/// <summary>
		/// 本次诊断会话开始时的进程内存快照。创建监控器和重置统计时均会重新采样。
		/// </summary>
		public MemorySnapshot SessionStartSnapshot
		{
			get
			{
				lock (_sessionLock) return _sessionStartSnapshot;
			}
		}

		private void CaptureSessionStart()
		{
			var snapshot = TakeSnapshot();
			lock (_sessionLock) _sessionStartSnapshot = snapshot;
		}

		public static string FormatBytes(long bytes)
		{
			if (bytes < 0) return "N/A";
			if (bytes < 1024) return $"{bytes}B";
			if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1}KB";
			if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1}MB";
			return $"{bytes / (1024.0 * 1024 * 1024):F2}GB";
		}

		private static string FormatSignedBytes(long bytes)
			=> bytes < 0 ? $"-{FormatBytes(-bytes)}" : FormatBytes(bytes);
	}
}
