using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace VisionTool.Models
{
	/// <summary>
	/// 单次内存采样快照
	/// </summary>
	public struct MemorySnapshot
	{
		/// <summary>
		/// 进程私有内存（PrivateMemorySize64，不含共享内存/映射文件）
		/// </summary>
		public long PrivateBytes;

		/// <summary>
		/// .NET GC 托管堆大小
		/// </summary>
		public long GcHeapBytes;

		/// <summary>
		/// 采样时间戳
		/// </summary>
		public long Timestamp;

		/// <summary>
		/// 非 GC 私有内存估算 = PrivateBytes - GcHeapBytes。
		/// 包含 HALCON、相机 SDK、线程栈和其他本机分配，不能单独等同于 HALCON 内存。
		/// </summary>
		public long UnmanagedBytes => PrivateBytes - GcHeapBytes;

		public static MemorySnapshot Take()
		{
			using var process = Process.GetCurrentProcess();
			return new MemorySnapshot
			{
				PrivateBytes = process.PrivateMemorySize64,
				GcHeapBytes = GC.GetTotalMemory(false),
				Timestamp = Stopwatch.GetTimestamp()
			};
		}
	}

	/// <summary>
	/// 单个步骤的内存增量记录
	/// </summary>
	public struct StepMemoryRecord
	{
		public string TaskName;
		public MemorySnapshot Before;
		public MemorySnapshot After;
		public double ElapsedMs;

		public long DeltaGcBytes => After.GcHeapBytes - Before.GcHeapBytes;
		public long DeltaNonGcPrivateBytes => After.UnmanagedBytes - Before.UnmanagedBytes;
	}

	/// <summary>
	/// 流水线节点的内存采样记录
	/// </summary>
	public struct PipelineMemoryRecord
	{
		public string StageName;
		public MemorySnapshot Snapshot;
		public double ElapsedMs;
	}

	/// <summary>
	/// 每步骤的滑动窗口统计（仅由串行工作流写入）。
	/// </summary>
	public class StepMemoryStats
	{
		public string TaskName { get; set; }
		public int SampleCount { get; private set; }
		public long LastGcHeapBytes { get; set; }
		public long LastPrivateBytes { get; private set; }
		public long LastNonGcPrivateBytes { get; private set; }

		private readonly object _lock = new();
		private readonly Queue<long> _recentDeltas = new();
		public const int WindowSize = 50;

		public void Record(long delta, MemorySnapshot after)
		{
			lock (_lock)
			{
				SampleCount++;
				LastPrivateBytes = after.PrivateBytes;
				LastGcHeapBytes = after.GcHeapBytes;
				LastNonGcPrivateBytes = after.UnmanagedBytes;
				_recentDeltas.Enqueue(delta);
				while (_recentDeltas.Count > WindowSize) _recentDeltas.Dequeue();
			}
		}

		public double AverageRecentDelta
		{
			get
			{
				lock (_lock) { return _recentDeltas.Count > 0 ? _recentDeltas.Average() : 0; }
			}
		}

		public bool IsGrowing
		{
			get
			{
				lock (_lock)
				{
					if (_recentDeltas.Count < 10) return false;
					var arr = _recentDeltas.ToArray();
					var recent = arr.Skip(arr.Length - 10);
					return recent.All(d => d > 0);
				}
			}
		}

		public List<long> GetRecentDeltas()
		{
			lock (_lock) { return new List<long>(_recentDeltas); }
		}

		public void Reset()
		{
			lock (_lock)
			{
				SampleCount = 0;
				LastPrivateBytes = 0;
				LastGcHeapBytes = 0;
				LastNonGcPrivateBytes = 0;
				_recentDeltas.Clear();
			}
		}
	}

	/// <summary>
	/// 流水线阶段的统计
	/// </summary>
	public class PipelineStageStats
	{
		public string StageName { get; set; }
		public long LastPrivateBytes { get; set; }
		public long LastGcHeapBytes { get; set; }
		public long LastUnmanagedBytes { get; set; }
		public long DeltaFromPrevious { get; set; }
		public int SampleCount { get; private set; }

		private readonly object _lock = new();

		public void Record(long privateBytes, long gcHeap, long unmanaged, long delta)
		{
			lock (_lock)
			{
				LastPrivateBytes = privateBytes;
				LastGcHeapBytes = gcHeap;
				LastUnmanagedBytes = unmanaged;
				DeltaFromPrevious = delta;
				SampleCount++;
			}
		}
	}
}
