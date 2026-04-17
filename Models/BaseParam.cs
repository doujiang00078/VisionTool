using Newtonsoft.Json;
using System;

namespace VisionTool.Models
{
	/// <summary>
	/// 算法参数基类
	/// 提供参数序列化和深克隆功能
	/// 所有算法参数类都应继承此基类
	/// </summary>
	[JsonObject(MemberSerialization.OptIn)]
	public class BaseParam : ICloneable
	{
		/// <summary>
		/// 创建当前参数对象的深度克隆副本
		/// 使用 JSON 序列化/反序列化实现深度克隆，确保所有嵌套对象都被正确复制
		/// </summary>
		/// <returns>当前对象的深度克隆副本</returns>
		/// <exception cref="InvalidOperationException">当序列化或反序列化失败时抛出</exception>
		public virtual BaseParam DeepClone()
		{
			try
			{
				// 使用 JSON 序列化设置，保留类型信息以支持多态克隆
				var settings = new JsonSerializerSettings
				{
					TypeNameHandling = TypeNameHandling.Auto,
					PreserveReferencesHandling = PreserveReferencesHandling.Objects,
					ReferenceLoopHandling = ReferenceLoopHandling.Ignore
				};

				// 序列化为 JSON 字符串
				string json = JsonConvert.SerializeObject(this, settings);

                // 反序列化为新对象（深度克隆）
                var clonedObject = JsonConvert.DeserializeObject(json, this.GetType(), settings);
                
                return clonedObject as BaseParam;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"深克隆失败: {ex.Message}", ex);
            }
        }

		/// <summary>
		/// 创建当前参数对象的深度克隆副本（泛型版本）
		/// 使用 JSON 序列化/反序列化实现深度克隆，确保所有嵌套对象都被正确复制
		/// </summary>
		/// <typeparam name="T">参数类型，必须继承自 BaseParam</typeparam>
		/// <returns>指定类型的深度克隆副本</returns>
		/// <exception cref="InvalidOperationException">当序列化或反序列化失败时抛出</exception>
		public virtual T DeepClone<T>() where T : BaseParam
		{
			try
			{
				// 使用 JSON 序列化设置，保留类型信息以支持多态克隆
				var settings = new JsonSerializerSettings
				{
					TypeNameHandling = TypeNameHandling.Auto,
					PreserveReferencesHandling = PreserveReferencesHandling.Objects,
					ReferenceLoopHandling = ReferenceLoopHandling.Ignore
				};

				// 序列化为 JSON 字符串
				string json = JsonConvert.SerializeObject(this, settings);

				// 反序列化为指定类型的新对象（深度克隆）
				return JsonConvert.DeserializeObject<T>(json, settings);
			}
			catch (JsonException ex)
			{
				throw new InvalidOperationException($"深克隆失败: {ex.Message}", ex);
			}
		}

		/// <summary>
		/// 实现 ICloneable 接口的 Clone 方法
		/// 调用 DeepClone 方法创建深度副本
		/// </summary>
		/// <returns>当前对象的深度克隆副本</returns>
		object ICloneable.Clone()
		{
			return DeepClone();
		}

        /// <summary>
        /// 公开的克隆方法，提供更好的类型安全性
        /// </summary>
        /// <returns>当前对象的深度克隆副本</returns>
        public BaseParam Clone()
        {
            return DeepClone();
        }
    }
}
