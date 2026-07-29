using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace VisionTool.Models
{
	/// <summary>
	/// 通用可释放字典：管理 <c>Dictionary&lt;string, T&gt;</c> 的 Set/Get/Clear 生命周期，
	/// 在替换或清空时自动 Dispose 旧值，避免非托管资源泄漏。
	/// 线程安全：所有读写操作均通过内部锁同步。
	/// </summary>
	/// <typeparam name="T">值类型，必须是引用类型且实现 IDisposable</typeparam>
	public class DisposableMap<T> : IDisposable, ICollection<KeyValuePair<string, T>> where T : class, IDisposable
	{
		private readonly Dictionary<string, T> _items = new Dictionary<string, T>();
		private readonly object _lock = new object();
		private readonly bool _ownsValues;

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
		bool ICollection<KeyValuePair<string, T>>.Contains(KeyValuePair<string, T> item) { lock (_lock) return _items.TryGetValue(item.Key, out var val) && ReferenceEquals(val, item.Value); }
		void ICollection<KeyValuePair<string, T>>.CopyTo(KeyValuePair<string, T>[] array, int index) { lock (_lock) ((ICollection<KeyValuePair<string, T>>)_items).CopyTo(array, index); }
		bool ICollection<KeyValuePair<string, T>>.Remove(KeyValuePair<string, T> item) { if (item.Value is null) return false; return Remove(item.Key); }

		/// <summary>
		/// 存入值。若 key 已存在且值引用相同则跳过；否则先 Dispose 旧值再替换。
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
						existing?.Dispose();
				}
				_items[key] = value;
			}
		}

		/// <summary>
		/// 获取值，找不到时返回 null。
		/// </summary>
		public T Get(string key)
		{
			lock (_lock)
			{
				_items.TryGetValue(key, out T value);
				return value;
			}
		}

		/// <summary>
		/// 判断是否包含指定 key。
		/// </summary>
		public bool ContainsKey(string key) { lock (_lock) return _items.ContainsKey(key); }

		/// <summary>
		/// 尝试获取值，找不到时返回 false。
		/// </summary>
		public bool TryGetValue(string key, out T value) { lock (_lock) return _items.TryGetValue(key, out value); }

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
						existing?.Dispose();
					_items.Remove(key);
					return true;
				}
				return false;
			}
		}

		/// <summary>
		/// 批量存入。先 Clear 当前所有项，再逐个 Set。
		/// </summary>
		public void SetAll(IEnumerable<KeyValuePair<string, T>> source)
		{
			Clear();
			if (source is null) return;
			foreach (var kvp in source)
				Set(kvp.Key, kvp.Value);
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
						kvp.Value?.Dispose();
				}
				_items.Clear();
			}
		}

		/// <summary>
		/// 当前存储的元素数量。
		/// </summary>
		public int Count { get { lock (_lock) return _items.Count; } }

		/// <summary>
		/// 索引器：get 等价于 Get，set 等价于 Set。
		/// </summary>
		public T this[string key]
		{
			get => Get(key);
			set => Set(key, value);
		}

		/// <summary>
		/// 获取所有 key 的快照。
		/// </summary>
		public IEnumerable<string> Keys { get { lock (_lock) return _items.Keys.ToArray(); } }

		/// <summary>
		/// 获取所有 value 的快照。
		/// </summary>
		public IEnumerable<T> Values { get { lock (_lock) return _items.Values.ToArray(); } }

		/// <summary>
		/// 复制一份快照为普通 Dictionary（不转移所有权，不 Dispose）。
		/// 用于需要传给接受 Dictionary 的外部接口。
		/// </summary>
		public Dictionary<string, T> ToDictionary()
		{
			lock (_lock) return new Dictionary<string, T>(_items);
		}

		public IEnumerator<KeyValuePair<string, T>> GetEnumerator()
		{
			lock (_lock) return _items.ToArray().AsEnumerable().GetEnumerator();
		}
		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

		public void Dispose() => Clear();
	}
}
