using HalconDotNet;
using System;
using System.Collections.Generic;

namespace VisionTool.Models
{
    /// <summary>
    /// 单个算法步骤的执行结果，包含业务数据和可视化HObject。
    /// </summary>
    public class InspectionStepResult : IDisposable
    {
        /// <summary>
        /// 算法执行名称
        /// </summary>
        public string StepName { get; set; }

        /// <summary>
        /// 是否成功
        /// </summary>
        public bool IsSuccess { get; set; }

        /// <summary>
        /// 消息
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// 结束时间
        /// </summary>
        public DateTime EndTimestamp { get; set; }

        /// <summary>
        /// 算法耗时（毫秒）
        /// </summary>
        public long ElapsedMilliseconds { get; set; }

        /// <summary>
        /// 供UI调试的可视化对象（如区域、XLD）
        /// </summary>
        public List<HObject> DebugObjects { get; set; }

        /// <summary>
        /// 图像
        /// </summary>
        public Dictionary<string, HObject> Img { get; set; }

        /// <summary>
        /// 区域
        /// </summary>
        public Dictionary<string, HObject> Reg { get; set; }

        /// <summary>
        /// 数值
        /// </summary>
        public Dictionary<string, HTuple> Other { get; set; }

        /// <summary>
        ///    缺陷区域 
        /// </summary>
        public Dictionary<string, HObject> DefectiveReg { get; set; }

        /// <summary>
        /// 缺陷数值
        /// </summary>
        public Dictionary<string, HTuple> DefectiveOther { get; set; }

        public InspectionStepResult()
        {
            StepName = string.Empty;
            DebugObjects = [];
            Reg = [];
            Other = [];
            Img = [];
            DefectiveReg = [];
            DefectiveOther = [];
            EndTimestamp = DateTime.Now;
            ElapsedMilliseconds = 0;
        }

        public void Dispose()
        {
            if (DebugObjects != null)
            {
                foreach (var obj in DebugObjects)
                {
                    obj?.Dispose(); // 释放所有 DebugObjects
                }
                DebugObjects.Clear();
            }
            if (Reg != null)
            {
                foreach (var item in Reg)
                {
                    item.Value?.Dispose();
                }
            }
            Reg.Clear();
            if (Other != null)
            {
                foreach (var item in Other)
                {
                    item.Value?.Dispose();
                }
            }
            Other.Clear();
            if (Img != null)
            {
                foreach (var item in Img)
                {
                    item.Value?.Dispose();
                }
            }
            Img.Clear();
            if (DefectiveReg != null)
            {
                foreach (var item in DefectiveReg)
                {
                    item.Value?.Dispose();
                }
                DefectiveReg.Clear();
            }
            if (DefectiveOther != null)
            {
                foreach (var item in DefectiveOther)
                {
                    item.Value?.Dispose();
                }
                DefectiveOther.Clear();
            }
        }
    }
}