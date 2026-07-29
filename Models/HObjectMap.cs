using HalconDotNet;

namespace VisionTool.Models
{
	/// <summary>
	/// 专门管理 <c>Dictionary&lt;string, HObject&gt;</c> 的容器。
	/// 重写 Get 以额外检查 HObject.IsInitialized()，避免返回已失效的对象。
	/// </summary>
	public class HObjectMap : DisposableMap<HObject>
	{
		public HObjectMap(bool ownsValues = true) : base(ownsValues)
		{
		}

		/// <summary>
		/// 创建不接管 HObject 生命周期的输入视图。
		/// 算法步骤只能用它传递上游图像/区域，结果必须写入默认构造的拥有型 HObjectMap。
		/// </summary>
		public static HObjectMap CreateInputView()
		{
			return new HObjectMap(false);
		}

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
