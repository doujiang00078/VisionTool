using System;

namespace VisionTool.Models
{
    /// <summary>
    /// 标注在参数对象的成员上：**本成员是"每帧产物"**（每次检测都会被本步骤重写）。
    ///
    /// 用途只有一个：让引擎的**共享参数突变检测**（<c>WorkflowEngine.OnSharedParameterMutated</c>）
    /// 忽略它 —— 否则每帧写它都会报一次"帧内改动了跨帧复用的参数"。
    ///
    /// <para>
    /// <b>标注它同时是一个承诺</b>：该字段的值只允许在**产生它的那一次步骤执行内**被消费。
    /// 一旦有别人（下游步骤、界面、另一个制程）要去读它，就必须改成从**帧上下文**取：
    /// <c>context.StepOutputs[步骤名]</c>（HObject/HTuple）或
    /// <c>InspectionStepResult.FramePayload</c>（任意对象）；
    /// 否则每制程并行跑多帧时会读到**另一帧**的值（不报错、不崩溃，只是结果/界面偶尔不对），
    /// 界面还可能拿到已被释放的 HObject。
    /// </para>
    ///
    /// <para>
    /// 反过来：**没有**标注的成员如果在帧执行期间被改写，说明它既不是纯配方参数、
    /// 又没走帧上下文 —— 这类写法正是串帧的来源，引擎会报出来让你二选一：
    /// 标注（并保证读取方走帧上下文），或者把数据搬进帧上下文。
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class FrameScopedAttribute : Attribute
    {
    }
}
