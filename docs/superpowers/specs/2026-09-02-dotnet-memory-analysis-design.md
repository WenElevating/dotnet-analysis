# .NET 内存分析工具：首版多快照诊断设计

**日期：** 2026-09-02
**状态：** 已完成设计讨论，待书面评审
**范围：** Windows x64 本机 .NET 8、.NET 9、.NET 10 CoreCLR 进程的托管内存分析

## 1. 目标、范围与术语

首版用于分析本机 Windows x64 上运行的 .NET 8、.NET 9 和 .NET 10 CoreCLR 进程。用户附着一个进程后，可以持续观察内存时间线，并在任意时刻多次截取内存快照。

每一份快照回答两个不同的问题：

1. **这一刻还占着内存的是什么？** 由 `.gcdump` 的堆图提供类型、对象和引用链。
2. **从上一份成功快照到这一份快照，什么分配最热？** 由附着期间持续收集的分配样本提供类型级热点调用栈。

本设计中的“快照文件”是成功保存的 `.gcdump`；不使用“制品”一词。

首版支持：

- 选择并附着运行中的进程。
- 持续显示“托管堆”和“进程内存”两条时间线。
- 多次截取 `.gcdump` 快照。
- 查看类型占用、对象列表、引用链和区间分配热点调用栈。
- 导入已有 `.gcdump`。
- 为 `.dmp` 预留读取扩展点，但不实现 `.dmp` 解析。

首版不支持：远程进程、Linux、容器、.NET Framework、对象实例的精确创建栈、常驻历史录制、GC Root 以外的复杂堆图算法、符号自动下载或转储文件解析。

最低操作系统前提为 Windows 10 22H2（含 2023-09 累积更新）或 Windows 11 22H2（含 2023-09 累积更新）及更高版本。该前提用于稳定取得与 Windows 任务管理器“内存”列一致的进程内存口径。

## 2. 关键产品语义

### 2.1 附着、多快照与分配区间

```text
附着进程
  ├─ 持续采样
  │    ├─ 托管堆
  │    ├─ 进程内存
  │    └─ 分配调用栈样本
  │
  └─ 用户多次截取快照
       ├─ 立即开始采集当前 .gcdump
       ├─ 保存当前堆中的类型、对象、引用链
       └─ 封存上一成功快照到当前快照的分配热点
```

点击“截取快照”立即开始采集，不再有“结束录制后才采集一次快照”的流程。一次 `.gcdump` 采集可能造成目标进程短暂停顿，这是首版已接受的诊断成本。

每次成功快照都对应一个分配区间：

| 快照 | 分配区间 |
| --- | --- |
| 快照 #1 | 附着成功到快照 #1 成功保存 |
| 快照 #N | 快照 #N-1 成功保存到快照 #N 成功保存 |

失败或取消的截取不创建快照，也不切分分配区间。后续成功快照继续从上一份成功快照开始统计。

`RequestedAtUtc` 记录用户点击时间，`CaptureStartedAtUtc` 记录诊断层开始采集时间，`CapturedAtUtc` 记录快照文件成功保存时间。分配区间以成功保存的 `CapturedAtUtc` 为边界，避免把失败采集误当成时间边界。

### 2.2 调用栈与引用链的语义

对某个类型查看“调用栈”时，展示的是：**该类型在所属分配区间内观测到的类型级分配热点调用栈**。它不是某一个对象实例的精确创建调用栈。

对象详情中的引用链独立回答“这个对象为什么仍然存活”。调用栈和引用链不能相互推导，也不能宣称一一对应。

分配热点始终是采样结果。即使采样流未发现中断，也不得向上层或 UI 表述为“记录了每一次分配”。

### 2.3 时间线语义

时间线固定包含两条：

- **托管堆**：当前托管堆使用量。
- **进程内存**：内部使用与 Windows 任务管理器“内存”列一致的私有工作集口径；上层和 UI 不暴露 Windows API 或性能计数器术语。

时间线不把读数缺失画成零。每个点显式带状态，允许 UI 显示中断或会话结束。

## 3. 分层架构与依赖方向

```text
Desktop -> Application -> Core
                     ↑
Diagnostics ---------+
```

| 项目 | 职责 | 允许依赖 | 明确禁止 |
| --- | --- | --- | --- |
| `DotnetAnalysis.Core` | 值对象、不可变数据模型、状态转换规则、应用事件基础契约 | BCL | WPF、文件系统、诊断 SDK、DI 容器 |
| `DotnetAnalysis.Application` | 用例编排、附着会话和快照工作流、事件总线、面向 UI 的诊断契约 | `Core`、BCL、日志抽象 | EventPipe/TraceEvent 具体类型、WPF 控件、诊断文件格式 |
| `DotnetAnalysis.Diagnostics` | Windows 进程、EventPipe、`.gcdump` 采集与读取、内存采样 | `Application`、`Core`、诊断 SDK | ViewModel、窗口、Application 工作流状态 |
| `DotnetAnalysis.Desktop` | WPF、轻量 MVVM、命令、导航、组合根、UI 线程切换 | `Application`、`Core`、WPF | 诊断实现类型、PID/文件格式/EventPipe 细节 |
| `DotnetAnalysis.Tests` | 单元、集成和契约测试 | 被测项目、测试库 | 生产服务注册 |

`Desktop` 只依赖 `Application` 暴露的接口；组合根可以注册 `Diagnostics` 的实现，但 ViewModel 不得引用其实现类型。`Core` 不得知道诊断连接、磁盘路径或 UI。

## 4. 上层稳定诊断契约

上层只认识以下诊断对象：

```text
IProcessDiagnostics
IProcessDiagnosticsSession
TargetProcess
MemoryUsageSample
MemorySnapshot
MemorySnapshotAnalysis
AllocationProfile
```

```csharp
public interface IProcessDiagnostics
{
    Task<IReadOnlyList<TargetProcess>> GetProcessesAsync(
        CancellationToken cancellationToken);

    Task<IProcessDiagnosticsSession> AttachAsync(
        TargetProcess process,
        CancellationToken cancellationToken);

    Task<MemorySnapshot> OpenSnapshotAsync(
        string filePath,
        CancellationToken cancellationToken);
}

public interface IProcessDiagnosticsSession : IAsyncDisposable
{
    TargetProcess Process { get; }

    IAsyncEnumerable<MemoryUsageSample> GetMemoryUsageAsync(
        CancellationToken cancellationToken);

    Task<MemorySnapshot> CaptureSnapshotAsync(
        CancellationToken cancellationToken);
}
```

`IProcessDiagnostics` 是应用级入口，负责列出进程、附着和打开已有快照。`IProcessDiagnosticsSession` 是一个已经附着的、带状态的诊断上下文；它的生命周期跟随“附着”，而非任一份快照。

`TargetProcess` 至少包含 `ProcessId` 和 `StartedAtUtc`。附着前和每次采集前都复核这两个值，防止 PID 被复用后误采集其他进程。

诊断 SDK、EventPipe Session、TraceEvent、文件路径、临时目录和底层异常都不得穿过上述边界。

## 5. 数据模型

### 5.1 时间线与分配数据

```csharp
public sealed record MemoryUsageSample(
    DateTimeOffset ObservedAtUtc,
    long? ManagedHeapBytes,
    long? ProcessMemoryBytes,
    MemoryUsageSampleState State);
```

`MemoryUsageSampleState` 使用 `Measured`、`Unavailable`、`SessionEnded` 三种清晰状态。`Unavailable` 表示本次无法取得读数；字段为 `null`，不得用 `0` 替代。

```text
AllocationProfile
├─ IntervalStartUtc
├─ IntervalEndUtc
├─ DataQuality: Continuous | Interrupted | NotAvailable
└─ Hotspots
   ├─ TypeIdentity
   ├─ ObservedAllocatedBytes
   ├─ SampleCount
   └─ CallStack
```

- `Continuous`：本区间未检测到分配采样流中断。
- `Interrupted`：仍有热点结果，但区间内出现丢失、断开或无法判定的采样缺口。
- `NotAvailable`：没有对应的附着期间分配样本，例如直接导入 `.gcdump`。

`ObservedAllocatedBytes` 是采样观测到并聚合的字节数，不命名为“总分配字节数”或“精确分配字节数”。

### 5.2 快照与分析结果

`MemorySnapshot` 是已经安全保存、可以重新打开的快照。实时采集快照同时关联内部保存的区间分配数据；导入快照只包含原始 `.gcdump`。

`MemorySnapshotAnalysis` 是对 `MemorySnapshot` 的可重试分析结果，包含：

- 按对象总大小排序的类型列表；
- 选定类型的对象列表；
- 对象的引用链；
- 当前快照对应区间的 `AllocationProfile`。

分析失败不会损坏或删除成功保存的快照文件。重新分析只读取已有快照和已有区间分配数据，不重新连接目标进程。

分析由 Application 内部的 `MemorySnapshotAnalysisService` 执行：它输入 `MemorySnapshot`，输出 `MemorySnapshotAnalysis`。该服务是 `MemorySnapshotOperation` 的实现细节，不提供给 Desktop，也不把文件读取器或堆图对象泄漏给调用方。

## 6. 生命周期与并发规则

### 6.1 附着会话

```text
Attaching -> Monitoring -> Ending -> Ended
                    \--------------> Failed
```

`ProcessDiagnosticsSession` 进入 `Monitoring` 后，持续提供时间线并持续接收分配样本。目标进程自行退出时，会话以 `Ended` 收尾；诊断基础设施故障导致无法继续运行时进入 `Failed`。

### 6.2 单次快照

```text
Pending -> Capturing -> Analyzing -> Ready
                    ├-> Failed
                    └-> Canceled
```

- 单个附着会话同一时刻只允许一个 `Capturing` 快照。Application 在该状态禁用下一次截取，不排队第二次请求。
- 原始 `.gcdump` 成功保存后，快照进入 `Analyzing`；分析不再需要目标进程仍然存在。
- 用户结束附着时，停止时间线和分配采样，取消正在采集的快照；已保存快照保留，已开始的本地分析允许完成。
- 用户关闭应用时，取消正在采集和正在分析的工作；已完整保存的快照保留，下次可重新打开。
- 任何不完整文件都必须删除；已完整保存的快照不因结束附着而删除。

### 6.3 Application 中的状态所有者

`AttachedProcessSession` 管理一个附着会话的生命周期、时间线订阅和 `IProcessDiagnosticsSession` 的释放。

`MemorySnapshotOperation` 管理一次截取、保存后的分析、分析重试以及快照状态。两者都发布事实事件，但事件总线不是状态来源；状态只能由相应对象持有并通过查询读取。

## 7. Diagnostics 实现组成

`WindowsProcessDiagnostics` 是 `IProcessDiagnostics` 的 Windows 实现。内部名称采用“对象名 + 责任”的形式，避免 `Helper`、`Manager`、`Complete` 等无法表达职责的命名。

```text
WindowsProcessDiagnostics
├─ ProcessEnumerator
├─ ProcessIdentityValidator
├─ RuntimeCapabilitiesResolver
├─ ProcessDiagnosticsSession
│  ├─ ProcessMemorySampler
│  ├─ AllocationSampleCollector
│  ├─ GCDumpSnapshotCollector
│  └─ AllocationProfileBuilder
├─ MemorySnapshotStore
└─ MemorySnapshotReaderRegistry
   ├─ GCDumpSnapshotReader
   └─ DumpSnapshotReader（未来实现）
```

| 组件 | 单一职责 |
| --- | --- |
| `ProcessEnumerator` | 枚举候选进程，并创建 `TargetProcess`。 |
| `ProcessIdentityValidator` | 验证 PID 与进程启动时间仍对应同一目标。 |
| `RuntimeCapabilitiesResolver` | 确认 Windows x64、本机 CoreCLR、运行时版本与可用诊断能力。未来低版本支持在这里扩展能力表和适配实现。 |
| `ProcessMemorySampler` | 持续读取托管堆与进程内存时间线。 |
| `AllocationSampleCollector` | 在附着期间持续读取 EventPipe 分配事件与调用栈样本。 |
| `GCDumpSnapshotCollector` | 为一次截取创建临时 `.gcdump`、验证其可读并交给存储层完成保存。 |
| `AllocationProfileBuilder` | 以成功快照时间边界封存一个分配区间，生成 `AllocationProfile`。 |
| `MemorySnapshotStore` | 管理临时文件、原子保存、已保存快照和区间分配数据；不向上层泄漏路径策略。 |
| `MemorySnapshotReaderRegistry` | 根据输入格式选择读取器。首版只注册 `GCDumpSnapshotReader`；后续可注册 `DumpSnapshotReader` 而不改变上层接口。 |
| `GCDumpSnapshotReader` | 从 `.gcdump` 构建类型、对象和引用链分析数据。 |

`AllocationSampleCollector` 与 `GCDumpSnapshotCollector` 必须是独立职责：前者是整个附着期的持续 EventPipe 采样，后者只在用户点击时执行一次快照采集。采集器、读取器和采样器均不得决定 Application 状态转换。

## 8. 文件保存、导入与重试

实时采集的快照写入应用管理的本地存储。写入过程为：临时文件 -> 可读性验证 -> 原子改为正式快照文件 -> 保存对应分配区间数据。取消、失败或应用关闭只删除临时文件。

导入 `.gcdump` 不改写、移动或删除用户选择的源文件。它创建一个可分析的 `MemorySnapshot`；因为不存在对应附着期间的分配样本，其 `AllocationProfile.DataQuality` 为 `NotAvailable`。

首版不把 `.dmp` 伪装成可用格式：当读取器注册表没有支持该格式的读取器时，明确返回 `SnapshotFormatNotSupported`。

## 9. 错误边界

上层只处理 `DiagnosticsException` 及以下错误码：

| 错误码 | 触发场景 |
| --- | --- |
| `AccessDenied` | 没有权限打开或诊断目标进程。 |
| `TargetExited` | 目标进程在附着、采样或采集期间退出。 |
| `TargetChanged` | PID 仍存在但启动时间不一致，说明目标身份已变化。 |
| `RuntimeNotSupported` | 不是受支持的 Windows x64 CoreCLR 或运行时能力不满足首版要求。 |
| `SnapshotFormatNotSupported` | 输入不是支持的快照格式，或未来格式阅读器尚未实现。 |
| `CaptureFailed` | EventPipe、快照写入或校验失败。 |
| `CaptureCancelled` | 用户、会话结束或应用关闭取消了采集。 |

文件 I/O、P/Invoke、EventPipe 和解析器的原始异常保留在内部日志与 `InnerException`，但 UI 和 Application 不依赖异常文本进行流程判断。

## 10. 事件总线

沿用已有的 `IEventBus` 和 `InProcessEventBus`，不新增第二套事件总线。它已经是实例级、强类型、异步、有界且可释放订阅的进程内事件机制。

新的诊断工作流替换旧的一次性采集事件，但不改变总线的边界：

- 生命周期事件：`ProcessDiagnosticsSessionStateChanged`、`MemorySnapshotCaptureStarted`、`MemorySnapshotCaptured`、`MemorySnapshotCaptureFailed`、`MemorySnapshotAnalysisStarted`、`MemorySnapshotAnalysisCompleted`、`MemorySnapshotAnalysisFailed`、`ProcessDiagnosticsSessionEnded`。
- 高频事件：`ProcessMemoryUsageUpdated`、分配采样的轻量状态更新。

事件对象只携带标识、时间、状态、轻量摘要和失败码；类型列表、对象列表、引用链和调用树仍通过所属会话或快照操作查询，不经事件总线传递。

当前总线仅对 `CaptureProgressChanged` 做硬编码合并。重构时将它泛化为事件自身声明的投递策略：

```text
ApplicationEventDeliveryMode
├─ Ordered      // 生命周期事件：按顺序投递，不允许合并
└─ LatestOnly   // 时间线事件：同一会话、同一事件类型只保留最新值
```

`LatestOnly` 事件携带明确的 `DeliveryKey`，例如“会话 ID + 进程内存时间线”。每个订阅仍有独立有界队列和单线程消费循环：慢 UI 可以丢弃过期时间线点，但不得阻塞诊断采样；生命周期事件队列无法接收时快速报告 `EventDeliveryException`，不能静默丢弃。

订阅处理异常仍隔离为 `ModuleFaulted`，且处理 `ModuleFaulted` 自身失败时不得递归发布。应用关闭时，总线停止接收、取消订阅处理器并在有限预算内收尾；非合作订阅者只记录告警，不能无限阻塞退出。

## 11. 技术栈

- .NET 10 与固定的 `global.json` SDK。
- WPF 桌面端。
- 自研轻量 MVVM：`ObservableObject`、`RelayCommand`、`AsyncRelayCommand`；不引入第三方 MVVM 框架。
- `Microsoft.Extensions.DependencyInjection` 与 `Microsoft.Extensions.Logging` 基础抽象。
- `Microsoft.Diagnostics.NETCore.Client`：诊断连接、EventPipe 和运行中进程的采集能力。
- TraceEvent：首版内部用于读取诊断数据和 `.gcdump` 分析。
- ClrMD：仅作为未来 `.dmp` 阅读器候选，首版不引用。

不启动 `dotnet-gcdump` 或 `dotnet-trace` 子进程；它们仅用于人工交叉验证和准备测试数据，不能成为应用运行时依赖。

## 12. 性能、资源与取消

- EventPipe 读取、快照写入和堆图分析均不运行在 UI 线程。
- `ProcessMemorySampler` 和 `AllocationSampleCollector` 不能被慢事件订阅者反压。
- 不同时长期持有完整堆图和完整原始事件列表；读取时优先流式处理、增量聚合和按需构建对象详情。
- UI 只保存页面所需的汇总行和按需展开的数据，不持有整个堆图。
- 结束附着和应用关闭使用协作取消；临时文件清理由 `MemorySnapshotStore` 保证幂等。
- 每次快照采集前都复核目标身份；采集成功后先确保文件完整可读，再发布“已保存”状态。

## 13. 测试与验证

### 13.1 单元与契约测试

- `TargetProcess` 身份比较与 PID 复用防护。
- 附着会话和单次快照状态转换。
- 快照失败、取消和目标退出不切分分配区间。
- 快照保存成功后分析失败可重试，且不重新采集。
- 导入 `.gcdump` 的类型、对象和引用链分析；热点数据必须为 `NotAvailable`。
- 分配热点按类型、调用栈聚合，并正确标注 `Continuous`、`Interrupted`、`NotAvailable`。
- 现有事件总线的生命周期投递与订阅释放回归；新增时间线事件的 `LatestOnly` 合并不影响 `Ordered` 事件。

### 13.2 Windows 集成测试

使用受控的 .NET 8、.NET 9、.NET 10 x64 测试进程，验证：

- 枚举、附着、时间线、截取和会话释放。
- 目标退出、访问失败和 PID 身份变化的错误映射。
- `.gcdump` 可重新打开并得到类型、对象和引用链。
- 截取期间的临时文件清理与成功快照保留。
- 进程内存读数与 Windows 任务管理器对应口径的交叉核对。

诊断库升级、Windows 版本升级或未来加入低版本 CoreCLR 支持时，必须重新运行此组集成测试。

## 14. 后续扩展点

- `DumpSnapshotReader`：以 ClrMD 支持 `.dmp`，不改变 `IProcessDiagnostics.OpenSnapshotAsync`。
- 扩展 `RuntimeCapabilitiesResolver` 与采集实现，逐步支持更低版本 CoreCLR。
- GC Root、支配树、对象字段浏览和快照差异对比。
- 本地提权采集助手。
- 快照导出和报告生成。

这些扩展不得要求 Desktop 了解诊断库，不得改变 `IProcessDiagnostics`、`IProcessDiagnosticsSession`、`MemorySnapshot` 和 `MemorySnapshotAnalysis` 的既有语义。
