using HalconDotNet;

namespace VisionTool.Models
{
	/// <summary>
	/// 专门管理 <c>Dictionary&lt;string, HObject&gt;</c> 的容器。
	/// 重写 Get 以额外检查 HObject.IsInitialized()，避免返回已失效的对象。
	/// </summary>
	public class HObjectMap : DisposableMap<HObject>
	{
		/// <summary>
		/// 获取 HObject，找不到或未初始化时返回 null。
		/// </summary>
		public new HObject Get(string key)
		{
			var obj = base.Get(key);
			if (obj != null && obj.IsInitialized())
				return obj;
			return null;
		}

		/// <summary>
		/// 索引器：get 带 IsInitialized 检查，set 等价于 Set。
		/// </summary>
		public new HObject this[string key]
		{
			get => Get(key);
			set => Set(key, value);
		}
	}
}
