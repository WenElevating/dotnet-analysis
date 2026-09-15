# 生产级诊断编排层 V1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Each task must be completed, tested, reviewed, and committed before moving to the next task.

**Goal:** 在不泄漏 Diagnostics/WPF 实现细节的前提下，建立可被 WPF、CLI 或未来其他宿主复用的生产级诊断编排层，覆盖目标查找、能力探测、启动/附着、活动会话、多快照分析、取消/超时、错误恢复和四层业务验收。

**Architecture:** `Core <- Application <- Orchestration <- Host`。`Orchestration` 只依赖 Core/Application；Diagnostics 通过 Application 契约提供真实能力；Desktop 仅在组合根注册并消费编排接口，不直接编排诊断流程。单元测试独立验证接口和规则；集成、性能、压力测试使用真实 Windows 业务流程。

**Tech stack:** C# latest、.NET 10、MSTest 4、Microsoft.Extensions.DependencyInjection、现有 `IEventBus`、Windows x64、现有 Diagnostics 集成测试基础设施。

---

## Global Constraints

- 支持 Windows x64、Windows 10 22H2/Windows 11 22H2 及以上以及 .NET 8/9/10 CoreCLR 目标。
- `DotnetAnalysis.Orchestration` 不得引用 WPF、`System.Windows`、Diagnostics 具体实现、EventPipe、Profiler 原生类型或内部临时路径。
- 宿主只能看到稳定模型、接口、质量摘要和 `DiagnosticsErrorCode`；不得看到原始异常、Native DLL 路径或 Diagnostics 临时目录。
- 目标身份始终使用 PID + `StartedAtUtc`；任何附着、捕获、查询前必须重新验证身份。
- 对象数达到 100,000 时只允许分页；Retention 失败不得静默降级为标准快照成功。
- 同一应用上下文最多一个活动目标会话；所有状态和事件携带 generation/session/operation 身份，旧代次不得覆盖新代次。
- 单元测试是独立接口/规则测试；集成、性能、压力测试才执行真实完整业务流程。
- 每个任务结束时运行对应最小测试并提交；不在本计划中修改无关 UI 功能或 Diagnostics 算法。
- 所有新增公开类型、接口、枚举、构造函数和方法补充中文 XML 文档。

---

## File Structure

### 新增源项目

- `src/DotnetAnalysis.Orchestration/DotnetAnalysis.Orchestration.csproj`：net10.0、Core/Application 引用、无 WPF/Diagnostics 引用。
- `src/DotnetAnalysis.Orchestration/DependencyInjection/OrchestrationServiceCollectionExtensions.cs`：注册编排层公共实现和默认策略。
- `src/DotnetAnalysis.Orchestration/DiagnosticsApplication.cs`：应用上下文、当前 generation、活动会话和快照集合所有权。
- `src/DotnetAnalysis.Orchestration/TargetProcessFinder.cs`：候选目标查询、过滤、排序和去重。
- `src/DotnetAnalysis.Orchestration/TargetCapabilityProbe.cs`：选中目标后的运行时和能力探测。
- `src/DotnetAnalysis.Orchestration/AnalysisSession.cs`：活动会话生命周期、样本流、捕获、执行采样和停止。
- `src/DotnetAnalysis.Orchestration/SnapshotCollection.cs`：多快照、当前快照、基线/候选快照和关闭。
- `src/DotnetAnalysis.Orchestration/SnapshotAnalysis.cs`：单快照基础查询、分页、路径和派生分析转发。
- `src/DotnetAnalysis.Orchestration/Operations/DiagnosticOperation.cs`：宿主可观察的操作状态。
- `src/DotnetAnalysis.Orchestration/Operations/OperationScope.cs`：内部截止时间、取消、generation/session 隔离。
- `src/DotnetAnalysis.Orchestration/Models/*.cs`：`TargetContext`、`DiagnosticCapabilities`、`ProcessFilter`、`LaunchTarget`、应用/会话/快照状态和质量模型。

### Application/Core 契约扩展

- `src/DotnetAnalysis.Application/Contracts/Diagnostics/ITargetProcessLauncher.cs`：宿主无关的启动目标契约（若现有契约检查确认可放入现有 `IProcessDiagnostics`，则保留单一边界而不新增重复入口）。
- `src/DotnetAnalysis.Application/Contracts/Diagnostics/TargetProcessLaunchRequest.cs`：EXE、参数、工作目录和启动策略。
- `src/DotnetAnalysis.Application/Contracts/Diagnostics/TargetProcessLaunchResult.cs`：启动后 PID、启动时间和身份验证结果。
- `src/DotnetAnalysis.Application/Contracts/Diagnostics/TargetProcessCapabilities.cs`：Diagnostics 侧可验证能力输入。
- `src/DotnetAnalysis.Core/Diagnostics/DiagnosticQuality.cs`、`DiagnosticCapability.cs`：跨层稳定质量和能力值（若已有等价模型则复用，不重复创建）。

### 新增单元测试项目

- `tests/DotnetAnalysis.Orchestration.Tests/DotnetAnalysis.Orchestration.Tests.csproj`：net10.0-windows、MSTest、Core/Application/Orchestration 引用。
- `tests/DotnetAnalysis.Orchestration.Tests/Fakes/FakeProcessDiagnostics.cs`：Application 契约替身。
- `tests/DotnetAnalysis.Orchestration.Tests/Fakes/FakeTargetProcessLauncher.cs`：可控启动替身。
- `tests/DotnetAnalysis.Orchestration.Tests/Fakes/RecordingEventBus.cs`：事件断言替身。
- `tests/DotnetAnalysis.Orchestration.Tests/ApplicationContractTests.cs`：独立入口接口契约测试。
- `tests/DotnetAnalysis.Orchestration.Tests/TargetProcessFinderTests.cs`：去重、过滤、排序和退出规则。
- `tests/DotnetAnalysis.Orchestration.Tests/TargetCapabilityProbeTests.cs`：能力和错误映射。
- `tests/DotnetAnalysis.Orchestration.Tests/OperationScopeTests.cs`：取消、截止时间、代次和幂等收尾。
- `tests/DotnetAnalysis.Orchestration.Tests/AnalysisSessionTests.cs`：会话状态、样本、捕获和退出。
- `tests/DotnetAnalysis.Orchestration.Tests/SnapshotCollectionTests.cs`：多快照和比较选择规则。
- `tests/DotnetAnalysis.Orchestration.Tests/SnapshotAnalysisTests.cs`：分页、基础/派生分析隔离和质量传播。
- `tests/DotnetAnalysis.Orchestration.Tests/DiagnosticsApplicationTests.cs`：上下文替换、旧结果隔离和关闭。

### 集成/性能/压力测试与证据

- `tests/DotnetAnalysis.Diagnostics.IntegrationTests/OrchestrationWorkflowIntegrationTests.cs`：真实 .NET 8/9/10 端到端流程。
- `tests/DotnetAnalysis.Diagnostics.IntegrationTests/OrchestrationFailureWorkflowTests.cs`：退出、PID 复用、权限、Profiler、取消和存储失败。
- `tests/DotnetAnalysis.Diagnostics.IntegrationTests/OrchestrationPerformanceTests.cs`：真实目标、快照、索引、分页、路径和比较测量。
- `tests/DotnetAnalysis.Diagnostics.IntegrationTests/OrchestrationStressTests.cs`：20 轮循环、60 分钟基准和 2 小时 Soak（环境变量显式开启）。
- `eng/Run-OrchestrationAcceptance.ps1`：串行执行四层测试、生成 metadata/summary 和原始测量证据。
- `Run-OrchestrationAcceptance.bat`：Windows CMD 双击入口，默认不启用超长门禁，显式参数启用。

### 现有文件修改

- `DotnetAnalysis.sln`：加入 Orchestration 源项目和单元测试项目。
- `src/DotnetAnalysis.Desktop/DotnetAnalysis.Desktop.csproj`：引用 Orchestration。
- `src/DotnetAnalysis.Desktop/Composition/DesktopServiceCollectionExtensions.cs`：只注册编排层入口，不在 ViewModel 中直接调用 Diagnostics。
- `src/DotnetAnalysis.Desktop/App.xaml.cs`：组合根注册顺序保持 Core/Application/Diagnostics/Orchestration/Desktop。
- `src/DotnetAnalysis.Application/Contracts/Diagnostics/DiagnosticsErrorCode.cs`：仅在现有错误码不足时补充稳定编排阶段错误，并补中文 XML。
- `eng/Run-ReleaseAcceptance.ps1`：把 Orchestration 四层门禁加入发布验收，但保持现有 Diagnostics 门禁独立可运行。

---

## Task 1: 建立项目骨架和最小依赖边界

**Files:**
- Create: `src/DotnetAnalysis.Orchestration/DotnetAnalysis.Orchestration.csproj`
- Create: `tests/DotnetAnalysis.Orchestration.Tests/DotnetAnalysis.Orchestration.Tests.csproj`
- Modify: `DotnetAnalysis.sln`
- Modify: `src/DotnetAnalysis.Desktop/DotnetAnalysis.Desktop.csproj`
- Modify: `tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj` only if architecture tests need explicit project discovery

**Interfaces:**
- Consumes: existing `DotnetAnalysis.Core` and `DotnetAnalysis.Application` projects.
- Produces: buildable Orchestration project and isolated MSTest project; no implementation behavior yet.

- [ ] **Step 1: 写项目边界测试**

在 `tests/DotnetAnalysis.Tests/Architecture/OrchestrationBoundaryTests.cs` 增加测试，断言 Orchestration 程序集不存在 WPF/Diagnostics 程序集引用，并且 Desktop 只通过组合根注册编排层。

- [ ] **Step 2: 创建两个项目并加入 solution**

使用 SDK-style 项目，源项目目标 `net10.0`，测试项目目标 `net10.0-windows`，启用 `Nullable`、`ImplicitUsings`、`LangVersion=latest`，测试项目引用 MSTest 4 和 Core/Application/Orchestration。

- [ ] **Step 3: 运行骨架验证**

Run:

```powershell
dotnet build .\DotnetAnalysis.sln --configuration Debug
 dotnet test .\tests\DotnetAnalysis.Orchestration.Tests\DotnetAnalysis.Orchestration.Tests.csproj --configuration Debug
```

Expected: 编译成功；新测试项目可发现且当前无测试失败。

- [ ] **Step 4: Commit**

```powershell
git add DotnetAnalysis.sln src/DotnetAnalysis.Orchestration tests/DotnetAnalysis.Orchestration.Tests src/DotnetAnalysis.Desktop/DotnetAnalysis.Desktop.csproj
git commit -m "feat: add diagnostics orchestration project boundaries"
```

---

## Task 2: 定义稳定模型、状态和错误映射

**Files:**
- Create: `src/DotnetAnalysis.Orchestration/Models/TargetContext.cs`
- Create: `src/DotnetAnalysis.Orchestration/Models/DiagnosticCapabilities.cs`
- Create: `src/DotnetAnalysis.Orchestration/Models/ProcessFilter.cs`
- Create: `src/DotnetAnalysis.Orchestration/Models/LaunchTarget.cs`
- Create: `src/DotnetAnalysis.Orchestration/Models/DiagnosticsApplicationState.cs`
- Create: `src/DotnetAnalysis.Orchestration/Models/DiagnosticQualitySummary.cs`
- Create: `src/DotnetAnalysis.Orchestration/Models/DiagnosticOperationState.cs`
- Modify: `src/DotnetAnalysis.Application/Contracts/Diagnostics/DiagnosticsErrorCode.cs` only if a missing stable code is proven.
- Test: `tests/DotnetAnalysis.Orchestration.Tests/ApplicationContractTests.cs`

**Interfaces:**
- Consumes: `TargetProcess`, `ProcessDiagnosticsSessionState`, `MemorySnapshot`, `MemorySnapshotCaptureMode`, `DiagnosticsErrorCode`.
- Produces: immutable models containing target identity, capabilities, quality, operation state and application state; no WPF or Diagnostics concrete types.

- [ ] **Step 1: Write failing model/contract tests**

测试至少断言：目标身份必须包含 PID 和启动时间；`ProcessFilter` 不包含 UI 类型；能力可以独立为可用/不可用；错误结果保留错误码、阶段和可重试标记；状态快照包含 generation、session、snapshot 和 operation identities。

- [ ] **Step 2: 实现不可变模型和中文 XML 文档**

所有公开模型使用 `record`/只读属性；构造函数验证非空身份、正数 PID、合法页大小和时间范围。质量模型至少区分 Complete、Partial、Unavailable、Failed。

- [ ] **Step 3: 运行独立单元测试**

```powershell
dotnet test .\tests\DotnetAnalysis.Orchestration.Tests\DotnetAnalysis.Orchestration.Tests.csproj --configuration Debug --filter "FullyQualifiedName~ApplicationContractTests"
```

Expected: 所有接口/模型契约测试通过。

- [ ] **Step 4: Commit**

```powershell
git add src/DotnetAnalysis.Orchestration/Models tests/DotnetAnalysis.Orchestration.Tests/ApplicationContractTests.cs src/DotnetAnalysis.Application/Contracts/Diagnostics/DiagnosticsErrorCode.cs
git commit -m "feat: add orchestration state and quality contracts"
```

---

## Task 3: 实现操作作用域和事件代次隔离

**Files:**
- Create: `src/DotnetAnalysis.Orchestration/Operations/DiagnosticOperation.cs`
- Create: `src/DotnetAnalysis.Orchestration/Operations/OperationScope.cs`
- Create: `src/DotnetAnalysis.Orchestration/Operations/OperationStage.cs`
- Modify: `src/DotnetAnalysis.Application/Events/*` only to add missing operation/generation fields without breaking existing event contracts.
- Test: `tests/DotnetAnalysis.Orchestration.Tests/OperationScopeTests.cs`

**Interfaces:**
- Consumes: `CancellationToken`, `TimeProvider`, existing `IEventBus`, operation options.
- Produces: operation identity, linked cancellation, deadline state, stage/progress and `AcceptsResult(generation, sessionId, operationId)` guard.

- [ ] **Step 1: 写失败测试**

覆盖：创建唯一 operation ID；取消只结束当前操作；截止时间触发超时；generation/session 不匹配拒绝写入；重复完成和释放幂等；旧操作事件被宿主可识别地丢弃。

- [ ] **Step 2: 实现最小作用域**

`OperationScope` 必须拥有链接取消源和截止时间；释放时取消并等待注册清理；`DiagnosticOperation` 只暴露不可变状态和稳定结果，不暴露底层 `CancellationTokenSource`。

- [ ] **Step 3: 验证并发边界**

```powershell
dotnet test .\tests\DotnetAnalysis.Orchestration.Tests\DotnetAnalysis.Orchestration.Tests.csproj --configuration Debug --filter "FullyQualifiedName~OperationScopeTests"
```

Expected: 并发完成、取消、超时和旧代次结果测试全部通过。

- [ ] **Step 4: Commit**

```powershell
git add src/DotnetAnalysis.Orchestration/Operations src/DotnetAnalysis.Application/Events tests/DotnetAnalysis.Orchestration.Tests/OperationScopeTests.cs
git commit -m "feat: isolate orchestration operations by generation"
```

---

## Task 4: 实现目标查找、能力探测和 EXE 启动边界

**Files:**
- Create: `src/DotnetAnalysis.Orchestration/TargetProcessFinder.cs`
- Create: `src/DotnetAnalysis.Orchestration/TargetCapabilityProbe.cs`
- Create: `src/DotnetAnalysis.Application/Contracts/Diagnostics/ITargetProcessLauncher.cs`
- Create: `src/DotnetAnalysis.Application/Contracts/Diagnostics/TargetProcessLaunchRequest.cs`
- Create: `src/DotnetAnalysis.Application/Contracts/Diagnostics/TargetProcessLaunchResult.cs`
- Modify: `src/DotnetAnalysis.Diagnostics/Windows/WindowsProcessDiagnostics.cs` or the existing Windows process boundary to implement the launch adapter only if that is the selected composition location.
- Test: `tests/DotnetAnalysis.Orchestration.Tests/TargetProcessFinderTests.cs`
- Test: `tests/DotnetAnalysis.Orchestration.Tests/TargetCapabilityProbeTests.cs`
- Test: `tests/DotnetAnalysis.Orchestration.Tests/Fakes/FakeTargetProcessLauncher.cs`

**Interfaces:**
- Consumes: existing `IProcessDiagnostics.GetProcessesAsync`, target identity and capability evidence; launch adapter for EXE start.
- Produces: filtered unique candidates, `TargetContext`, capability summary, launch result and stable failure stages.

- [ ] **Step 1: 写独立接口测试**

覆盖进程去重、筛选、排序、退出候选、权限字段为空、PID 复用、运行时不支持、Profiler 能力不可用、启动参数校验和启动等待超时。

- [ ] **Step 2: 实现查找和探测**

查找阶段只执行快速候选读取；能力探测对选中目标执行深度校验；附着前必须再次校验 PID 与启动时间。任何探测结果只能作为提示，不能替代附着时验证。

- [ ] **Step 3: 实现启动契约适配**

支持 EXE 路径、参数、工作目录和显式目标处理策略；默认不终止启动失败的目标进程。启动结果必须包含 PID、启动时间和身份验证状态。

- [ ] **Step 4: 运行测试**

```powershell
dotnet test .\tests\DotnetAnalysis.Orchestration.Tests\DotnetAnalysis.Orchestration.Tests.csproj --configuration Debug --filter "FullyQualifiedName~TargetProcess"
```

Expected: 查找、能力和启动边界独立测试通过；不存在对 WPF 或 Diagnostics 内部类型的引用。

- [ ] **Step 5: Commit**

```powershell
git add src/DotnetAnalysis.Orchestration src/DotnetAnalysis.Application/Contracts/Diagnostics src/DotnetAnalysis.Diagnostics tests/DotnetAnalysis.Orchestration.Tests
git commit -m "feat: orchestrate target discovery capability and launch"
```

---

## Task 5: 实现活动分析会话

**Files:**
- Create: `src/DotnetAnalysis.Orchestration/IAnalysisSession.cs`
- Create: `src/DotnetAnalysis.Orchestration/AnalysisSession.cs`
- Create: `src/DotnetAnalysis.Orchestration/MemoryTimeline.cs`
- Create: `src/DotnetAnalysis.Orchestration/SessionStateMachine.cs`
- Test: `tests/DotnetAnalysis.Orchestration.Tests/AnalysisSessionTests.cs`
- Test: `tests/DotnetAnalysis.Orchestration.Tests/Fakes/FakeProcessDiagnostics.cs`

**Interfaces:**
- Consumes: `IProcessDiagnostics.AttachAsync`, `IProcessDiagnosticsSession`, `OperationScope`, `TargetContext`, `IEventBus`.
- Produces: `IAnalysisSession` with `ReadMemoryTimelineAsync`, standard/Retention `CaptureAsync`, execution profile query, `StopAsync`, `DisposeAsync` and capability/quality state.

- [ ] **Step 1: 写会话失败测试**

覆盖附着成功状态序列、样本质量、捕获模式转发、Retention 禁止降级、目标退出、PID 变化、取消、停止、重复释放和后台任务收尾。

- [ ] **Step 2: 实现会话所有权**

会话独占底层诊断会话和会话级取消源；宿主只能获得 `IAnalysisSession`；停止时等待采样、捕获和查询相关后台工作进入终态。

- [ ] **Step 3: 实现时间线和执行采样入口**

时间线有界并保留缺失/中断质量；执行采样查询验证时间区间属于当前会话可查询范围；查询取消只影响当前调用。

- [ ] **Step 4: 运行测试**

```powershell
dotnet test .\tests\DotnetAnalysis.Orchestration.Tests\DotnetAnalysis.Orchestration.Tests.csproj --configuration Debug --filter "FullyQualifiedName~AnalysisSessionTests"
```

Expected: 所有会话单元测试通过，且无未观察后台任务。

- [ ] **Step 5: Commit**

```powershell
git add src/DotnetAnalysis.Orchestration tests/DotnetAnalysis.Orchestration.Tests
git commit -m "feat: add owned orchestration analysis sessions"
```

---

## Task 6: 实现多快照集合和快照分析

**Files:**
- Create: `src/DotnetAnalysis.Orchestration/ISnapshotCollection.cs`
- Create: `src/DotnetAnalysis.Orchestration/ISnapshotAnalysis.cs`
- Create: `src/DotnetAnalysis.Orchestration/SnapshotCollection.cs`
- Create: `src/DotnetAnalysis.Orchestration/SnapshotAnalysis.cs`
- Create: `src/DotnetAnalysis.Orchestration/SnapshotComparison.cs`
- Test: `tests/DotnetAnalysis.Orchestration.Tests/SnapshotCollectionTests.cs`
- Test: `tests/DotnetAnalysis.Orchestration.Tests/SnapshotAnalysisTests.cs`

**Interfaces:**
- Consumes: `IMemorySnapshotAnalysisService`, `MemorySnapshot`, `MemoryObjectPage`, `MemoryReferencePath`, `MemoryRetentionPathResult`, `MemorySnapshotComparison`。
- Produces: 多快照集合、当前/基线/候选选择、单快照分析接口和独立比较结果。

- [ ] **Step 1: 写快照单元测试**

覆盖三次快照顺序、导入不修改源文件、当前快照切换、基线/候选选择、100,000 对象分页门禁、基础/派生分析隔离、查询取消和关闭。

- [ ] **Step 2: 实现快照集合所有权**

快照只有在 Diagnostics 确认正式文件持久化后进入集合；捕获取消或失败不加入成功集合；集合关闭时释放分析句柄但不删除正式快照。

- [ ] **Step 3: 实现单快照分析转发**

类型、对象分页、引用路径、Retention 路径、GC Root、支配树和比较分别传播质量与错误码；基础查询不能因派生失败而失效。

- [ ] **Step 4: 运行测试**

```powershell
dotnet test .\tests\DotnetAnalysis.Orchestration.Tests\DotnetAnalysis.Orchestration.Tests.csproj --configuration Debug --filter "FullyQualifiedName~Snapshot"
```

Expected: 多快照、分页、质量和派生隔离测试全部通过。

- [ ] **Step 5: Commit**

```powershell
git add src/DotnetAnalysis.Orchestration tests/DotnetAnalysis.Orchestration.Tests
 git commit -m "feat: add orchestration snapshot collection and analysis"
```

---

## Task 7: 实现 `DiagnosticsApplication`、注册和 Desktop 组合根接入

**Files:**
- Create: `src/DotnetAnalysis.Orchestration/IDiagnosticsApplication.cs`
- Create: `src/DotnetAnalysis.Orchestration/DiagnosticsApplication.cs`
- Create: `src/DotnetAnalysis.Orchestration/DependencyInjection/OrchestrationServiceCollectionExtensions.cs`
- Modify: `src/DotnetAnalysis.Desktop/Composition/DesktopServiceCollectionExtensions.cs`
- Modify: `src/DotnetAnalysis.Desktop/App.xaml.cs`
- Test: `tests/DotnetAnalysis.Orchestration.Tests/DiagnosticsApplicationTests.cs`
- Modify: `tests/DotnetAnalysis.Tests/Desktop/CompositionTests.cs`

**Interfaces:**
- Consumes: `TargetProcessFinder`、`TargetCapabilityProbe`、`TargetProcessStarter`、`IAnalysisSession`、`SnapshotCollection`、`IEventBus`。
- Produces: `IDiagnosticsApplication`，保证同一上下文最多一个活动会话、正确替换 generation、关闭幂等和状态发布。

- [ ] **Step 1: 写应用上下文测试**

覆盖附着替换、启动替换、快照打开、关闭、旧结果隔离、同一时刻冲突操作和注册后依赖图。

- [ ] **Step 2: 实现应用上下文**

应用上下文拥有当前 generation、活动会话和快照集合；替换前停止旧会话并等待收尾；旧事件只能被识别但不能更新当前状态。

- [ ] **Step 3: 接入组合根**

Desktop 只调用 `AddOrchestration()`；ViewModel 不新增 Diagnostics 直接依赖；现有 `IEventBus` 保持单例并由编排层复用。

- [ ] **Step 4: 运行组合和架构测试**

```powershell
dotnet test .\tests\DotnetAnalysis.Orchestration.Tests\DotnetAnalysis.Orchestration.Tests.csproj --configuration Debug
dotnet test .\tests\DotnetAnalysis.Tests\DotnetAnalysis.Tests.csproj --configuration Debug --no-build --filter "FullyQualifiedName~Composition|FullyQualifiedName~Architecture"
```

Expected: 编排测试和依赖边界测试通过；Desktop 可构建。

- [ ] **Step 5: Commit**

```powershell
git add src/DotnetAnalysis.Orchestration src/DotnetAnalysis.Desktop tests/DotnetAnalysis.Tests DotnetAnalysis.sln
git commit -m "feat: compose diagnostics application orchestration"
```

---

## Task 8: 编写真实业务流程集成测试

**Files:**
- Create: `tests/DotnetAnalysis.Diagnostics.IntegrationTests/OrchestrationWorkflowIntegrationTests.cs`
- Create: `tests/DotnetAnalysis.Diagnostics.IntegrationTests/OrchestrationFailureWorkflowTests.cs`
- Modify: `tests/DotnetAnalysis.Diagnostics.IntegrationTests/IntegrationTestHost.cs` only to expose a reusable orchestrated fixture.

**Interfaces:**
- Consumes: 已注册的真实 Diagnostics、Orchestration 和 .NET 8/9/10 TestTarget。
- Produces: 真实业务流程通过/失败证据；不修改生产实现。

- [ ] **Step 1: 写真实附着流程**

每个 TFM 执行：发现目标 → 能力探测 → 附着 → 样本 → 三次标准快照 → 类型/分页/路径 → 快照比较 → 停止 → 重开快照。

- [ ] **Step 2: 写真实启动流程**

验证 EXE 路径、参数、工作目录、等待总超时、身份校验和附着失败策略。

- [ ] **Step 3: 写真实失败流程**

验证目标退出、分析中退出、PID 复用、权限不足、Profiler 冲突/缺失、不可写目录、取消和旧操作延迟完成。

- [ ] **Step 4: 串行运行集成门禁**

```powershell
dotnet test .\tests\DotnetAnalysis.Diagnostics.IntegrationTests\DotnetAnalysis.Diagnostics.IntegrationTests.csproj --configuration Release --filter "TestCategory=WindowsDiagnosticsIntegration&FullyQualifiedName~Orchestration" --logger "trx;LogFileName=orchestration-integration.trx"
```

Expected: 三个目标 TFM 的真实流程和错误码断言全部通过，测试结束无残留目标、会话目录或后台任务。

- [ ] **Step 5: Commit**

```powershell
git add tests/DotnetAnalysis.Diagnostics.IntegrationTests
git commit -m "test: cover orchestration business workflows"
```

---

## Task 9: 编写性能、压力和资源验收

**Files:**
- Create: `tests/DotnetAnalysis.Diagnostics.IntegrationTests/OrchestrationPerformanceTests.cs`
- Create: `tests/DotnetAnalysis.Diagnostics.IntegrationTests/OrchestrationStressTests.cs`
- Create: `eng/Run-OrchestrationAcceptance.ps1`
- Create: `Run-OrchestrationAcceptance.bat`
- Modify: `eng/Run-ReleaseAcceptance.ps1`

**Interfaces:**
- Consumes: 真实编排业务流程、已有大快照生成器、执行采样测试基础设施和显式环境变量门禁。
- Produces: `TestResults/OrchestrationAcceptance-*` 下 metadata、原始数组、TRX、summary 和资源趋势。

- [ ] **Step 1: 写性能流程测试**

按真实流程测量 .NET 目标附着、三次快照、首次/缓存索引、类型、首/中/尾页、空页、非法 offset、最大页、路径和比较；覆盖 100k/1m/5m/10m 对象规模。

- [ ] **Step 2: 写压力流程测试**

实现 20 轮打开/附着→采样→快照→索引→查询→释放；增加 60 分钟执行采样基准、2 小时 Soak、并发分页/路径/取消、周期快照和故障交错。超长门禁必须只有显式环境变量开启才执行。

- [ ] **Step 3: 写证据收集器**

脚本和测试共同记录提交号、包版本、OS/CPU、权限、TFM、目标 PID/启动时间、耗时、对象数、文件大小、内存峰值、样本/丢失数、P50/P95/P99、错误码和资源清理结果。

- [ ] **Step 4: 串行运行性能和压力门禁**

```powershell
$env:DOTNET_ANALYSIS_RUN_ORCHESTRATION_PERFORMANCE = 'true'
$env:DOTNET_ANALYSIS_RUN_ORCHESTRATION_STRESS = 'true'
.\eng\Run-OrchestrationAcceptance.ps1 -Configuration Release -EnableLongRunning
```

Expected: 四层结果写入单一时间戳目录；任何测试失败、资源残留、旧代次污染或质量误报导致脚本非零退出。

- [ ] **Step 5: Commit**

```powershell
git add tests/DotnetAnalysis.Diagnostics.IntegrationTests eng/Run-OrchestrationAcceptance.ps1 Run-OrchestrationAcceptance.bat eng/Run-ReleaseAcceptance.ps1
git commit -m "test: add orchestration performance and stress gates"
```

---

## Task 10: 全量验证、文档和发布门禁接入

**Files:**
- Modify: `docs/superpowers/specs/2026-09-15-diagnostics-orchestration-design.md` only if implementation decisions require traceable clarification.
- Modify: `docs/superpowers/plans/2026-09-15-diagnostics-orchestration.md` to record completed tasks only if execution workflow requires it.
- Modify: `eng/Run-ReleaseAcceptance.ps1` for final orchestration gate ordering.
- Modify: `eng/release/README.md` with orchestration acceptance commands and evidence paths.

**Interfaces:**
- Consumes: 全部源代码、四层测试项目、发布验收脚本。
- Produces: Release x64 可构建、可重复测试和完整验收证据。

- [ ] **Step 1: 运行单元门禁**

```powershell
dotnet test .\tests\DotnetAnalysis.Orchestration.Tests\DotnetAnalysis.Orchestration.Tests.csproj --configuration Release
```

Expected: 所有独立接口/规则测试通过。

- [ ] **Step 2: 运行现有回归门禁**

```powershell
dotnet build .\DotnetAnalysis.sln --configuration Release --property:Platform=x64
dotnet test .\tests\DotnetAnalysis.Tests\DotnetAnalysis.Tests.csproj --configuration Release --no-build
```

Expected: 现有 Core/Application/Diagnostics/Desktop 测试无回归。

- [ ] **Step 3: 运行真实集成门禁**

```powershell
dotnet test .\tests\DotnetAnalysis.Diagnostics.IntegrationTests\DotnetAnalysis.Diagnostics.IntegrationTests.csproj --configuration Release --filter "TestCategory=WindowsDiagnosticsIntegration&FullyQualifiedName~Orchestration" --logger "trx;LogFileName=orchestration-final.trx"
```

Expected: .NET 8/9/10 业务流程通过，失败场景错误码和资源收尾通过。

- [ ] **Step 4: 运行显式性能/压力门禁**

```powershell
$env:DOTNET_ANALYSIS_RUN_ORCHESTRATION_PERFORMANCE = 'true'
$env:DOTNET_ANALYSIS_RUN_ORCHESTRATION_STRESS = 'true'
.\Run-OrchestrationAcceptance.bat --configuration Release --enable-long-running
```

Expected: 单一证据目录生成完整 metadata、原始测量和 summary；脚本以失败测试或残留资源退出非零。

- [ ] **Step 5: 检查包和差异**

```powershell
git diff --check
git status --short
```

Expected: 无格式错误；发布前按用户决定提交，不自动推送。

- [ ] **Step 6: Commit**

```powershell
git add docs/superpowers/plans/2026-09-15-diagnostics-orchestration.md eng/release/README.md eng/Run-ReleaseAcceptance.ps1
git commit -m "docs: finalize orchestration implementation and acceptance plan"
```

---

## Self-Review Checklist

- [ ] 规格 §6 的目标、能力、会话、时间线、快照、分析、关闭流程均有对应任务。
- [ ] 规格 §10 的状态机由 Task 2、Task 3、Task 5、Task 7 覆盖。
- [ ] 规格 §11 的目标退出、PID 复用、Profiler、磁盘、取消和派生失败由 Task 4、Task 5、Task 6、Task 8 覆盖。
- [ ] 规格 §12–§15 的性能、稳定性、日志、监控和配置由 Task 3、Task 9、Task 10 覆盖。
- [ ] 规格 §16–§17 的权限、Windows x64、.NET 8/9/10 和不支持项由 Task 4、Task 8 覆盖。
- [ ] 规格 §19–§21 的四层测试、证据和发布门禁由 Task 8、Task 9、Task 10 覆盖。
- [ ] 单元测试没有被写成端到端测试；集成、性能、压力测试明确按业务流程执行。
- [ ] 未使用未定义的函数名、项目名或接口名；如实现中发现现有契约等价能力，必须复用而不是新增重复接口。
- [ ] 计划没有用“以后补”“适当处理”“保证性能”等空泛步骤替代具体操作和验证。
- [ ] 每个任务包含文件范围、输入/输出边界、测试命令和独立提交点。