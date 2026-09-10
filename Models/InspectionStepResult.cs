using HalconDotNet;
using System;
using System.Collections.Generic;
using System.Threading;

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
        public HObjectMap Img { get; set; }

        /// <summary>
        /// 区域
        /// </summary>
        public HObjectMap Reg { get; set; }

        /// <summary>
        /// 数值
        /// </summary>
        public DisposableMap<HTuple> Other { get; set; }

        /// <summary>
        ///    缺陷区域 
        /// </summary>
        public HObjectMap DefectiveReg { get; set; }

        /// <summary>
        /// 缺陷数值
        /// </summary>
        public DisposableMap<HTuple> DefectiveOther { get; set; }

        /// <summary>
        /// 执行过程中产生的临时对象（Operator 的 LocalImages/LocalRegions/LocalTuples），
        /// 仅用于生命周期管理，不参与数据传递。
        /// </summary>
        public List<IDisposable> Temporaries { get; set; } = [];

        private int _disposed;

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
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (DebugObjects != null)
            {
                foreach (var obj in DebugObjects)
                {
                    obj?.Dispose();
                }
                DebugObjects.Clear();
            }
            if (Temporaries != null)
            {
                foreach (var obj in Temporaries)
                {
                    obj?.Dispose();
                }
                Temporaries.Clear();
            }
            Reg?.Dispose();
            Other?.Dispose();
            Img?.Dispose();
            DefectiveReg?.Dispose();
            DefectiveOther?.Dispose();
        }
    }
}