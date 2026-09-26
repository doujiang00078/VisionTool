using System;

namespace VisionTool.Models
{
	/// <summary>
	/// HALCON 非托管值的借用/拥有包装。
	/// <see cref="Borrowed"/> 不负责 Dispose；<see cref="Owned"/> / <see cref="Transferred"/>
	/// 在未调用 <see cref="TakeOwnership"/> 前由本包装释放。
	/// </summary>
	public sealed class NativeValue<T> : IDisposable where T : class, IDisposable
	{
		private bool _ownsValue;

		public T Value { get; private set; }

		/// <summary>本包装当前是否负责 Dispose。</summary>
		public bool OwnsValue => _ownsValue;

		private NativeValue(T value, bool ownsValue)
		{
			Value = value;
			_ownsValue = ownsValue;
		}

		/// <summary>借用上游/共享对象，本包装不释放。</summary>
		public static NativeValue<T> Borrowed(T value) => new(value, ownsValue: false);

		/// <summary>本包装拥有新建对象，负责释放（除非 TakeOwnership）。</summary>
		public static NativeValue<T> Owned(T value) => new(value, ownsValue: true);

		/// <summary>从容器 TryTake 出来的对象，所有权转入本包装。</summary>
		public static NativeValue<T> Transferred(T value) => new(value, ownsValue: true);

		/// <summary>
		/// 所有权已交给接收方（Operator.SetImage/SetRegion/SetTuple 等）。
		/// 本包装不再 Dispose；由接收方的 Clear/结果容器负责。
		/// </summary>
		public void TakeOwnership()
		{
			_ownsValue = false;
		}

		public void Dispose()
		{
			if (_ownsValue)
			{
				try { Value?.Dispose(); }
				catch { /* 释放路径不抛 */ }
			}
			Value = null;
			_ownsValue = false;
		}
	}
}
