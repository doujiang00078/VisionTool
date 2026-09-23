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
        /// 供UI调试的可视化对象（如区域、XLD）。
        /// **只读视图**：Dispose 时会被原子换出（下次读为 null），读方需在同一次读取里取到自己要用的引用。
        /// 注意：当前三个仓库里都没有写入方（主仓库与"自定义算法"仓库均未使用该字段，只有
        /// UI Presenter 在读），也就是说这条调试显示路径目前是空的——保留它只为将来接线。
        /// </summary>
        public List<HObject> DebugObjects => _debugObjects;

        /// <summary>
        /// 底层列表。Dispose 时先把它原子换出，避免"Pipeline 线程清空列表"与"UI 线程枚举"
        /// 同时发生（枚举中被 Clear 会抛 InvalidOperationException；更糟的是访问已释放的 HObject）。
        /// </summary>
        private List<HObject> _debugObjects = [];

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

        /// <summary>
        /// 帧内载荷：**只属于这一帧**的附加数据（例如"栅线类型 → 线区域列表"、定位用的 hTuples 容器），
        /// 供 UI 在帧回调里按"正在显示的那一帧的结果"取用（UI 拿到的就是这个对象）。
        /// </summary>
        /// <remarks>
        /// 为什么需要它：这类"本帧中间产物"过去被写在**跨帧复用**的 Param 上
        /// （`_param.LineResults`、`BusbarParam._lastHTuples`）。每制程并发跑 2 帧之后，UI 逐帧刷新时
        /// 可能读到**另一帧**刚写进去的值（现场表现为左侧列表/屏蔽框跟着另一帧跳），
        /// 而放在结果对象上，载荷天然跟着那一帧走。
        /// <para>
        /// 约定：本字段**不参与释放**——结果对象 <see cref="Dispose"/> 时不碰它。
        /// 载荷是非托管容器（HObject/HTuple）时，由产出方自己的结果容器（如 <see cref="Other"/>）负责生命周期，
        /// 这里只放引用；载荷是纯托管对象（如线区域列表）时交给 GC。
        /// </para>
        /// </remarks>
        public object FramePayload { get; set; }

        private int _disposed;

        public InspectionStepResult()
        {
            StepName = string.Empty;
            Reg = [];
            Other = [];
            Img = [];
            DefectiveReg = [];
            DefectiveOther = [];
            EndTimestamp = DateTime.Now;
            ElapsedMilliseconds = 0;
        }

        /// <summary>
        /// 取一份可在 UI 线程安全使用的调试对象副本（调用方拥有，负责 Dispose）。
        /// 返回 null 表示本结果已释放或没有可显示的对象。
        /// </summary>
        public List<HObject> TryCopyDebugObjects()
        {
            // 一次性取到引用：Dispose 可能随时把字段换成 null
            var snapshot = Volatile.Read(ref _debugObjects);
            if (snapshot == null || snapshot.Count == 0) return null;

            var copies = new List<HObject>(snapshot.Count);
            foreach (var obj in snapshot)
            {
                if (obj == null || !obj.IsInitialized()) continue;
                copies.Add(obj.CopyObj(1, -1));
            }
            return copies.Count > 0 ? copies : null;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            // 先原子换出列表再释放：UI 线程即便此刻正在枚举，也拿到的是换出前的那份引用，
            // 不会遇到"枚举中被清空"。
            var debugObjects = Interlocked.Exchange(ref _debugObjects, null);
            if (debugObjects != null)
            {
                foreach (var obj in debugObjects)
                {
                    obj?.Dispose();
                }
                debugObjects.Clear();
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