# VisionTool 项目说明

> 仓库：`E:\AOI\VisionTool`（独立 git 仓库，分支 `main`）
> 产出：`VisionTool.dll`（net48 类库，WPF + WinForms 混用）
> 相关文档：[VisionTool-Halcon12适配记录.md](./VisionTool-Halcon12适配记录.md)

---

## 1. 一句话定位

VisionTool 是一个**视觉检测「工作流框架」类库**，本身**不含任何具体检测算法**。它提供：

1. 把多个检测模块组织成**有依赖关系的任务图（DAG）**并调度执行；
2. 统一管理 HALCON 非托管对象（`HObject` / `HTuple`）的生命周期；
3. 统一算法参数序列化与结果回传协议；
4. 提供每模块调试界面（MVP）的基类。

具体缺陷算法留在各检测模块里（`Breakage`、`BCMainPole`、`Position`、`StainDetection`、`ColorDifference`、`GridLineModule` 等）。

## 2. 在系统中的位置

```
wh_multi_maincutter(-12-合并) 主程序
   └─ 各检测模块工程（BCMainPole / Breakage / Position / ...）
          └─ <Reference Include="VisionTool"> → Mydll\VisionTool.dll   ← 本项目的产物
                 └─ 各模块写自己的 XxxStep : BaseWorkflowTask，插进 WorkflowEngine
```

**只以 DLL 形式被引用**，不是主程序的源码工程。主程序侧路径：`wh_multi_maincutter\Mydll\VisionTool.dll`（Halcon18）或 `wh_multi_maincutter-12-合并\Mydll\VisionTool.dll`（Halcon12）。

## 3. 依赖

| 依赖 | 说明 |
|---|---|
| `halcondotnet` | HALCON 的 .NET 封装。**版本决定本项目编出哪个版本**（见 §6） |
| `MstPicboxTool` / `MstImagelib` | 上位机自研的 WinForms 图像显示 / 图像处理库（`PicboxControl` 就来自这里） |
| `Newtonsoft.Json` | 参数序列化（中央包管理，版本在 `Directory.Packages.props`） |
| `VisionTool.Enums`（ProjectReference） | 模块名 / 配方类型枚举 |
| `WHWpf.Theming`（ProjectReference） | WPF 主题 |

## 4. 目录与文件职责

| 路径 | 行数 | 职责 |
|---|---|---|
| **Algorithm/WorkflowEngine.cs** | 861 | **DAG 调度引擎**（本项目核心）。按 `Dependencies` 入度驱动、`Task.Run` 并行执行；支持取消 / 超时 / 串行模式（`ForceSerialExecution`）/ `alwaysRun`（上游失败仍执行）/ `runToTaskName`（运行到某步）。做了大量并发与内存安全优化：线程池预热、入度数组复用、状态通知批量队列、帧间 `_snapshotOutputs` + `_previousOutputs` 延迟释放（防并发 use-after-free） |
| **Algorithm/InspectionProgram.cs** | 431 | **流程组装入口**。注册任务、初始化参数、持有模板图；三种运行模式：`StartInspectionAsync`（实时）/ `RunToStepAsync`（运行到某步）/ `ExecuteSingleStepAsync`（单步）。`BuildImageChannelDictionary` 把输入图统一成 4 通道字典（红/绿/蓝/暗红） |
| Algorithm/BaseWorkflowTask.cs | 94 | 任务抽象基类：`Name` / `Dependencies` / `State` / `_latestResult` |
| **Services/InspectionService.cs** | 277 | **对 UI 的门面**。`SemaphoreSlim` 调用级并发锁、CTS 超时/取消、参数与结果转发 |
| Interfaces/IWorkflowTask.cs | 67 | 任务契约：`ExecuteAsync` / `GetLatestResult` / `Initialize` / `CreateDefaultParameters` / `GetDefectAlgorithmName` |
| Interfaces/IAlgorithmTaskPresenter.cs | 164 | 子界面 Presenter 契约 + `InspectionServiceContext`（注入给 Presenter，避免它依赖整个 `InspectionService`） |
| Interfaces/IAlgorithmTaskView.cs | 77 | 子界面 View 契约（子界面本身是 WinForms `UserControl`） |
| **Models/WorkflowExecutionContext.cs** | 224 | **跨任务共享上下文**：`ModeImage` / `CurrentImage` / `Image`(HObjectMap) / `Parameters` / `Parameters1` / `StepOutputs` |
| **Models/InspectionStepResult.cs** | 115 | **单步结果**：`Img` / `Reg` / `Other` / `DefectiveReg` / `DefectiveOther` / `DebugObjects` / `Temporaries`，实现 `IDisposable`，带防重入标记 |
| **Models/DisposableMap.cs** | 212 | **线程安全可释放字典**：读走不可变快照（无锁）、写走锁；替换/清空时自动 `Dispose` 旧值。专门治 HALCON 内存泄漏 |
| Models/HObjectMap.cs | 44 | `Dictionary<string, HObject>` 专用容器，`Get` 额外做 `IsInitialized()` 检查；`CreateInputView()` 生成不接管所有权的只读视图 |
| Models/BaseParam.cs | 96 | 所有算法参数基类，提供 JSON 深度克隆（`DeepClone`） |
| Models/GlobalParameter.cs / DefectNameCollection.cs / WorkflowTaskStatus.cs | 44/42/36 | 全局参数（含模板图）/ 缺陷名集合（分区域类、数值类）/ 任务状态快照 |
| Enums/RunType.cs | 8 | `模板图` / `当前图` |
| Enums/WorkflowState.cs | 43 | `Pending` / `Ready` / `Running` / `Completed` / `Failed` / `Skipped` / `Cancelled` |
| UI/Presenters/BaseAlgorithmTaskPresenter.cs | 421 | **MVP 基类**：子模块调试界面（运行到此任务、离线加载、单步、参数同步、主图叠加显示） |
| UI/Views/IAlgorithmDebugView.cs | 127 | 主调试视图契约 |
| BaseTool.cs / VisionToolBase.cs | 115/57 | 更早的工具基类（`Save`/`Load`/`Backup`/`RollBack`），保留兼容 |
| ProgramType.cs / MedianCalculator.cs | 9/102 | 枚举 / 中值计算小工具 |

## 5. 核心概念与数据流

```
InspectionService（UI 门面，带并发锁 + CTS）
    └─ InspectionProgram（组装：任务列表 + 参数 + 模板图）
           └─ WorkflowEngine（DAG 调度）
                  ├─ IWorkflowTask.ExecuteAsync(context, token)   ← 各模块的 XxxStep
                  └─ WorkflowExecutionContext（共享数据）
                        ├─ Image / ModeImage / CurrentImage   (HObject/HObjectMap)
                        ├─ Parameters1  (Dictionary<string, BaseParam>)
                        └─ StepOutputs  (ConcurrentDictionary<string, InspectionStepResult>)
```

- **一个"模块"= 一个 `IWorkflowTask`**（例：`BCMainPoleStep`）。它声明 `Dependencies`（依赖哪些上游任务名）和 `ExecuteAsync` 的实现，由引擎负责排序、并行、状态回调、超时取消。
- **结果回传**：每个步骤把产出的 `HObject` / `HTuple` 装进 `HObjectMap` / `DisposableMap<HTuple>`，写进 `InspectionStepResult`，由框架统一释放。
- **两条释放路径**：帧间由 `WorkflowEngine.ResetWorkflowState` → `_previousOutputs` 延迟释放；整体由 `InspectionProgram.Dispose` → `WorkflowExecutionContext.Dispose` 释放。

## 6. 两个 HALCON 版本的构建（⭐ 重要）

同一个工程、同一份源码，通过 `HalconVersion` 属性切换依赖：

```bash
# Halcon 18 版（默认）
dotnet build E:\AOI\VisionTool\VisionTool.csproj -c Debug
#   → bin\Debug\VisionTool.dll                        引用 halcondotnet 18.11

# Halcon 12 版
dotnet build E:\AOI\VisionTool\VisionTool.csproj -c Debug -p:HalconVersion=12
#   → bin\Debug\halcon12\VisionTool.dll               引用 halcondotnet 12.0 + Halcon12Compat 1.0.0.0
```

- Halcon18 依赖：`..\wh_multi_maincutter\{VisionTool.Enums, WHWpf.Theming}`
- Halcon12 依赖：`..\wh_multi_maincutter-12-合并\{VisionTool.Enums, WHWpf.Theming, Halcon12Compat}`
- 两个配置的 `obj` / `bin` 相互独立（`Directory.Build.props` 里给 Halcon12 单独指定），互不污染。

细节、坑位与改动原因见 **[VisionTool-Halcon12适配记录.md](./VisionTool-Halcon12适配记录.md)**。

## 7. 已知约定与注意事项

1. **Halcon12 的 `HTuple` 不是 `IDisposable`**，Halcon18 的是。由此派生的两条约束：
   - `DisposableMap<T>` 的约束已放宽为 `where T : class`，释放用 `(x as IDisposable)?.Dispose()`（见适配记录）；
   - 不能写 `using (HTuple t = ...)` / `using var t = new HTuple(...)`，`HTuple` 用 `.Dispose()`（Halcon12 下由 `Halcon12Compat.HTupleDisposeCompat` 扩展方法兜成空操作）。
2. **`HObject` / `HImage` / `HRegion` / `HXLD` / `HSerializedItem` 等在两版里都是 `IDisposable`**，`using` 可以照常写。
3. 当前源码（`main` HEAD = `7c7786a`）**没有内存监控功能**——`Algorithm/MemoryMonitor.cs`、`Models/MemorySnapshot.cs` 等在同一次提交里被主动删除。若要恢复，需回到 `4902dc5`。
4. 任务名是工作流里的唯一键（重复 `AddTask` 会抛异常），模块间靠任务名互相依赖。
