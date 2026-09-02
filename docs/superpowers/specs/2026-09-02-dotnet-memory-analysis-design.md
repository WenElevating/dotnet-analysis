# .NET 内存分析工具：首版设计

**日期：** 2026-09-02  
**状态：** 已完成讨论，待书面评审  
**范围：** Windows x64 本机 CoreCLR 进程的托管内存分析首版

## 1. 目标与范围

首版面向 Windows x64 本机运行的 .NET 8、.NET 9 和 .NET 10 CoreCLR 进程，提供一个桌面端托管内存分析工作流：

1. 用户选择运行中的目标进程并附着。
2. 工具从附着成功时开始记录对象分配事件与调用栈。
3. 用户显式点击“结束采集并分析”。
4. 工具停止分配追踪、保存 `.nettrace`，再采集当前托管堆的 `.gcdump`。
5. 后台分析两类采集文件，统一展示类型占用排行和分配调用栈。

首版还支持导入已有 `.gcdump`，导入后复用同一堆分析管线。

首版不支持实际解析 `.dmp`，但架构应预留读取器扩展点。首版不做 GC Root 引用路径、对象字段浏览、支配树计算、远程进程、容器、Linux、.NET Framework 或常驻监控。

## 2. 关键语义

`.gcdump` 与 `.nettrace` 提供不同证据，界面与模型必须严格区分：

| 数据 | 来源 | 含义 |
| --- | --- | --- |
| 当前占用 | `.gcdump` | 结束采集时仍在托管堆中的对象和类型总量 |
| 分配热点 | `.nettrace` | 附着到用户结束采集期间观察到的分配事件及调用栈 |

两类结果只按规范化的类型标识关联，不声明某个当前对象可对应到其历史分配调用栈。

分配追踪与大堆快照均可能产生采样/估算数据。所有相关结果必须带 `Exact` 或 `Estimated` 精度标记，避免将估算结果表述为精确结果。

## 3. 用户工作流与状态

### 3.1 运行中进程采集

```text
选择进程
  -> 运行时与权限预检
  -> 附着并开始分配追踪
  -> 用户复现问题
  -> 结束采集并分析
  -> 停止并保存 .nettrace
  -> 采集 .gcdump
  -> 后台分析
  -> 显示结果
```

采集没有固定时长。用户显式结束时，系统必须先完成分配追踪文件的停止与持久化，再开始堆快照采集。

关闭采集窗口或退出程序视为取消：立即停止附着和后台工作、删除本次临时采集文件，不采集堆快照，不生成分析结果。

### 3.2 文件导入

导入 `.gcdump` 跳过附着和采集，直接进入相同的堆读取、模型构建和结果展示流程。

## 4. 工程结构

```text
src/
  DotnetAnalysis.Desktop/       WPF 界面、ViewModel、窗口与用户交互
  DotnetAnalysis.Application/   用例编排、采集会话状态机、接口与事件
  DotnetAnalysis.Core/          纯领域模型、分析结果、值对象
  DotnetAnalysis.Diagnostics/   EventPipe、采集文件读取与微软诊断库实现
tests/
  DotnetAnalysis.Tests/         Core、Application、Diagnostics 的测试
```

依赖方向必须保持为：

```text
Desktop -> Application -> Core
Diagnostics -> Application + Core
Tests -> 被测项目
```

`Desktop` 不得直接访问 PID、EventPipe 或采集文件解析细节。`Core` 不得依赖 WPF、微软诊断库或文件系统。`Diagnostics` 是唯一允许引用微软诊断包的项目。

### 4.1 项目职责与引用约束

| 项目 | 允许依赖 | 负责内容 | 明确禁止 |
| --- | --- | --- | --- |
| `DotnetAnalysis.Core` | 仅 BCL | 值对象、状态、分析结果、排序/聚合规则、接口共享的事件定义 | WPF、诊断 SDK、文件 I/O、依赖注入容器 |
| `DotnetAnalysis.Application` | `Core`、BCL、日志抽象 | 用例、会话状态机、取消、事件总线、接口定义、错误映射 | WPF 控件、TraceEvent 具体类型、直接解析采集文件 |
| `DotnetAnalysis.Diagnostics` | `Core`、`Application`、微软诊断包 | 本机 CoreCLR 发现与预检、EventPipe 追踪、快照采集、读取 `.gcdump` / `.nettrace` | ViewModel、窗口、业务流程状态机 |
| `DotnetAnalysis.Desktop` | `Core`、`Application`、WPF | 组合根、轻量 MVVM、导航、命令绑定、UI 线程调度 | `Diagnostics` 的实现类型、采集文件格式细节 |
| `DotnetAnalysis.Tests` | 被测项目、测试库 | 规则测试、状态机测试、采集文件样本测试、WPF ViewModel 测试 | 生产环境服务注册 |

`Desktop` 不直接引用 `Diagnostics`。组合根在启动时只面向 `Application` 的接口注册诊断实现，因此 ViewModel 不会在编译期耦合 EventPipe、TraceEvent、`.gcdump` 或 `.nettrace`。

### 4.2 框架目录与关键类型

```text
src/
  DotnetAnalysis.Core/
    Processes/                 TargetProcess, RuntimeCapability
    Sessions/                  AnalysisSession, AnalysisSessionState, SessionId
    Results/                   HeapTypeRow, HeapObjectSample, AllocationCallTree
    Events/                    IApplicationEvent, 事件记录类型
  DotnetAnalysis.Application/
    Contracts/                 IProcessCatalog, ICaptureCoordinator, IAnalysisService
    UseCases/                  StartCapture, FinishCapture, CancelCapture, ImportGcdump
    Sessions/                  AnalysisSessionCoordinator, 会话状态机
    Events/                    IEventBus, InProcessEventBus, 订阅生命周期
    Errors/                    AnalysisFailure, 用户可见错误映射
  DotnetAnalysis.Diagnostics/
    Processes/                 CoreClrProcessCatalog, RuntimeProbe
    Capture/                   EventPipeAllocationTraceCapture, GcDumpSnapshotCapture
    Readers/                   GcDumpReader, NetTraceReader, IAnalysisFileReader
    Analysis/                  HeapAnalyzer, AllocationAnalyzer, ResultCorrelator
  DotnetAnalysis.Desktop/
    Infrastructure/            ObservableObject, RelayCommand, AsyncRelayCommand
    ViewModels/                ProcessList, Capture, Results, TypeDetail
    Views/                     对应 XAML 页面
    Composition/               服务注册、UI 调度器适配器
```

接口、会话状态和结果模型的命名以领域含义为准；采集器和读取器的具体技术名仅停留在 `Diagnostics` 内部。

## 5. 技术栈

- .NET 10，使用 `global.json` 固定 SDK 版本。
- WPF 桌面界面。
- 自研极简 MVVM，不引入第三方 MVVM 包。
  - `ObservableObject`：`INotifyPropertyChanged` 和 `SetProperty`。
  - `RelayCommand`：同步命令。
  - `AsyncRelayCommand`：异步执行状态、取消入口和异常回传。
- 内置依赖注入和日志抽象。
- `Microsoft.Diagnostics.NETCore.Client`：进程发现、诊断连接、EventPipe 会话和 dump 相关能力。
- TraceEvent：`.gcdump` / `.nettrace` 读取和事件分析。
- 后续 `.dmp` 读取器将接入 ClrMD，但不是首版依赖。

不使用外部 `dotnet-gcdump` 或 `dotnet-trace` 进程作为运行时依赖；这些 CLI 工具仅用于人工交叉验证和测试数据准备。

### 5.1 包与版本策略

| 位置 | 技术/包 | 作用 | 版本策略 |
| --- | --- | --- | --- |
| 全部项目 | .NET 10 / C# | 运行时与语言基线 | 用 `global.json` 固定已验证 SDK；不跟随本机预览 SDK 漂移 |
| `Desktop` | WPF | Windows 桌面 UI | 仅使用框架自带控件和自研 MVVM 基础类 |
| `Application` | `Microsoft.Extensions.DependencyInjection`、`Microsoft.Extensions.Logging.Abstractions` | 组合根依赖注入、结构化日志抽象 | 只使用必要基础包，不引入通用消息总线框架 |
| `Diagnostics` | `Microsoft.Diagnostics.NETCore.Client` | PID 诊断连接与 EventPipe 会话 | 与支持的 .NET 运行时组合做兼容性测试 |
| `Diagnostics` | TraceEvent | `.gcdump`、`.nettrace` 的读取和事件处理 | 先以固定版本验证样本，再做升级 |
| 未来扩展 | ClrMD | `.dmp` 读取与离线堆分析 | 不进入首版项目引用 |

首版不使用 ORM、数据库、第三方 MVVM、第三方事件总线、反射扫描式容器、源生成命令或网络符号下载。它们都不解决首版核心问题，并会扩大启动、调试或诊断过程的复杂度。

## 6. 应用层接口与职责

`Application` 提供明确的用例入口，至少包括：

- 列出并预检可分析进程。
- 开始采集会话。
- 正常结束采集并开始分析。
- 取消采集会话。
- 导入 `.gcdump`。
- 查询分析会话、类型排行、类型详情和分配调用树。

`Diagnostics` 通过接口实现下列能力：

- 进程发现与 CoreCLR 能力探测。
- 分配追踪的开始、停止与文件写入。
- 堆快照采集。
- `.gcdump` 读取。
- `.nettrace` 读取。
- 未来 `.dmp` 读取。

首版在单个桌面进程中运行采集与分析；应用层接口不暴露进程内实现，以便未来增加本地提权采集助手而不改 UI 或分析模型。

### 6.1 会话状态机

```text
Created
  -> Preflighting
  -> CapturingAllocations
  -> FinishingTrace
  -> CapturingHeapSnapshot
  -> Analyzing
  -> Completed

Created/Preflighting/CapturingAllocations/FinishingTrace/CapturingHeapSnapshot/Analyzing
  -> Canceling -> Canceled

Canceling
  -> Failed（活动阶段或清理失败）

任意非终态
  -> Failed
```

状态机只允许 `Application` 的 `AnalysisSessionCoordinator` 写入。UI、采集器和分析器只能请求动作或发布事实，不得跳过状态机修改会话状态。

### 6.2 关键用例的责任链

**开始采集**：`StartCapture` 创建 `SessionId` 与临时工作区，执行目标进程预检，写入 `Preflighting`，预检成功后启动 EventPipe 分配追踪，状态变为 `CapturingAllocations`。

**结束采集并分析**：`FinishCapture` 只在 `CapturingAllocations` 状态可执行。它先禁止重复结束请求，再停止并关闭追踪文件，进入 `FinishingTrace`；追踪文件确认可读后采集 `.gcdump`，进入 `CapturingHeapSnapshot`；快照确认可读后，按顺序执行堆读取、分配追踪读取、类型关联，进入 `Analyzing`；成功时生成不可变报告并进入 `Completed`。

**取消/关闭**：`CancelCapture` 可以由“取消”命令、关闭采集页面或退出程序触发。它使会话进入 `Canceling`，并发发出任务令牌取消与幂等的后端升级取消，等待两条路径受控收敛，删除临时采集文件，最后进入 `Canceled`。若活动阶段或清理失败，则在清理尝试完成后由 `Canceling` 进入 `Failed`。取消不会自动采集堆快照，也不会产生可浏览的结果页。

**导入**：`ImportGcdump` 直接创建会话并进入 `Analyzing`，不允许在导入会话上执行开始、结束或取消附着的采集命令。

### 6.3 错误与访问能力

进程预检必须把原始异常映射为稳定、可显示的能力状态：非 CoreCLR、架构不匹配、诊断连接不可用、访问被拒绝、目标已退出、存储空间不足、采集超时、解析失败和用户取消。UI 根据能力状态决定是否允许开始采集；不得依赖异常字符串或日志文本控制按钮状态。

## 7. 事件总线

模块间自研强类型事件总线位于 `Application`，用于状态变化与广播通知；命令、查询、返回值和取消仍使用显式接口调用。

事件总线要求：

- 不使用静态全局单例、字符串 Topic、反射或序列化。
- 使用不可变强类型事件。
- 所有会话相关事件都携带 `SessionId`。
- 提供 `PublishAsync<TEvent>()` 和 `Subscribe<TEvent>()`，订阅返回可释放句柄。
- 采用有界异步队列；进度事件允许合并并只保留最新状态，慢订阅者不得阻塞采集文件写入。
- 单个订阅者异常隔离并发布 `ModuleFaulted`，不得中断其他订阅者。
- 会话关闭或取消时释放该会话订阅，防止内存泄漏。
- WPF 层订阅后通过 UI 调度器更新 ViewModel。

### 7.1 事件总线的精确边界

事件总线是**进程内、强类型、异步发布订阅**机制。它不跨进程、不落盘、不支持回放、不保证消息在应用重启后保留，也不是命令总线。

需要结果、失败反馈、取消语义或严格先后关系的调用，必须使用接口方法并等待返回；例如 `FinishCaptureAsync`、`CancelCaptureAsync`、读取分析结果。事件总线只发布已经发生的状态变化，让 UI、日志或独立模块被动响应。

建议的最小 API 语义为：

```text
PublishAsync<TEvent>(TEvent event, CancellationToken)
Subscribe<TEvent>(handler, subscriptionOptions) -> IDisposable
```

`TEvent` 必须实现 `IApplicationEvent`，并包含 `OccurredAt`、可空 `SessionId`、事件来源模块标识。发布后事件对象不可再修改。

### 7.2 首版事件目录

| 事件 | 发布者 | 用途 | 是否允许合并 |
| --- | --- | --- | --- |
| `ProcessProbeCompleted` | 进程预检 | 更新可分析进程状态 | 是，同 PID 仅保留最新 |
| `CaptureStarted` | 会话协调器 | 进入采集页面状态 | 否 |
| `CaptureProgressChanged` | 采集器 | 文件写入量、持续时间、当前阶段 | 是，同会话仅保留最新 |
| `CaptureStopRequested` | 会话协调器 | 记录用户请求停止 | 否 |
| `TraceSaved` | 采集器 | `.nettrace` 已完整关闭 | 否 |
| `HeapSnapshotSaved` | 采集器 | `.gcdump` 已完整写入 | 否 |
| `AnalysisStarted` | 会话协调器 | 结果页进入分析中状态 | 否 |
| `AnalysisProgressChanged` | 分析器 | 当前分析阶段和进度 | 是，同会话仅保留最新 |
| `AnalysisCompleted` | 会话协调器 | 不可变报告已可查询 | 否 |
| `AnalysisCanceled` | 会话协调器 | 会话取消完成 | 否 |
| `AnalysisFailed` | 会话协调器 | 结构化失败原因 | 否 |
| `ModuleFaulted` | 事件总线/模块边界 | 订阅处理异常或模块故障 | 否 |

事件不携带完整对象图、完整调用树或大量字节数组。大结果由 `IAnalysisService` 按会话 ID 查询，事件只传递状态、标识和轻量摘要。

### 7.3 队列、顺序与失败处理

- 每个订阅拥有独立的有界队列和单一消费循环；一个慢订阅者不阻塞其他订阅者，也不阻塞诊断采集线程。
- 同一订阅内，非进度事件按发布顺序串行处理。会话结束、失败、取消等终态事件不得被合并或静默丢弃。
- 进度事件使用“按会话和事件类型覆盖最新值”策略；UI 只需最新进度，不需要每一个中间值。
- 队列满时，优先合并/丢弃可合并进度事件；若非可合并事件无法投递，记录总线故障并使所属会话以受控失败结束，不允许悄悄丢失终态事件。
- 订阅处理器抛出异常时，记录原始异常并生成轻量 `ModuleFaulted` 通知。总线不得递归地为处理 `ModuleFaulted` 失败再次发布 `ModuleFaulted`。
- 订阅按所有者会话或应用生命周期注册；会话终态后协调器释放该会话订阅。应用退出时停止接收新事件、释放全部订阅并请求处理器取消，排空已接收事件并等待合作处理器在有限关闭预算内完成；预算耗尽仍在运行的非合作处理器必须记录告警，不能无限阻塞 WPF 关闭，也不能声称可强制终止任意处理器代码。进程退出会终止仍残留的用户代码。

## 8. 分析模型与展示

`Core` 中的稳定模型至少包括：

- `TargetProcess`：进程身份、运行时信息、可用能力。
- `AnalysisSession`：会话身份、状态、时间范围、采集文件位置和失败信息。
- `HeapTypeRow`：类型名、程序集、对象数、对象总大小、平均大小、精度。
- `HeapObjectSample`：类型详情中的最大对象样本与大小分布。
- `AllocationCallTree`：按类型聚合的分配调用树、采样分配字节数、采样次数、时间范围与精度。
- `MemoryAnalysisReport`：会话概览、类型排行、类型详情与分配来源的查询入口。

首版结果页包含：

1. 会话概览：目标进程、运行时版本、采集时间和分析状态。
2. 类型排行：类型名、对象数、对象总大小、平均对象大小和精度标记。
3. 类型详情：最大对象样本、大小分布和所属程序集。
4. 分配来源：调用树、采集窗口内的分配字节数、采样次数、时间范围与精度标记。

调用栈仅使用追踪中已有的方法和程序集信息；本地符号无法解析时显示原始帧，不联网下载符号。

### 8.1 类型关联规则

类型关联使用 `TypeIdentity`。优先键为“程序集标识 + 完整类型名”；当追踪数据只包含类型名时，降级为规范化完整类型名，并将关联质量标记为 `NameOnly`。泛型类型名、嵌套类型和数组类型必须经过同一规范化函数后才能参与关联。

没有匹配到堆快照的分配热点仍应显示，标记为“采集窗口内发生分配，但结束时未在快照中匹配到存活类型”；没有匹配到分配事件的高占用类型仍应显示，标记为“当前占用高，但采集窗口内未观察到分配来源”。

### 8.2 查询与 UI 数据量控制

结果服务按查询返回页面模型，而不是把完整堆图推给 UI：

- 类型排行支持排序、筛选和分页。
- 只有选中类型后才构造其最大对象样本、大小分布和分配调用树。
- 调用树节点按需展开；每一层只返回直接子节点摘要。
- `MemoryAnalysisReport` 是不可变会话结果索引，不是 UI 持有完整原始数据的容器。

## 9. 性能、并发与资源约束

- 采集文件写入和解析不得运行在 UI 线程。
- `.nettrace` 采集时直接顺序写入临时文件；停止后先完成文件关闭，再采集 `.gcdump`。
- 后台解析按阶段执行，不并行持有大型对象图和大型追踪数据，控制工具自身峰值内存。
- UI 仅持有面向页面的汇总行、分页/延迟加载的对象样本与调用树节点，不持有完整原始图。
- 每个会话有独立取消令牌和临时目录；取消必须停止诊断会话、等待受控收尾并删除临时目录。
- 错误以结构化会话状态和用户可理解的失败原因呈现，底层异常仅进入诊断日志。

### 9.1 任务所有权

`AnalysisSessionCoordinator` 是每个会话的唯一任务所有者，持有会话级 `CancellationTokenSource` 和受控任务集合。采集器只负责开始/停止自己的 EventPipe 会话；读取器和分析器只接受文件路径、取消令牌和进度回调；它们不得创建脱离会话的后台任务。

所有状态转换、采集文件改名、删除临时目录和完成/失败事件都由协调器串行编排。任何阶段失败都取消尚未开始或仍在运行的后续阶段，并清理不完整文件。

### 9.2 内存预算原则

首版不承诺任意大小进程都可在固定内存内完成分析，但必须遵守：不在 UI 线程加载原始数据；不同时保留堆图与完整追踪事件列表；优先流式读取和增量聚合；对类型排行、对象样本、调用树均设置页面级上限和延迟展开。超出安全预算时以明确失败状态结束，不允许工具自身无限增长或无响应。

## 10. 测试策略

- `Core`：类型标识规范化、精度传播、调用树聚合、结果排序。
- `Application`：正常结束、取消关闭、导入、失败状态转换、事件顺序、订阅释放。
- `Diagnostics`：使用采集文件样本验证 `.gcdump` / `.nettrace` 读取；对 EventPipe 客户端使用可替代边界测试错误映射。
- `Desktop`：ViewModel 状态和命令测试；不把真实进程附着作为常规单元测试前提。
- 手工/集成验证：选取受控 .NET 8/9/10 x64 示例进程，与本机 `dotnet-gcdump`、`dotnet-trace` 结果交叉比较。

## 11. 明确的后续扩展点

- `.dmp` 文件读取器（ClrMD）。
- 低版本或不同 CLR 运行时适配器。
- 本地提权采集助手。
- GC Root 引用路径、支配树和对象字段浏览。
- 多快照增长对比。
- 导出报告。

这些能力均不得改变首版的 `Core` 结果模型、`Application` 用例入口和 WPF 调用路径，只能新增具体读取器、分析器或展示页面。
