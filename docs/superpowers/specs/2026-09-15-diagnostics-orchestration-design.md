# 诊断编排层 V1 设计

## 1. 目标与范围

本设计为 Windows x64 内存分析工具建立宿主无关的诊断业务编排层。编排层位于 `Application` 与桌面或未来其他宿主之间，把进程查找、EXE 启动、诊断附着、实时采样、快照捕获和快照查询组合成稳定的用户业务流程。

本阶段不实现 WPF 页面、WebView、D3D 绘制、主题或 UI 文案布局。Desktop 仍是当前组合根和展示壳；未来其他壳可以复用同一编排层。

V1 支持：

- 查找本机可诊断的 .NET 进程；
- 启动一个 EXE 并自动附着；
- 附着已运行进程；
- 持续读取进程内存时间线；
- 捕获标准 GCDump 和 Retention Profiler 快照；
- 打开导入快照；
- 查询类型、对象分页、引用路径、保留路径、根分类和快照对比；
- 统一取消、状态转换、失败结果和资源收尾。

不在本阶段支持：远程进程、Linux、容器、`.dmp` 解析、提权启动、启动前暂停、子进程树选择、环境变量编辑和 UI 专属模型。

## 2. 分层与依赖

```text
Core
  ↑
Application
  ↑
Orchestration
  ↑
Desktop / future hosts / CLI / automation
```

新增项目：

```text
src/DotnetAnalysis.Orchestration
tests/DotnetAnalysis.Orchestration.Tests
```

`DotnetAnalysis.Orchestration` 只能引用 `DotnetAnalysis.Core` 和 `DotnetAnalysis.Application`。它不得引用 WPF、`System.Windows`、`Dispatcher`、XAML、`DiagnosticsClient`、EventPipe、Profiler 原生类型或 Diagnostics 项目具体实现。

Diagnostics 继续通过 `IProcessDiagnostics`、`IProcessDiagnosticsSession` 和 `IMemorySnapshotAnalysisService` 提供能力。若启动 EXE 缺少稳定的 Application 契约，只补充最小的宿主无关启动接口，不让 Desktop 直接调用 `Process.Start` 绕过编排层。

## 3. 核心领域词汇

| 业务概念 | 名称 | 说明 |
| --- | --- | --- |
| 编排层入口 | `DiagnosticsApplication` | 当前宿主使用的诊断业务入口 |
| 查找目标 | `TargetProcessFinder` | 枚举、过滤、搜索、排序和校验可选目标 |
| 启动目标 | `TargetProcessStarter` | 启动 EXE 并返回已校验的目标进程 |
| 活动会话 | `AnalysisSession` | 一次持续诊断生命周期，包含采样和捕获 |
| 快照查询上下文 | `SnapshotAnalysis` | 一个快照的摘要、分页和路径查询入口 |
| 当前操作 | `DiagnosticOperation` | 可取消、可观察的用户操作状态 |

避免使用 `Coordinator`、`Manager`、`Catalog` 和 `Workspace` 作为主要公共业务名；这些词不能表达用户正在完成的动作或对象。

## 4. 宿主入口

编排层对宿主公开 `IDiagnosticsApplication`。它提供当前应用状态和四类入口操作：查找目标、附着或启动、打开快照、关闭当前上下文。

建议的初始形状：

```csharp
public interface IDiagnosticsApplication : IAsyncDisposable
{
    DiagnosticsApplicationState State { get; }

    Task<IReadOnlyList<TargetProcess>> FindProcessesAsync(
        ProcessFilter filter,
        CancellationToken cancellationToken);

    Task<AnalysisSession> AttachAsync(
        TargetProcess target,
        CancellationToken cancellationToken);

    Task<AnalysisSession> StartAsync(
        LaunchTarget target,
        CancellationToken cancellationToken);

    Task<SnapshotAnalysis> OpenSnapshotAsync(
        string filePath,
        CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}
```

具体返回类型可以在实现阶段根据生命周期和接口隔离调整，但不得返回 WPF 对象或底层 Diagnostics 实现对象。

`DiagnosticsApplication` 负责当前应用上下文的互斥与替换：同一时刻最多一个活动目标会话和一个当前快照分析上下文。切换目标或关闭应用时，必须先取消并等待旧操作收尾，再发布新状态；旧会话的事件和查询结果不得污染新目标。

## 5. 目标进程查找

`TargetProcessFinder` 负责用户为了附着而进行的进程查找，不负责附着本身。

候选目标必须包含：

- PID；
- 启动时间；
- 进程名；
- 可读取的 EXE 路径；
- 进程内存；
- 位数；
- 运行时识别结果；
- 是否可附着；
- 不可附着的稳定原因；
- 本次刷新时间。

进程唯一身份始终使用 `ProcessId + StartedAtUtc`，不能只使用 PID。刷新、排序和搜索都必须对已退出进程保持安全：消失的进程从新结果移除，正在附着的目标继续通过身份验证决定是否可用。

`ProcessFilter` 只表达查询条件，例如名称、路径、运行时、位数和排序；不携带 UI 控件或分页控件类型。

## 6. EXE 启动与自动附着

`TargetProcessStarter` 只支持 V1 的最小启动语义：EXE 路径、命令行参数和工作目录。工作目录默认使用 EXE 所在目录，启动权限使用当前用户权限。

启动流程：

1. 校验路径和参数；
2. 启动目标；
3. 在有限总超时内等待可识别的目标进程；
4. 通过 PID 与启动时间建立 `TargetProcess` 身份；
5. 将目标交给 `AnalysisSession` 附着；
6. 附着失败时返回稳定错误并按策略处理由本次启动创建的进程。

V1 不实现提权、启动前暂停、环境变量编辑、服务进程启动、跨用户会话启动和子进程树选择。启动等待、附着和自动清理由编排层控制取消与总超时，不能无限等待。

## 7. 活动分析会话

`AnalysisSession` 封装一个 `IProcessDiagnosticsSession`，但不把该底层对象暴露给宿主。它负责：

- 附着后的生命周期状态；
- 进程内存时间线的有界保存和读取；
- 标准快照和 Retention 快照捕获；
- 执行采样时间区间查询；
- 目标退出、PID 复用、取消和结束；
- 将诊断事件转换为宿主可消费的状态更新。

时间线必须保留采样质量信息，至少区分有效样本、缺失样本、采样中断和会话结束。分配热点和执行采样结果必须保留“采样结果”语义，编排层不得把它们表述为每次分配或完整执行记录。

Retention 捕获不可用时必须返回 `ProfilerAttachUnavailable` 或 `ProfilerCaptureFailed` 等稳定错误，不得回退为普通 GCDump 并标记为保留分析成功。

## 8. 快照分析上下文

`SnapshotAnalysis` 封装 `IMemorySnapshotAnalysisService`，对宿主提供：

- 快照摘要和分析状态；
- 类型统计；
- 对象分页；
- 引用路径；
- Retention 保留路径；
- GC Root 分类；
- 支配树等派生分析；
- 快照对比；
- 数据质量、根证据质量、符号状态和分页限制。

大快照自动遵守 Diagnostics 已有的对象数门禁。对象数达到 100,000 时，编排层不得尝试完整对象枚举，而应返回分页能力和 `SnapshotTooLargeForFullEnumeration` 语义。

基础索引失败、派生分析失败和查询限制必须分层表达：派生分析失败不能把仍可查询的类型和对象基础索引标记为整个快照不可用。

导入快照的源文件不得被修改。打开、分析和关闭快照时，编排层只管理快照句柄和状态，不暴露 Diagnostics 内部临时路径。

## 9. 状态与错误

`DiagnosticsApplicationState` 至少包括：

- 当前模式：开始、查找目标、活动分析、快照分析、关闭；
- 当前目标身份；
- 当前会话状态；
- 当前快照列表和当前快照；
- 最新内存样本和采样质量摘要；
- 当前 `DiagnosticOperation`；
- 稳定错误码、失败阶段和是否可重试。

状态更新必须单调且与实际生命周期一致。取消不能被报告为成功；目标退出、PID 复用、权限不足、Profiler 冲突、磁盘不足和不支持运行时不能被包装为通用成功结果。

编排层可以提供面向宿主的错误结果，但不应暴露原始异常文本、EventPipe 类型、Native DLL 路径或内部临时目录。错误映射必须保留 `DiagnosticsErrorCode`，并允许不同宿主自行决定最终文案。

## 10. 并发、取消和资源生命周期

- 同一工作区最多一个活动目标会话；切换前先取消并等待旧会话；
- 同一会话的捕获操作遵循 Diagnostics 的并发限制；
- 单个查询取消只取消该调用等待，不取消共享索引解析；
- 所有启动、附着、捕获、索引和分析操作都有总超时与取消令牌；
- `CloseAsync` 和 `DisposeAsync` 必须幂等；
- 旧操作完成时必须检查操作代次或会话 ID，不能覆盖新状态；
- 任何失败路径都要等待会话、后台采样、文件句柄和临时目录收尾；
- 编排层不自行缓存完整对象图，也不延长快照解析缓存生命周期。

## 11. 测试设计

新增 `DotnetAnalysis.Orchestration.Tests`，使用 Application 契约的测试替身覆盖：

- 进程查找过滤、排序、去重和进程消失；
- PID 复用保护；
- 附着成功、拒绝和取消；
- EXE 启动成功、等待超时、目标未出现和附着失败；
- 会话切换时旧操作取消和事件隔离；
- 实时样本保存、缺失和中断状态；
- 标准与 Retention 捕获的质量区分；
- Retention 不可用时禁止静默降级；
- 快照导入不修改源文件；
- 大快照分页门禁；
- 基础索引可用而派生分析失败；
- 引用路径、保留路径和快照对比结果转发；
- 并发查询、取消和关闭；
- 资源收尾、幂等释放和状态代次保护；
- 服务注册、项目依赖方向和中文 XML 文档。

实现后至少运行：

```powershell
dotnet build .\DotnetAnalysis.sln --configuration Release --property:Platform=x64
dotnet test .\tests\DotnetAnalysis.Orchestration.Tests\DotnetAnalysis.Orchestration.Tests.csproj --configuration Release
dotnet test .\tests\DotnetAnalysis.Tests\DotnetAnalysis.Tests.csproj --configuration Release --no-build
dotnet test .\tests\DotnetAnalysis.Diagnostics.IntegrationTests\DotnetAnalysis.Diagnostics.IntegrationTests.csproj --configuration Release --no-build --filter "TestCategory=WindowsDiagnosticsIntegration"
```

## 12. 分阶段实施

第一阶段只实现编排层基础骨架、契约、状态模型、目标查找、附着和关闭。

第二阶段加入 EXE 启动适配器、自动附着和启动失败恢复。

第三阶段加入实时内存时间线、标准/Retention 捕获和执行采样入口。

第四阶段加入快照分析上下文、分页、引用路径、Retention 路径和快照对比。

第五阶段补齐并发、取消、错误恢复、资源收尾和组合测试。

每阶段都保持 Desktop 可继续构建，但不要求 UI 在本阶段接入新流程。