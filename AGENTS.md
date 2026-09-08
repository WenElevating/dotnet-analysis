# dotnet-analysis

## 项目概述

这是一个基于 .NET 10 的 Windows 托管内存诊断桌面应用。它枚举并附着本机进程，采样进程内存，捕获或导入 `.gcdump` 快照，并提供类型、对象和引用路径分析。

## 项目架构

依赖方向固定为 `Core <- Application <- Diagnostics`，`Desktop` 作为组合根和展示层引用前三者：

- `src/DotnetAnalysis.Core`：领域模型、快照模型与领域事件；不得依赖诊断 SDK、Windows API 或 UI。
- `src/DotnetAnalysis.Application`：应用契约、诊断门面、编排和事件契约；不得泄漏 `DiagnosticsClient`、EventPipe、临时文件路径等基础设施细节。
- `src/DotnetAnalysis.Diagnostics`：Windows 进程枚举、EventPipe、`.gcdump`、快照存储、采样和分析实现；通过 `AddWindowsProcessDiagnostics()` 注册服务。
- `src/DotnetAnalysis.Desktop`：WPF 桌面程序、组合根、视图模型和基础设施；在 `App.xaml.cs` 组合 Desktop 与 Diagnostics 服务。
- `tests/DotnetAnalysis.Tests`：单元、架构与应用层测试。
- `tests/DotnetAnalysis.Diagnostics.IntegrationTests`：真实 Windows/EventPipe/.gcdump 集成测试。
- `tests/DotnetAnalysis.Diagnostics.TestTarget`：供诊断集成测试附着的多运行时目标程序（net8.0、net9.0、net10.0）。

## 项目文档指引

- 知识文档路径：

`docs/superpowers/specs/`、`docs/superpowers/plans/` 与 `docs/superpowers/evidence/` 是规格、计划和验证产物，不是项目知识文档；除非任务明确要求，不要将其作为长期行为约定的唯一来源。

## 运行命令

在仓库根目录执行：

```powershell
dotnet build .\DotnetAnalysis.sln --configuration Debug
dotnet test .\DotnetAnalysis.sln --configuration Debug --no-build
```

只运行常规 Windows 诊断集成测试：

```powershell
dotnet test .\tests\DotnetAnalysis.Diagnostics.IntegrationTests\DotnetAnalysis.Diagnostics.IntegrationTests.csproj --configuration Debug --no-build --filter "TestCategory=WindowsDiagnosticsIntegration"
```

百万对象性能测试默认不执行；需要显式启用：

```powershell
$env:DOTNET_ANALYSIS_RUN_LARGE_SNAPSHOT_BENCHMARK = 'true'
dotnet test .\tests\DotnetAnalysis.Diagnostics.IntegrationTests\DotnetAnalysis.Diagnostics.IntegrationTests.csproj --configuration Debug --no-build --filter "FullyQualifiedName~LargeSnapshotBenchmark_RecordsCaptureAndCachedQueryMeasurements"
```

## 编码规范

- 保持既有分层边界：功能应放在所属层。诊断采集、EventPipe、`.gcdump` 和快照分析只能在 `Diagnostics` 实现；`Application` 负责契约和编排；`Desktop` 只负责展示与组合，不能承载诊断实现。
- 保持高内聚、低耦合并面向接口编程。一个类及其成员应各自承担清晰且不重叠的职责；跨层能力通过既有契约注入和使用，避免把实现细节、状态管理或业务编排重复堆叠在调用方。
- 编码时优先考虑性能，依据数据访问和分配特征选择合适的数据结构与算法；热点路径可使用集合、值类型、`Span<T>`/`ReadOnlySpan<T>` 等手段减少查找、复制和分配，但必须以可复现的测试或基准证明收益。
- IO 密集型操作优先采用真正异步的 API，并传播 `CancellationToken`；CPU 密集型操作应使用线程池或受控并行机制，避免在 UI 线程执行，也不要用无界并发或伪异步包装阻塞 IO。
- 复杂业务流程除取消机制外，还必须结合实际的外部依赖、预期时限、幂等性和失败类型，设计恰当的超时与重试机制；只对可恢复的瞬时失败重试，并限制次数、退避策略和总耗时，避免对取消、参数错误或不可恢复失败重试。
- 严禁反向或跨层依赖。项目引用和运行时访问必须遵守既定依赖方向；高层代码不得直接依赖低层实现细节。`Desktop` 仅可在组合根调用 Diagnostics 的注册扩展，业务与视图模型通过 Application 契约访问诊断能力。
- 使用 C# `latest`、可空引用类型和隐式 using；构建开启最新推荐分析器、代码风格检查，并将警告视为错误。公共 API、公共类型、枚举值和异常语义使用中文 XML 文档，补齐 `summary`、`param`、`returns` 与必要的 `exception`。
- 新增或实质修改的类、接口、结构、记录、枚举、委托、构造函数、方法和局部函数必须有中文注释；可生成 XML 文档的声明使用 XML 文档注释，并按实际签名补齐 `summary`、`param`、`returns` 与必要的 `exception`。注释必须说明职责、关键输入/输出或状态边界、并发/取消/资源生命周期及重要设计取舍（适用时），不得只复述类型或成员名称；重写或实现已有契约时可使用准确的 `<inheritdoc/>`。

## 项目约定

- `IProcessDiagnostics` 与 `IProcessDiagnosticsSession` 是上层访问诊断能力的边界；Application 和 Desktop 不直接使用 EventPipe、`DiagnosticsClient`、Windows API 或快照临时目录。
- 复用既有 `IEventBus`，不要新增并行事件总线；诊断 SDK 类型保持在 Diagnostics 层内部。
- 捕获前和每次捕获时验证 PID 与进程启动时间，防止 PID 复用；稳定失败使用 `DiagnosticsException` 和既有 `DiagnosticsErrorCode`。
- 大快照按对象数判定：对象数达到 100,000 时只允许分页读取，完整对象枚举必须返回 `SnapshotTooLargeForFullEnumeration`。
- 当前快照索引缓存采用单飞加载；单个调用取消只取消等待，不取消共享解析。切换快照后不保留历史解析缓存。
- 修改诊断功能时，至少运行受影响的单元测试和 Windows 集成测试；涉及大快照、索引、捕获或序列化时，额外运行显式启用的百万对象性能测试。
- Diagnostics 层新增公开接口时，必须增加并执行性能测试和压力测试，覆盖该接口适用的正常、边界、并发以及取消或失败负载场景。
