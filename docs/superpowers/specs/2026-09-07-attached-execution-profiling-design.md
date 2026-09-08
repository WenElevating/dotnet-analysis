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

### 5.1 性能、内存、CPU 与耗时硬门槛

下表是交付门槛，不是预先宣称已经达到的性能结论。全部测量必须使用受控目标、固定运行时、记录机器规格和原始 JSON 结果；任一门槛不满足即不能把执行采样能力标记为完成。

基准负载为：Windows x64 .NET 10 受控目标启动 8 个持续 CPU 工作线程，循环经过至少 200 条不同托管调用栈；采样频率使用运行时 Sample Profiler 默认频率。每次测量先预热 30 秒，再记录数据。

| 维度 | 硬门槛 | 测量方式 |
| --- | --- | --- |
| 目标进程 CPU 开销 | 与不附着执行采样的同负载基线相比，120 秒完成工作量下降不得超过 25%。这是完整调用树采样模式的目标侧预算；诊断进程 CPU 门槛仍独立适用。 | 同一机器、同一目标、三轮基线和三轮采样运行，比较中位数完成量。 |
| 诊断进程 CPU | 60 分钟稳定采样期间，诊断进程用于执行采样的平均 CPU 时间不得超过 0.05 个逻辑核。 | `TotalProcessorTime` 增量除以墙钟时间；采样、查询和快照分别记录。 |
| 诊断进程内存 | 60 分钟会话结束前，诊断进程相对执行采样启动后基线的峰值私有内存增量不得超过 128 MiB。 | 记录进程私有内存峰值；会话数据必须写入私有压缩存储，不能以无界托管集合保存样本。 |
| 会话存储增长 | 60 分钟基准会话的私有采样存储不得超过 128 MiB。 | 记录所有段、帧表、栈表和索引文件总大小。 |
| 全会话查询耗时 | 对完整 60 分钟范围构建热点和调用树，连续 10 次查询的 P95 不得超过 2 秒。 | 只测已写入数据的读取、聚合与符号缓存命中路径；单独记录冷符号解析。 |
| 查询临时分配 | 完整 60 分钟查询的每次托管分配不得超过 64 MiB，且连续查询后私有内存不得阶梯式增长。 | 使用分配计数和进程私有内存双重记录。 |
| 并发查询 | 8 个并发随机时间范围查询、其中 2 个取消时，其余查询必须返回正确结果；采样不得中断。 | 校验样本总数、调用树计数、取消语义和写入连续性。 |
| 快照并发 | 连续执行 3 次 `.gcdump` 捕获期间，执行采样器不得停止；捕获前后均必须有持续样本。 | 验证采样时间连续、会话未失败，并记录 `LostEventCount`。 |

`LostEventCount` 必须始终写入基准和压力测试结果。受控负载下出现非零丢失事件即为失败；在不受控真实目标上它是诊断事实，不得被清零、隐藏或解释成完整采样。

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

### 9.1 单元、集成、性能和压力测试

测试分层必须独立执行，不能以“能编译”或少量集成测试替代性能与稳定性证据。

| 层级 | 必测内容 | 默认执行 |
| --- | --- | --- |
| 单元测试 | 时间范围半开边界、模型不可变性、参数验证、帧/栈去重、跨段完整读取、热点排序、调用树包含/独占计数、空样本语义、单飞符号解析、查询取消、结束排空、清理与重复释放。 | 是 |
| 契约与故障测试 | `ExecutionProfilingUnavailable`、`ExecutionProfileRangeUnavailable`、`ExecutionProfileStorageFailed`、目标退出、EventPipe 事件丢失和 I/O 失败；断言既有时间线与快照不因执行采样失败失效。 | 是 |
| Windows 集成测试 | 对真实 .NET 8、.NET 9、.NET 10 x64 受控目标执行附着、调用树查询、热点方法命中、Debug PDB 文件行定位、无 PDB 源码降级，以及与三次 `.gcdump` 捕获并发。 | 是 |
| 性能基准 | 执行第 5.1 节全部 60 分钟门槛：目标吞吐、诊断 CPU、私有内存、存储体积、完整会话查询 P95、分配和并发查询。 | 显式启用 |
| 压力/浸泡测试 | 2 小时连续附着，8 个 CPU 工作线程、至少 200 条调用栈、每分钟一次随机范围查询、每 10 分钟一次并发查询与取消；验证零受控丢失、无会话存储泄漏、无句柄增长、无调用树计数损坏。 | 显式启用 |
| 真实应用验收 | 诊断一个独立运行的真实 .NET 应用，而非 `DotnetAnalysis.Diagnostics.TestTarget`、单元测试宿主或合成事件源；完整执行附着、全会话查询、代码定位和快照并发流程。 | 发布前显式执行，必需 |

性能与压力测试命令必须独立于常规集成测试：

```powershell
$env:DOTNET_ANALYSIS_RUN_EXECUTION_PROFILE_BENCHMARK = 'true'
dotnet test .\tests\DotnetAnalysis.Diagnostics.IntegrationTests\DotnetAnalysis.Diagnostics.IntegrationTests.csproj --configuration Debug --no-build --filter "FullyQualifiedName~ExecutionSamplingBenchmark"

$env:DOTNET_ANALYSIS_RUN_EXECUTION_PROFILE_STRESS = 'true'
dotnet test .\tests\DotnetAnalysis.Diagnostics.IntegrationTests\DotnetAnalysis.Diagnostics.IntegrationTests.csproj --configuration Debug --no-build --filter "FullyQualifiedName~ExecutionSamplingStress"

$env:DOTNET_ANALYSIS_RUN_REAL_APPLICATION_ACCEPTANCE = 'true'
$mqttnetTestApp = Get-Process -Name 'MQTTnet.TestApp' -ErrorAction Stop
if (@($mqttnetTestApp).Count -ne 1) { throw '真实应用验收要求恰好一个正在运行的 MQTTnet.TestApp 进程。' }
$env:DOTNET_ANALYSIS_REAL_APPLICATION_PID = $mqttnetTestApp.Id
dotnet test .\tests\DotnetAnalysis.Diagnostics.IntegrationTests\DotnetAnalysis.Diagnostics.IntegrationTests.csproj --configuration Debug --no-build --filter "FullyQualifiedName~ExecutionSamplingRealApplicationAcceptance"
```

每次显式性能、压力或真实应用验收运行必须写入 `TestResults/ExecutionSampling-<timestamp>/`，至少包含：Git 提交、SDK/运行时、Windows 版本、逻辑处理器数、目标负载参数、采样频率、原始样本数、丢失事件数、诊断 CPU、目标吞吐、私有内存峰值、存储大小、查询耗时列表和分配列表。真实应用验收还必须保存第 9.1 节规定的目标身份、符号身份、三段查询原始结果和异常记录。结果文件是验收证据；只报告 P50/P95 摘要而不保留原始列表不算通过。

真实应用验收固定使用 `D:\AIProject\MQTTnet\Source\MQTTnet.TestApp\MQTTnet.TestApp.csproj` 编译的 `MQTTnet.TestApp` Debug `net8.0` 程序；在 64 位 Windows 上运行时必须验证目标为 x64 CoreCLR 进程。该项目不是本仓库测试目标，也不是合成事件源，且其 Debug 输出已有可匹配 PDB，能够验证第三方真实应用模块的代码定位。

验收前必须在独立的正常控制台启动目标，不能用输入重定向或 `echo b | ...` 启动：

```powershell
dotnet run --project 'D:\AIProject\MQTTnet\Source\MQTTnet.TestApp\MQTTnet.TestApp.csproj' --configuration Debug
```

在菜单出现后人工按一次 `b`。该入口执行 `PerformanceTest.RunQoS1Test`：进程内启动 MQTT server 和 client，并持续进行 QoS 1 消息发布。保持此工作负载不少于 10 分钟；验收者使用独立诊断宿主附着这个已经运行的 PID，在该时间内：

1. 保留从附着成功到结束前的全部执行采样，并针对早期、中期和末期的三个时间范围查询调用树。
2. 在 `MQTTnet.TestApp` 或 `MQTTnet` 目标模块中验证至少一个方法解析到匹配源码文件和行号，并记录其可复现的二进制和 PDB SHA-256。
3. 在采样期间至少执行一次 `.gcdump` 捕获，验证采样前后仍有连续样本且会话没有失败。
4. 记录目标 PID、进程启动时间、应用路径及 SHA-256、PDB 路径及 SHA-256、附着/结束时间、三个查询的原始结果、丢失事件数、诊断 CPU/内存和任何异常。

真实应用验收不得由受控测试目标、模拟对象或只检查返回值的自动化测试替代。没有这份实际诊断证据，即使单元、集成、性能和压力测试均通过，也不得宣称功能完成。

因为这是新增的 Diagnostics 公开接口，单元、集成、性能和压力测试必须覆盖正常、边界、并发、取消和失败负载。既有百万对象快照基准仍按现有门禁显式启用；执行采样的长会话证据不能由小型单元测试或一次短集成测试替代。

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
