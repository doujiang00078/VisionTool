using System;
using System.ComponentModel;
using System.Reflection;

namespace VisionTool.Enums
{
    /// <summary>
    ///  模块名称
    /// </summary>
    public class ModuleName
    {
        public enum 成品
        {
            /// <summary>
            /// 破损检测
            /// </summary>
            [Description("破损检测")]
            Breakage = 0,

            /// <summary>
            /// 色深检测
            /// </summary>
            [Description("色深检测")]
            ColorDepth = 1,

            /// <summary>
            /// 色差检测
            /// </summary>
            [Description("色差检测")]
            ColorDifference = 2,

            /// <summary>
            /// 脏污检测
            /// </summary>
            [Description("脏污检测")]
            DirtyDetection = 3,

            /// <summary>
            /// 栅线检测
            /// </summary>
            [Description("栅线检测")]
            GridInspection = 4,

            /// <summary>
            /// 栅线提取
            /// </summary>
            [Description("栅线提取")]
            GridLineModule = 5,

            /// <summary>
            /// 线痕检测
            /// </summary>
            [Description("线痕检测")]
            LineMarkDetection = 6,

            /// <summary>
            /// 定位测量
            /// </summary>
            [Description("定位测量")]
            Position = 7,

            /// <summary>
            /// 色斑检测
            /// </summary>
            [Description("色斑检测")]
            StainDetection = 8,
        }

        public enum BC丝网
        {
            /// <summary>
            /// BC定位
            /// </summary>
            [Description("BC定位")]
            Position = 0,

            [Description("BC细栅")]
             FGridModule = 2
        }

        public enum 铝舟
        {
            /// <summary>
            /// AI检测
            /// </summary>
            [Description("AI检测")]
            AIDetection = 0
        }
    }

    public static class EnumExtensions
    {
        /// <summary>
        /// 枚举 → 中文
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static string GetDescription(this Enum value)
        {
            var field = value.GetType().GetField(value.ToString());
            var attribute = field?.GetCustomAttribute<DescriptionAttribute>();
            return attribute?.Description ?? value.ToString();
        }

        /// <summary>
        /// 中文 → 枚举（安全版）
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="description"></param>
        /// <param name="result"></param>
        /// <returns></returns>
        public static bool TryFromDescription<T>(string description, out T result) where T : struct, Enum
        {
            foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var attr = field.GetCustomAttribute<DescriptionAttribute>();
                if (attr?.Description == description)
                {
                    result = (T)field.GetValue(null)!;
                    return true;
                }
            }

            result = default;
            return false;
        }
    }
}