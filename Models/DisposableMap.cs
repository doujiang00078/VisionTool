using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace VisionTool.Models
{
	/// <summary>
	/// 通用可释放字典：管理 <c>Dictionary&lt;string, T&gt;</c> 的 Set/Get/Clear 生命周期，
	/// 在替换或清空时自动 Dispose 旧值，避免非托管资源泄漏。
	///
	/// 线程安全策略（性能优化版）：
	/// - 读操作（Get/TryGetValue/ContainsKey）从不可变快照 _snapshot 无锁读取，
	///   算法执行阶段 4+ 并发任务同时读 context.Image 不再被单锁序列化。
	/// - 写操作（Set/Remove）仍通过 _lock 修改 _items，不影响并发读者。
	/// - 批量替换（SetAll/Clear）在锁内原子替换 _snapshot，此时无并发读者（帧间）。
	/// </summary>
	/// <typeparam name="T">值类型，必须是引用类型；若实现了 IDisposable，则在替换/清空时自动释放（Halcon12 的 HTuple 不实现 IDisposable，故约束不强制）</typeparam>
	public class DisposableMap<T> : IDisposable, ICollection<KeyValuePair<string, T>> where T : class
	{
		private readonly Dictionary<string, T> _items = new Dictionary<string, T>();
		private readonly object _lock = new object();
		private readonly bool _ownsValues;

		/// <summary>
		/// 不可变快照：读操作从此字段无锁读取。
		/// 仅在 SetAll/Clear（帧间）时原子替换，Set 期间不更新（Set 在执行期极少见）。
		/// </summary>
		private volatile Dictionary<string, T> _snapshot = new Dictionary<string, T>();

		/// <summary>
		/// 创建一个对象容器。默认容器拥有并负责释放其中的值；
		/// <paramref name="ownsValues"/> 为 false 时仅作为上游输出的只读引用视图，
		/// 不得用于保存本步骤产生的结果。
		/// </summary>
		public DisposableMap(bool ownsValues = true)
		{
			_ownsValues = ownsValues;
		}

		// ── ICollection 接口（支持 [] 初始化语法）──
		bool ICollection<KeyValuePair<string, T>>.IsReadOnly => false;

		/// <summary>
		/// 按 key 存入值（支持集合初始化器：var map = [ ("k", v) ]）。
		/// 若 key 已存在且值引用相同则跳过；否则先 Dispose 旧值再替换。
		/// </summary>
		public void Add(string key, T value) => Set(key, value);

		void ICollection<KeyValuePair<string, T>>.Add(KeyValuePair<string, T> item) => Set(item.Key, item.Value);
		bool ICollection<KeyValuePair<string, T>>.Contains(KeyValuePair<string, T> item) { return _snapshot.TryGetValue(item.Key, out var val) && ReferenceEquals(val, item.Value); }
		void ICollection<KeyValuePair<string, T>>.CopyTo(KeyValuePair<string, T>[] array, int index) { ((ICollection<KeyValuePair<string, T>>)_snapshot).CopyTo(array, index); }
		bool ICollection<KeyValuePair<string, T>>.Remove(KeyValuePair<string, T> item) { if (item.Value is null) return false; return Remove(item.Key); }

		/// <summary>
		/// 存入值。若 key 已存在且值引用相同则跳过；否则先 Dispose 旧值再替换。
		/// 更新 _snapshot 以保证后续 Get 立即可见。
		/// </summary>
		public void Set(string key, T value)
		{
			lock (_lock)
			{
				if (_items.TryGetValue(key, out T existing))
				{
					if (ReferenceEquals(existing, value))
						return;
					if (_ownsValues)
						(existing as IDisposable)?.Dispose();
				}
				_items[key] = value;
				_snapshot = new Dictionary<string, T>(_items);
			}
		}

		/// <summary>
		/// 获取值，找不到时返回 null。
		/// 从不可变快照无锁读取。
		/// </summary>
		public T Get(string key)
		{
			_snapshot.TryGetValue(key, out var value);
			return value;
		}

		/// <summary>
		/// 判断是否包含指定 key。
		/// </summary>
		public bool ContainsKey(string key) => _snapshot.ContainsKey(key);

		/// <summary>
		/// 尝试获取值，找不到时返回 false。
		/// </summary>
		public bool TryGetValue(string key, out T value) => _snapshot.TryGetValue(key, out value);

		/// <summary>
		/// 移除并 Dispose 指定 key 的值，若存在的话。
		/// </summary>
		public bool Remove(string key)
		{
			lock (_lock)
			{
				if (_items.TryGetValue(key, out T existing))
				{
					if (_ownsValues)
						(existing as IDisposable)?.Dispose();
					_items.Remove(key);
					_snapshot = new Dictionary<string, T>(_items);
					return true;
				}
				return false;
			}
		}

		/// <summary>
		/// 批量存入。一次性加锁：先 Dispose 旧值并清空，再逐个写入新值。
		/// 【性能优化】在锁内原子替换 _snapshot，后续 Get/TryGetValue 无锁读取。
		/// </summary>
		public void SetAll(IEnumerable<KeyValuePair<string, T>> source)
		{
			lock (_lock)
			{
				if (_ownsValues)
				{
					foreach (var kvp in _items)
						(kvp.Value as IDisposable)?.Dispose();
				}
				_items.Clear();
				if (source != null)
				{
					foreach (var kvp in source)
						_items[kvp.Key] = kvp.Value;
				}
				_snapshot = new Dictionary<string, T>(_items);
			}
		}

		/// <summary>
		/// 批量合并。逐个 Set，已有 key 会 Dispose 旧值后覆盖，不丢失现有未命中的项。
		/// </summary>
		public void AddRange(IEnumerable<KeyValuePair<string, T>> source)
		{
			foreach (var kvp in source)
				Set(kvp.Key, kvp.Value);
		}

		/// <summary>
		/// 遍历 Dispose 所有值后清空字典。
		/// </summary>
		public void Clear()
		{
			lock (_lock)
			{
				if (_ownsValues)
				{
					foreach (var kvp in _items)
						(kvp.Value as IDisposable)?.Dispose();
				}
				_items.Clear();
				_snapshot = new Dictionary<string, T>();
			}
		}

		/// <summary>
		/// 当前存储的元素数量。
		/// </summary>
		public int Count
		{
			get
			{
				var snap = _snapshot;
				return snap.Count;
			}
		}

		/// <summary>
		/// 索引器：get 等价于 Get，set 等价于 Set。
		/// </summary>
		public T this[string key]
		{
			get => Get(key);
			set => Set(key, value);
		}

		/// <summary>
		/// 获取所有 key（从快照读取，无锁、无额外分配）。
		/// </summary>
		public IEnumerable<string> Keys => _snapshot.Keys;

		/// <summary>
		/// 获取所有 value（从快照读取，无锁、无额外分配）。
		/// </summary>
		public IEnumerable<T> Values => _snapshot.Values;

		/// <summary>
		/// 复制一份快照为普通 Dictionary（不转移所有权，不 Dispose）。
		/// 用于需要传给接受 Dictionary 的外部接口。
		/// </summary>
		public Dictionary<string, T> ToDictionary()
		{
			return new Dictionary<string, T>(_snapshot);
		}

		public IEnumerator<KeyValuePair<string, T>> GetEnumerator()
		{
			return _snapshot.GetEnumerator();
		}
		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

		public void Dispose() => Clear();
	}
}
