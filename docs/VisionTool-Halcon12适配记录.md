# VisionTool — Halcon 12 适配记录与接手指引

> 仓库：`E:\AOI\VisionTool`　分支：`main`　基线提交：`7c7786a`
> 相关文档：[VisionTool项目说明.md](./VisionTool项目说明.md)
> ⚠️ 本记录对应的改动**尚未提交**。`E:\AOI\VisionTool` 工作区状态：
> 3 个 modified（`Directory.Build.props`、`Models/DisposableMap.cs`、`VisionTool.csproj`）＋ 本 `docs/` 目录为新增未跟踪。

---

## 1. 背景与目标

`VisionTool.dll` 原本是 **HALCON 18** 版本，只在 Halcon18 侧（`wh_multi_maincutter`）使用。
Halcon12 侧的合并工程 `wh_multi_maincutter-12-合并` 需要一个**能在 Halcon12 上编译、引用 `halcondotnet 12.0.0.0`** 的 `VisionTool.dll`，替换掉 `Mydll\VisionTool.dll`。

**目标**：同一份源码、同一个工程，能同时产出 Halcon18 与 Halcon12 两个版本。

## 2. 现状摘要（已完成）

| 项 | 状态 |
|---|---|
| Halcon18 版构建 | ✅ 默认（不带参数），0 错误 |
| Halcon12 版构建 | ✅ `-p:HalconVersion=12`，0 错误 |
| `wh_multi_maincutter-12-合并\Mydll\VisionTool.dll` | ✅ 已替换为 Halcon12 版（63,488 字节） |
| 同目录 `VisionTool.pdb` | ✅ 已同步替换 |
| `Halcon12Compat`（两个 Halcon12 仓库） | ✅ 已完全回退，无残留改动 |
| 改动是否提交 | ❌ **未提交** |

## 3. 改动清单

### 3.1 `Models/DisposableMap.cs`（5 处）

原因：`DisposableMap<T>` 原约束是 `where T : class, IDisposable`，而 **Halcon12 的 `HTuple` 不实现 `IDisposable`**，导致 `InspectionStepResult` 里的 `DisposableMap<HTuple>` 报：

```
error CS0311: 类型"HalconDotNet.HTuple"不能用作泛型类型"DisposableMap<T>"的类型参数"T"。
没有从"HalconDotNet.HTuple"到"System.IDisposable"的隐式引用转换。
```

改法（**两版通用**，Halcon18 行为不变）：

```diff
-	public class DisposableMap<T> : IDisposable, ICollection<KeyValuePair<string, T>> where T : class, IDisposable
+	public class DisposableMap<T> : IDisposable, ICollection<KeyValuePair<string, T>> where T : class

  // 第 69 / 106 行
-	existing?.Dispose();
+	(existing as IDisposable)?.Dispose();

  // 第 126 / 157 行
-	kvp.Value?.Dispose();
+	(kvp.Value as IDisposable)?.Dispose();
```

> Halcon18 下 `HObject`/`HTuple` 都是 `IDisposable`，`as` 照样成立 → 仍然真释放；
> Halcon12 下 `HTuple` 不是 → 空操作（本来就没有非托管句柄要放），`HObject` 仍真释放。

### 3.2 `VisionTool.csproj`

- 新增 `HalconVersion` 开关（默认 `18`），用条件 ItemGroup 切换依赖；
- 新增 `<UseWindowsForms>true</UseWindowsForms>`（**必需**，见 §5 坑 2）；
- Halcon12 的 `halcondotnet` 用**带路径的 Include 写法**（**必需**，见 §5 坑 1）。

关键片段：

```xml
<PropertyGroup>
  <OutputType>Library</OutputType>
  <UseWPF>true</UseWPF>
  <UseWindowsForms>true</UseWindowsForms>
  <HalconVersion Condition="'$(HalconVersion)' == ''">18</HalconVersion>
</PropertyGroup>

<!-- Halcon 18（默认） -->
<ItemGroup Condition="'$(HalconVersion)' != '12'">
  <ProjectReference Include="..\wh_multi_maincutter\VisionTool.Enums\VisionTool.Enums.csproj" />
  <ProjectReference Include="..\wh_multi_maincutter\WHWpf.Theming\WHWpf.Theming.csproj" />
</ItemGroup>
<ItemGroup Condition="'$(HalconVersion)' != '12'">
  <Reference Include="halcondotnet"><HintPath>Mydll\halcondotnet.dll</HintPath></Reference>
  <Reference Include="MstPicboxTool"><HintPath>Mydll\MstPicboxTool.dll</HintPath></Reference>
  <Reference Include="MstImagelib"><HintPath>Mydll\MstImagelib.dll</HintPath></Reference>
</ItemGroup>

<!-- Halcon 12 -->
<ItemGroup Condition="'$(HalconVersion)' == '12'">
  <ProjectReference Include="..\wh_multi_maincutter-12-合并\VisionTool.Enums\VisionTool.Enums.csproj" />
  <ProjectReference Include="..\wh_multi_maincutter-12-合并\WHWpf.Theming\WHWpf.Theming.csproj" />
  <ProjectReference Include="..\wh_multi_maincutter-12-合并\Halcon12Compat\Halcon12Compat.csproj" />
</ItemGroup>
<ItemGroup Condition="'$(HalconVersion)' == '12'">
  <!-- 必须带路径！不能写 HintPath -->
  <Reference Include="..\wh_multi_maincutter-12-合并\Mydll\halcondotnet.dll" />
  <Reference Include="MstPicboxTool"><HintPath>..\wh_multi_maincutter-12-合并\Mydll\MstPicboxTool.dll</HintPath></Reference>
  <Reference Include="MstImagelib"><HintPath>..\wh_multi_maincutter-12-合并\Mydll\MstImagelib.dll</HintPath></Reference>
</ItemGroup>
```

> `Halcon12Compat` 是从 Halcon12 的 `VisionTool.Enums` **传递**进来的（`VisionTool.Enums` → `Halcon12Compat`），它提供了 `HTuple.Dispose()` 扩展（`HTupleDisposeCompat`），所以 `HTuple` 的 `.Dispose()` 调用能编过。

### 3.3 `Directory.Build.props`

给 Halcon12 配置**独立的中间目录与输出目录**，避免与 Halcon18 互相污染（见 §5 坑 3）：

```xml
<!-- 放在已有的 OutputPath PropertyGroup【之后】 -->
<PropertyGroup Condition="'$(HalconVersion)' == '12'">
  <BaseIntermediateOutputPath>obj\halcon12\</BaseIntermediateOutputPath>
  <OutputPath>bin\$(Platform)\$(Configuration)\halcon12\</OutputPath>
</PropertyGroup>
```

### 3.4 部署产物

```
E:\AOI\wh_multi_maincutter-12-合并\Mydll\VisionTool.dll   63,488 字节（替换前 70,656）
E:\AOI\wh_multi_maincutter-12-合并\Mydll\VisionTool.pdb   181,760 字节（同步替换，避免 PDB 与 DLL 不匹配）
```

新 DLL 的引用集（已核对）：

| 引用 | 版本 |
|---|---|
| halcondotnet | **12.0.0.0** |
| Halcon12Compat | **1.0.0.0** |
| MstPicboxTool | 1.5.6.2 |
| VisionTool.Enums | 0.0.0.0 |

## 4. 构建与部署步骤（照抄即可）

```powershell
# 1) 生成 Halcon12 版
dotnet build "E:\AOI\VisionTool\VisionTool.csproj" -c Debug -p:HalconVersion=12
#    Release 版把 -c Debug 换成 -c Release

# 2) 部署到合并工程
$src = "E:\AOI\VisionTool\bin\Debug\halcon12"
$dst = "E:\AOI\wh_multi_maincutter-12-合并\Mydll"
Copy-Item "$src\VisionTool.dll" "$dst\VisionTool.dll" -Force
Copy-Item "$src\VisionTool.pdb" "$dst\VisionTool.pdb" -Force

# 3) 生成 Halcon18 版（不受影响）
dotnet build "E:\AOI\VisionTool\VisionTool.csproj" -c Debug
```

**不需要手动拷 `Halcon12Compat.dll`** —— 合并工程是通过 `ProjectReference` 引用它的，重新生成时会自动重建并拷到各模块输出目录。

## 5. 关键坑（都踩过，带证据）

### 坑 1（最大）⭐ 本地 `Mydll\halcondotnet.dll` 会抢占 Halcon12 引用

`VisionTool\Mydll\` 里放的是 **Halcon18** 的 `halcondotnet.dll`。如果 Halcon12 配置用 `<HintPath>` 指向 Halcon12 的 dll：

```xml
<!-- ❌ 这样写会静默编出引用 18.11 的 DLL -->
<Reference Include="halcondotnet"><HintPath>..\wh_multi_maincutter-12-合并\Mydll\halcondotnet.dll</HintPath></Reference>
```

MSBuild 的 `ResolveAssemblyReference` 仍会把工程目录下那份 18.11 当作候选，**程序集统一（AutoUnify）时取高版本 18.11**，把 Halcon12 顶掉。实测 `AutoUnify=false` **也压不住**。

诊断方法（能直接看到解析到哪个文件）：

```powershell
dotnet msbuild "E:\AOI\VisionTool\VisionTool.csproj" -t:ResolveAssemblyReferences `
  -getItem:ReferencePath -p:Configuration=Debug -p:HalconVersion=12
# 看 "Identity"/"FullPath" 是不是 ..\wh_multi_maincutter-12-合并\Mydll\halcondotnet.dll
```

**解法**：改用「带路径的 Include 写法」——它进 `{CandidateAssemblyFiles}`，在 `AssemblySearchPaths` 里**排在 `{HintPathFromItem}` 之前**，能压住本地文件。

```xml
<Reference Include="..\wh_multi_maincutter-12-合并\Mydll\halcondotnet.dll" />   <!-- ✅ -->
```

**改完后务必核对产物引用版本**：

```powershell
$a=[System.Reflection.Assembly]::LoadFrom("$src\VisionTool.dll")
$a.GetReferencedAssemblies() | Where-Object { $_.Name -match 'halcondotnet|Halcon12Compat' }
# 必须是 halcondotnet 12.0.0.0 和 Halcon12Compat 1.0.0.0
```

### 坑 2 `<UseWindowsForms>true</UseWindowsForms>` 是硬需求

不加会报（Halcon18 配置下）：

```
error CS0012: 类型"UserControl"在未引用的程序集中定义。
必须添加对程序集"System.Windows.Forms, Version=4.0.0.0, ..."的引用。
  BaseAlgorithmTaskPresenter.cs:91 / :344
```

原因：`IAlgorithmTaskView.ShowImage(HObject, PicboxControl)` 用到 `MstPicbox` 的 WinForms 控件。
Hallcon12 配置下靠 `WHWpf.Controls` 传递引用恰好不需要，但**加上无害**，所以统一加在公共 `PropertyGroup` 里。

### 坑 3 两个配置共享 `obj` / `bin` 会互相污染

不加 §3.3 的目录隔离时，会出现「先编 12 再编 18，18 反而编译失败」这类时好时坏的现象（共享 `obj` 的还原状态与中间产物打架）。
**解法**：`Directory.Build.props` 里给 Halcon12 单独指定 `BaseIntermediateOutputPath` 与 `OutputPath`。

### 坑 4 新 DLL 比旧的小 7KB —— 是正常的

| | 字节数 | 说明 |
|---|---|---|
| 替换前（9/14 9:27 那份） | 70,656 | 由**更早的源码**编出 |
| 替换后 | 63,488 | 由**当前 HEAD `7c7786a`** 编出 |

差异原因：HEAD 提交 `7c7786a` **主动删除了内存监控功能**：

```
Algorithm/MemoryMonitor.cs   | 147 ---------  (删除)
Models/MemorySnapshot.cs     | 165 ---------  (删除)
Algorithm/WorkflowEngine.cs / InspectionProgram.cs / Services/InspectionService.cs | 精简
```

因此新 DLL 少了 `MemoryMonitor`、`MemorySnapshot`、`StepMemoryStats`、`PipelineStageStats`、`StepMemoryRecord`、`PipelineMemoryRecord`。

**替换前已逐项确认安全**：
- 合并工程里所有 “MemoryMonitor” 命中都是**独立模块 `ProcessMemoryMonitor`**，与 VisionTool 无关；
- `PipelineStageStats` / `StepMemoryStats` / `MemorySnapshot` → **无任何使用**；
- `IsSerialExecution`（6 处）是 `AlgorithmSettingsWindow` **自己类里的同名属性**，不是 VisionTool 被删的那个；
- `SetSerialExecution` 新版**仍在**（`AlgorithmManagement\UI\MainWindow.xaml.cs:148` 在用）；
- `StartInspectionAsync` 合并工程未使用；`StartWorkflowAsync` 只是多了可选参数，重新编译源码兼容。

> 若要恢复内存监控功能，需回到提交 `4902dc5` 取 `Algorithm/MemoryMonitor.cs` 与 `Models/MemorySnapshot.cs`。

## 6. 已回退的内容（不要再做一遍）

曾尝试给 `Halcon12Compat` 加 `HTupleScope` / `Ht`（`Ht.Tuple(...)` / `Ht.Scope(...)`）让 `using` 能在 Halcon12 上写。

**结论：已全部回退**，两个仓库的 `Halcon12Compat`（`wh_multi_maincutter-12-合并` 与 `wh_multi_maincutter-12`）都还原为原本 3 个文件（`Halcon12Compat.csproj` / `HOperatorSetCompat.cs` / `HTupleExtensions.cs`），编译产物只剩 `HOperatorSetCompat, HTupleDisposeCompat` 两个类型。

**回退原因**：它需要**每个调用点改写**（`using (HTuple t = ...)` → `using (Ht.Tuple(...)) `、成员访问加 `.Value`），而用户的目标是「合并别人在 Halcon18 上写的代码时不用改」——改写就失去意义了。

**重要结论**：`using (HTuple ...)` 在 Halcon12 上**无法做到源码零改动**。`using` 要求资源类型可隐式转换为 `IDisposable`，而不能给厂商的 `HTuple` 加接口实现，C# 也不允许自定义转换运算符到接口类型、没有「扩展运算符」。**唯一能零改动的办法是改 `halcondotnet.dll` 本身**（用 Mono.Cecil 给 `HTuple` 补 `IDisposable`），但风险高（强名签名失效、换机/Halcon 升级即失效、动厂商二进制），未采用。

## 7. 遗留问题

### 7.1 合并 Halcon18 代码时的 `CS1674`

**要改的面很小**——实测扫描 `wh_multi_maincutter-18`：

| 形式 | 数量 | Halcon12 下 |
|---|---|---|
| `using (HObject / HRegion / HImage / HXLD ...)` | — | ✅ 原样可用（这些在 Halcon12 里本来就是 `IDisposable`） |
| `using (HDevDisposeHelper dh = ...)` | — | ✅ 原样可用（Halcon12 的 halcondotnet 里就有，且 `IDisposable=True`） |
| `using var x = HObjectMap / DisposableMap / OpenFileDialog ...` | 35 处 | ✅ 原样可用 |
| `using (HTuple ...)` 块式 | **0 处** | — |
| **`using var x = new HTuple(...)` / `x.TupleXxx()`** | **88 处** | ❌ **唯一障碍** |

**处理办法**（建议按编译报错驱动，精确改，别全局替换）：

1. 代码原样合进来 → 编译；
2. 编译器会把每一处 `CS1674` 精确指出来；
3. 只把这些行去掉 `using ` 前缀：`using var x = new HTuple(...);` → `var x = new HTuple(...);`

去掉 `using` **不改变行为**：Halcon12 的 `HTuple` 本就没有需要释放的非托管句柄（`HTupleDisposeCompat` 就是空实现）。

> ⚠️ **不要无脑全局替换 `using var`** —— 那 35 处里有 `using var inputRegs = HObjectMap.CreateInputView();` 这类**真需要释放** `HObject` 的，去掉会泄漏。

### 7.2 `-12` 仓库的 DLL 未同步

`E:\AOI\wh_multi_maincutter-12\Mydll\VisionTool.dll` 仍是替换前的 70,656 版本（9/11）。
替换前两处文件**哈希一致**（同一份文件），所以如需保持一致，把新 DLL 也拷一份过去即可。

## 8. 验证记录

| 验证项 | 命令 / 方法 | 结果 |
|---|---|---|
| Halcon18 配置构建 | `dotnet build VisionTool.csproj -c Debug` | ✅ 0 警告 0 错误；产物引用 halcondotnet **18.11.0.1** |
| Halcon12 配置构建 | `dotnet build VisionTool.csproj -c Debug -p:HalconVersion=12` | ✅ 0 警告 0 错误；产物引用 halcondotnet **12.0.0.0** + Halcon12Compat **1.0.0.0** |
| 交替构建互不污染 | 先编 12 再编 18，各自核对引用 | ✅ 各自引用版本正确 |
| obj 隔离 | 检查 `VisionTool\obj` 目录 | ✅ 存在独立的 `halcon12` 子目录 |
| 部署产物引用 | 反射读 `Mydll\VisionTool.dll` | ✅ 与上面的 Halcon12 引用集完全一致 |
| 与在用旧 DLL 的引用集对比 | 反射比对 4 个引用 | ✅ 完全一致（仅体积因源码版本不同） |
| `Halcon12Compat` 回退 | 反射读两个仓库的编译产物 | ✅ 只剩 `HOperatorSetCompat, HTupleDisposeCompat` |
| 程序集版本 | 反射读 `Halcon12Compat.dll` | ✅ 仍为 `1.0.0.0`（不会破坏 `VisionTool.dll` 的绑定） |

## 9. 接手要点（下一步该做什么）

1. **先看工作区**：`git -C E:\AOI\VisionTool status --short` → 应有 3 个 `M`：`Directory.Build.props`、`Models/DisposableMap.cs`、`VisionTool.csproj`。这就是全部的 Halcon12 适配改动。
2. **要提交**就提交这 3 个文件（提交信息建议：`VisionTool 支持 Halcon12：双配置构建 + DisposableMap 约束放宽`）。
3. **改了 VisionTool 源码后**，要重新执行 §4 的两步（生成 Halcon12 版 + 拷到 `Mydll`），否则合并工程用的还是旧 DLL。
4. **改了 `Halcon12Compat` 源码后**，不用手动拷 —— 合并工程重新生成会自动重建。
5. **合并 Halcon18 代码**时按 §7.1 处理 `using var ... = new HTuple(...)`。
6. **不要**重做 §6 里已回退的 `HTupleScope` 方案，除非决定走「改厂商 `halcondotnet.dll`」那条路。
