# 附着会话执行采样诊断层设计

**日期：** 2026-09-07
**状态：** 已完成设计讨论，待书面评审
**范围：** Windows x64、.NET 8/.NET 9/.NET 10 CoreCLR 进程的附着式执行采样、全会话保存和调用树查询

## 1. 目标与非目标

本设计在既有的“内存时间线、分配采样和 `.gcdump` 快照”之外，增加一条独立的执行采样能力。它用于回答：在用户从内存时间线上选择的一段时间内，目标进程实际执行了哪些托管调用栈，哪些方法最热，以及在符号可用时这些方法对应的源码位置。

首版目标：

- 附着成功后，持续采集目标进程的托管执行栈样本。
- 从附着成功开始，到会话结束为止，完整保留每一条成功接收的执行采样记录；不得按时间窗口淘汰。
- 按任意已采集时间范围查询热点方法和自顶向下调用树。
- 对有匹配 PDB 与源码的帧返回文件、行和可选列；没有源码位置时仍返回模块和方法，不伪造行号，也不新增面向 UI 的“不可定位”状态。
- 用户主动结束或释放附着会话后，诊断层清理该会话的执行采样存储；首版不持久化、不导入、不重新打开执行采样结果。

首版非目标：

- 不修改 Desktop、ViewModel、XAML、导航或交互。
- 不以 `.nettrace` 作为用户可见或长期保存格式。
- 不使用原生 CLR Profiler、目标启动式插桩或逐实例精确创建栈。
- 不把执行采样结果混入 `AllocationProfile`、对象 GC Root 或快照分析结果。
- 不支持 .NET Framework、x86、ARM、远程进程或 Linux。

执行采样是统计采样。它说明“某时间段内哪些代码正在执行”，不说明“某一对象实例必然由该代码创建”，也不能替代 GC Root 引用链。

## 2. 分层与公开边界

依赖方向保持：`Core <- Application <- Diagnostics`。本轮没有 Desktop 改动。

| 项目 | 新增职责 |
| --- | --- |
| `DotnetAnalysis.Core` | 不可变执行采样查询输入与输出模型。 |
| `DotnetAnalysis.Application` | 在 `IProcessDiagnosticsSession` 增加执行分析查询契约及稳定错误码。 |
| `DotnetAnalysis.Diagnostics` | EventPipe Sample Profiler、会话级压缩存储、查询聚合和符号解析实现。 |
| `DotnetAnalysis.Desktop` | 不在本设计范围内。 |

`Application` 与将来的上层只能看到 Core 模型与 `IProcessDiagnosticsSession`。不得暴露 `DiagnosticsClient`、`EventPipeSession`、TraceEvent、PDB 读取器、会话临时目录或原始 EventPipe 事件。

## 3. 输入与输出契约

在既有 `IProcessDiagnosticsSession` 上增加：

```csharp
Task<ExecutionProfile> GetExecutionProfileAsync(
    ExecutionTimeRange timeRange,
    CancellationToken cancellationToken);
```

### 3.1 输入

`ExecutionTimeRange` 表示半开区间 `[StartAtUtc, EndAtUtc)`：

- 两端均为 UTC 时间；`StartAtUtc` 必须严格早于 `EndAtUtc`。
- 请求范围必须完全落在该会话已采集的时间范围内，不自动裁剪或静默降级。
- 会话结束并完成清理后，任何查询均不再有效。

### 3.2 输出

```text
ExecutionProfile
├─ TimeRange
├─ ReceivedSampleCount
├─ LostEventCount
├─ Hotspots
└─ CallTreeRoots

ExecutionHotspot
├─ Frame
├─ InclusiveSampleCount
└─ ExclusiveSampleCount

ExecutionCallTreeNode
├─ Frame
├─ InclusiveSampleCount
├─ ExclusiveSampleCount
└─ Children

ExecutionFrame
├─ MethodName
├─ ModuleName
└─ SourceLocation?

SourceLocation
├─ FilePath
├─ LineNumber
└─ ColumnNumber?
```

- `Hotspots` 按 `InclusiveSampleCount` 倒序，供上层直接呈现或关联内存时间段。
- `CallTreeRoots` 是自顶向下调用树的根节点集合；同一方法出现在不同调用路径时必须保留为不同节点。
- `InclusiveSampleCount` 是某帧及其后代的样本数；`ExclusiveSampleCount` 是样本落在该帧本身的数量。
- `ReceivedSampleCount` 只统计请求时间范围内成功写入的样本。
- `LostEventCount` 是查询时诊断层已观察到的会话累计 EventPipe 丢失事件数；其不伪装成范围内精确丢失数。
- `SourceLocation` 只有在该帧的模块、PDB 与源码能够匹配时返回；否则为 `null`。这不是错误，也不需要单独状态枚举。
- 采样器正常运行但区间没有托管栈样本时，返回空热点和空调用树，不抛异常。

## 4. EventPipe 采集与数据流

附着会话进入 Monitoring 时，新增的 `ExecutionSamplingSession` 与既有 `ProcessMemorySampler`、`AllocationSamplingSession` 并列启动。它使用独立的 EventPipe Session 订阅 Sample Profiler，并请求运行时 rundown 数据以解析托管方法信息。

```text
AttachAsync
  -> ProcessDiagnosticsSession
     -> ExecutionSamplingSession.StartAsync
        -> EventPipe Sample Profiler
           -> 栈规范化（根 -> 当前执行方法）
           -> ExecutionCaptureStore.Append

GetExecutionProfileAsync(range)
  -> ExecutionCaptureStore.Read(range)
  -> ExecutionProfileBuilder 聚合
  -> ExecutionSymbolResolver 按需补全 SourceLocation
  -> ExecutionProfile
```

采样接收线程只能完成轻量操作：规范化调用栈、帧和栈去重、记录时间与线程标识、向存储追加。PDB 读取、源码定位、调用树合并和大范围枚举不得在 EventPipe 消费线程执行。

## 5. 全会话存储与查询

会话必须完整保留全部成功接收的样本，但不能把整段原始 EventPipe 事件流常驻内存。`ExecutionCaptureStore` 采用诊断层私有、追加式的压缩记录：

```text
帧表：模块 / 方法 / 内部符号标识，按首次出现去重
栈表：父栈 ID + 帧 ID，按完整调用栈去重
样本段：时间戳增量 + 栈 ID + 线程 ID，持续追加
段索引：每段起止时间、样本数量和文件偏移
```

- 容量不通过丢弃历史样本控制，而通过帧/栈去重、增量时间戳和分段写入控制。
- 查询只读取与 `ExecutionTimeRange` 相交的段，并在查询内存中合并为 `ExecutionProfile`；不得先加载整个会话。
- 一次查询获得确定的已写入边界，因而不会读取到半条样本记录。
- 诊断层临时会话存储路径不穿过 Application 契约，也不作为用户文件展示。
- `EndAsync` 或 `DisposeAsync` 在排空已到达的事件、结束在途查询后删除会话存储和索引。

## 6. 符号与源码定位

`ExecutionSymbolResolver` 是 Diagnostics 内部组件。它在查询阶段为去重帧做按需解析，并对同一帧采用单飞加载，避免多个并发查询重复访问同一 PDB。

解析成功时，`ExecutionFrame.SourceLocation` 返回对应源码路径、行号和可选列号。解析失败、缺少 PDB、校验不匹配、源码文件不存在或动态程序集没有可用源码时，只返回方法名和模块名。

源码路径是诊断结果的一部分，不是 EventPipe 临时文件路径。首版不自动下载符号、不自动下载源码、不引入 Source Link 网络访问；只使用本机可匹配的符号与源码。

## 7. 并发、取消与生命周期

- 采样会话是单写入者；`ExecutionCaptureStore` 对写入顺序负责。
- 多个 `GetExecutionProfileAsync` 可以并发执行；它们是只读查询，彼此不串行化整段聚合。
- 单个调用的 `CancellationToken` 只取消该调用的等待、读取或聚合，不取消共享 EventPipe 采样和持续存储。
- 结束会话时，先停止新的 EventPipe 数据输入，再在有界等待内排空已经到达的数据；随后拒绝新查询、等待在途查询、清理会话数据。
- 会话结束后的数据不保留。调用方如需分析，必须在附着会话存活期间执行查询。
- 执行采样启动失败不应使内存时间线、快照捕获或既有分配采样会话失败。

## 8. 稳定失败语义

新增的公开 `DiagnosticsErrorCode`：

| 错误码 | 触发条件 |
| --- | --- |
| `ExecutionProfilingUnavailable` | EventPipe Sample Profiler 无法启动、目标运行时拒绝该能力，或执行采样器从未可用。 |
| `ExecutionProfileRangeUnavailable` | 查询范围不在会话已采集范围内，或会话结束并已清理。 |
| `ExecutionProfileStorageFailed` | 会话私有存储无法继续追加或读取，因而不能继续提供可靠执行分析。 |

采样流正常但没有样本、帧无法解析源码、或源码不存在，不属于以上错误。底层 EventPipe、I/O 或符号异常保留为 `DiagnosticsException.InnerException` 与结构化日志，调用方通过稳定错误码判断流程。

## 9. 兼容性探针与验证

在正式实现公开契约前，必须先完成隔离兼容性探针。探针覆盖受控的 .NET 8、.NET 9、.NET 10 x64 目标，并验证：

1. 对已运行目标可启动 EventPipe Sample Profiler。
2. 可取得真实托管调用栈和稳定方法名。
3. Debug PDB 与源码存在时，可定位受控目标预设方法的文件与行号。
4. PDB 或源码缺失时，调用树和热点仍正确，`SourceLocation` 为 `null`。
5. 执行采样与现有内存时间线、分配采样和 `.gcdump` 捕获并发运行时，不相互中断。

正式测试至少包括：

- Core 模型的时间范围、半开边界、不可变性和参数验证。
- 帧/栈去重、跨段完整读取、热点排序、调用树包含与独占样本计数。
- 全会话长时记录、高对象/高采样负载下的存储与查询分配基准。
- 并发查询、单查询取消、结束时排空、清理和重复释放。
- EventPipe 启动失败、事件丢失、存储失败和范围越界的稳定错误码。
- 真实 Windows .NET 8/9/10 目标的附着、调用树、源码定位、无源码降级，以及与快照并发的集成测试。

因为这是新增的 Diagnostics 公开接口，性能和压力测试必须覆盖正常、边界、并发、取消和失败负载。百万对象快照基准仍按既有门禁显式启用；执行采样应增加独立的长会话压力基准，不能用小型单元测试替代。

## 10. 已确认决策

| 决策 | 结论 |
| --- | --- |
| 首要能力 | ANTS Performance Profiler 风格的附着式执行采样、按时间范围调用树和代码定位。 |
| 技术路线 | 使用既有 EventPipe / DiagnosticsClient / TraceEvent 基础设施，不先引入原生 Profiler。 |
| 覆盖范围 | Windows x64 .NET 8/9/10 CoreCLR 已运行进程。 |
| 保留策略 | 从附着成功到会话结束完整保留；不使用滑动窗口。 |
| 存储策略 | 诊断层私有、压缩、追加式会话存储；不落长期 `.nettrace`。 |
| 会话结束 | 排空、完成在途查询、清理；不支持重开。 |
| 源码定位 | PDB/源码匹配时返回位置；否则仅返回模块和方法，不伪造或单列 UI 状态。 |
| 分层范围 | 仅 Core、Application 契约与 Diagnostics 实现；本轮不修改 Desktop/UI。 |

## 11. 书面评审后的下一步

本规格经用户书面确认后，才使用实施计划拆分兼容性探针、Core/Application 契约、Diagnostics 采样与存储、符号解析、测试和性能门禁。书面评审前不得开始实现。
