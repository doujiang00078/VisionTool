using System.Collections.Generic;
using System.Linq;

namespace VisionTool.Models
{
    /// <summary>
    /// 缺陷名称集合，按区域类和数值类分类存放
    /// </summary>
    public class DefectNameCollection
    {
        /// <summary>
        /// 区域类缺陷名称列表
        /// </summary>
        public List<string> AreaNames { get; set; } = [];

        /// <summary>
        /// 数值类缺陷名称列表
        /// </summary>
        public List<string> ValueNames { get; set; } = [];

        /// <summary>
        /// 所有缺陷名称列表
        /// </summary>
        public List<string> AllNames => [.. AreaNames, .. ValueNames];

        /// <summary>
        /// 合并多个 DefectNameCollection，去重
        /// </summary>
        public static DefectNameCollection Merge(IEnumerable<DefectNameCollection> collections)
        {
            var result = new DefectNameCollection();
            foreach (var c in collections)
            {
                result.AreaNames.AddRange(c.AreaNames);
                result.ValueNames.AddRange(c.ValueNames);
            }
            result.AreaNames = result.AreaNames.Distinct().ToList();
            result.ValueNames = result.ValueNames.Distinct().ToList();
            return result;
        }
    }
}
