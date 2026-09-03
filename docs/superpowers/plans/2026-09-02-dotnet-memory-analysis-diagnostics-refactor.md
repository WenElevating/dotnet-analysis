# .NET 内存分析诊断层重构实施计划

> **供执行型智能体使用：** 必须使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 按任务逐项实施。本计划以 - [ ] 复选框跟踪执行进度。

**目标：** 用“附着进程后持续采样、可多次保存 .gcdump、分析可重试”的诊断工作流，替换现有“开始录制、结束后只采集一次”的分析流程。

**架构：** Core 只保存不可变诊断值对象和显式状态转换规则；Application 持有附着会话及单次快照操作的状态，并通过既有事件总线发布轻量事实；Diagnostics 使用 Windows、EventPipe、TraceEvent 和内部快照存储实现稳定契约；Desktop 仅在组合根注册 Diagnostics，ViewModel 只消费 Application 契约。

**技术栈：** 使用当前机器可用的 .NET 10 SDK（允许同一主版本内的补丁/特性版本，例如 10.0.400），WPF、MSTest、Microsoft.Extensions.DependencyInjection、Microsoft.Extensions.Logging、Microsoft.Diagnostics.NETCore.Client 0.2.661903、Microsoft.Diagnostics.Tracing.TraceEvent 3.2.6、Windows x64 EventPipe 与 .gcdump；.NET 9 目标保留为预留兼容矩阵，不作为本机 .NET 10 开发的阻塞条件。

## 当前执行状态（2026-09-03）

- 任务 1–7：实现、聚焦测试和集成所需代码已完成。任务 1 已由 `0669e1f` 提交，任务 2–3 的基础实现已由 `9f01e0a` 提交，任务 4–8 收尾变更已由 `6c2af50`、`fdb495f` 和后续中文历史提交归档。
- 任务 8：使用本机 .NET SDK 10.0.400 完成构建、全量测试、真实 net8/net9/net10 EventPipe/.gcdump 集成测试、格式检查、前置脚本、依赖与层级边界检查。当前机器已安装 .NET 8.0.30、9.0.19 与 10.0.11 runtime，三运行时受控矩阵均可执行。
- 已完成：Windows 任务管理器等效内存交叉核对、全部自动化门禁、计划复选项和 Lore 提交归档。所有计划任务均已完成。
- 最近验证结果：`dotnet build` 0 警告/0 错误；`dotnet test DotnetAnalysis.sln --no-restore` 主测试 51 通过、集成测试 11 通过且 0 跳过；三运行时 net8/net9/net10 真实 EventPipe/.gcdump 集成测试全部通过；`dotnet format --verify-no-changes` 通过；三 runtime 前置脚本通过；`git diff --check` 无输出。

## 全局约束

- 仅支持 Windows 10 22H2（含 2023-09 累积更新）或 Windows 11 22H2 及更高版本上的本机 x64 .NET 8、.NET 9、.NET 10 CoreCLR；任务 8 保留三运行时矩阵，当前开发机以 .NET 10 为主并将缺失的 .NET 9 标记为 reserved/optional。
- 依赖方向固定为 Desktop -> Application -> Core，以及 Diagnostics -> Application/Core。Core 只依赖 BCL；Application 只依赖 Core、BCL 和日志抽象；Desktop 的 ViewModel 不得引用 Diagnostics；只有 App.xaml.cs 可以导入并注册 Diagnostics。
- Diagnostics 项目只新增 Microsoft.Diagnostics.NETCore.Client 0.2.661903 与 Microsoft.Diagnostics.Tracing.TraceEvent 3.2.6；首版不得引用 ClrMD，不得启动 dotnet-gcdump 或 dotnet-trace 子进程。
- EventPipe 读取、快照写入、堆图分析都不能运行在 WPF UI 线程。慢订阅者不得反压 ProcessMemorySampler、AllocationSampleCollector 或捕获流程；每个订阅保持独立的有界队列，时间线使用 LatestOnly。
- 同一附着会话同一时刻只允许一个 Capturing 快照；并发请求必须立即拒绝，不排队。只有文件完整保存后的 CapturedAtUtc 才能封存分配区间。
- 快照可见性的顺序固定为：临时文件写入 -> 可读性验证 -> 原子提升为正式文件 -> 保存区间分配数据 -> 发布已保存事件。失败、取消、重复清理只能删除临时数据，并且必须幂等。
- 每次附着前与每次截取前都验证 TargetProcess.ProcessId 和 TargetProcess.StartedAtUtc；PID 存在但启动时间变化必须返回 TargetChanged。
- 附着会话状态为 Attaching -> Monitoring -> Ending -> Ended 或 Failed；快照状态为 Pending -> Capturing -> Analyzing -> Ready、Failed 或 Canceled。上层只处理 DiagnosticsException 的稳定错误码。
- 生命周期事件必须有序投递；高频时间线和分配采样状态使用 LatestOnly。事件只能携带标识、时间、状态、轻量摘要与错误码，不能携带堆图、对象列表、引用链或调用树。
- 原始底层异常只能作为 DiagnosticsException.InnerException 和结构化日志保留。事件处理器故障发布一次 ModuleFaulted；处理 ModuleFaulted 自身失败不得递归发布。
- MemoryUsageSample 的 Unavailable 状态必须让两个字节字段均为 null，不得用 0 伪造读数。进程内存的内部口径为私有工作集，但 Application 与 Desktop 不暴露 Windows API 或性能计数器名称。
- AllocationProfile 只表达采样观测到的类型级热点。ObservedAllocatedBytes 不是精确总分配量；数据质量只能是 Continuous、Interrupted 或 NotAvailable。导入 .gcdump 的数据质量固定为 NotAvailable。
- 不得跨操作长期持有完整堆图或完整原始 EventPipe 事件列表。读取时流式聚合，对象与引用链按需读取，UI 只缓存摘要行和已展开内容。
- 已成功保存的快照必须在会话结束、取消、分析失败和应用重启后仍可打开。分析重试只能读取已保存快照及其分配数据，绝不能重新连接目标进程或重新捕获。
- 保持 Directory.Build.props 的全部门禁：Nullable、最新语言版本、最新推荐分析器、代码样式构建检查、警告即错误。每个任务先运行聚焦测试，任务 8 运行全量门禁。

---

## 文件结构

| 路径 | 单一职责 |
| --- | --- |
| src/DotnetAnalysis.Core/Diagnostics/* | 诊断 ID、不可变模型、错误值与两个状态机转换规则。 |
| src/DotnetAnalysis.Application/Contracts/Diagnostics/* | 上层稳定诊断入口、附着会话、分析数据源与错误契约。 |
| src/DotnetAnalysis.Application/Sessions/AttachedProcessSession.cs | 一个附着会话的状态、时间线订阅、捕获准入、释放流程。 |
| src/DotnetAnalysis.Application/Snapshots/MemorySnapshotOperation.cs | 一次快照的捕获后分析、失败重试与按需查询。 |
| src/DotnetAnalysis.Application/Events/* | 通用事件投递策略及诊断生命周期事件。 |
| src/DotnetAnalysis.Diagnostics/Windows/* | Windows 进程发现、身份验证、运行时检查、采样、EventPipe、存储和 .gcdump 读取。 |
| src/DotnetAnalysis.Diagnostics/DependencyInjection/* | Diagnostics 的依赖注入注册，不向 ViewModel 泄漏实现类型。 |
| src/DotnetAnalysis.Desktop/App.xaml.cs | Desktop 唯一可导入 Diagnostics 的组合根。 |
| src/DotnetAnalysis.Desktop/Composition/* | Application、事件总线、WPF 基础设施与 ViewModel 注册。 |
| src/DotnetAnalysis.Desktop/ViewModels/ShellViewModel.cs | 只订阅 Application 事件并切换至 UI 线程。 |
| tests/DotnetAnalysis.Tests/Core/Diagnostics/* | 模型、状态、错误码、数据质量测试。 |
| tests/DotnetAnalysis.Tests/Application/* | 附着会话、快照操作、事件总线契约测试。 |
| tests/DotnetAnalysis.Tests/Diagnostics/* | 存储、区间、读取器与不依赖真实进程的适配器测试。 |
| tests/DotnetAnalysis.Diagnostics.TestTarget/* | 同时生成 net8.0、net9.0、net10.0 的受控目标进程。 |
| tests/DotnetAnalysis.Diagnostics.IntegrationTests/* | 显式启用的 Windows x64 集成矩阵。 |
| eng/Verify-DiagnosticsIntegrationPrerequisites.ps1 | SDK、操作系统、体系结构和三套运行时的前置检查。 |

## 关键落地决定

- 所有无 Windows 细节的诊断模型放在 Core.Diagnostics。Application 契约只使用这些模型，永远不传递进程句柄、EventPipe Session、文件路径或 TraceEvent 类型。
- MemorySnapshotId 是跨 Application 与 Diagnostics 边界的唯一快照定位符。Diagnostics 在内部维护 ID 到存储位置的映射，因此上层对象没有路径字段。
- MemorySnapshotAnalysis 只保存快照、类型摘要与 AllocationProfile。对象列表和引用链由 IMemorySnapshotAnalysisService 按需读取，避免 Application 常驻完整堆图。
- AttachedProcessSession 管理附着生命周期；MemorySnapshotOperation 管理单次快照生命周期。两者是状态来源，事件总线只能观察，不能恢复状态。
- 高频事件通过 IApplicationEventDeliveryPolicy 声明 LatestOnly 与 DeliveryKey；删除 EventSubscriptionOptions.CoalesceProgressEvents 及 CaptureProgressChanged 专用合并逻辑。
- 集成测试使用 WindowsDiagnosticsIntegration 分类。当前本机验证要求可用的 .NET 10 SDK 与 .NET 10/.NET 8 运行时；.NET 9 目标和运行时探测保留为 optional/reserved，缺少 .NET 9 时必须明确报告但不得阻塞本机 .NET 10 开发验证。

## 任务 1：以诊断领域模型替换旧的一次性会话模型

**文件：**

- 新建：src/DotnetAnalysis.Core/Diagnostics/ProcessDiagnosticsSessionId.cs
- 新建：src/DotnetAnalysis.Core/Diagnostics/MemorySnapshotId.cs
- 新建：src/DotnetAnalysis.Core/Diagnostics/TargetProcess.cs
- 新建：src/DotnetAnalysis.Core/Diagnostics/MemoryUsageSample.cs
- 新建：src/DotnetAnalysis.Core/Diagnostics/MemoryUsageSampleState.cs
- 新建：src/DotnetAnalysis.Core/Diagnostics/TypeIdentity.cs
- 新建：src/DotnetAnalysis.Core/Diagnostics/CallStackFrame.cs
- 新建：src/DotnetAnalysis.Core/Diagnostics/AllocationHotspot.cs
- 新建：src/DotnetAnalysis.Core/Diagnostics/AllocationProfile.cs
- 新建：src/DotnetAnalysis.Core/Diagnostics/AllocationProfileDataQuality.cs
- 新建：src/DotnetAnalysis.Core/Diagnostics/MemorySnapshot.cs
- 新建：src/DotnetAnalysis.Core/Diagnostics/MemorySnapshotOrigin.cs
- 新建：src/DotnetAnalysis.Core/Diagnostics/MemorySnapshotState.cs
- 新建：src/DotnetAnalysis.Core/Diagnostics/MemorySnapshotTransitionRules.cs
- 新建：src/DotnetAnalysis.Core/Diagnostics/ProcessDiagnosticsSessionState.cs
- 新建：src/DotnetAnalysis.Core/Diagnostics/ProcessDiagnosticsSessionTransitionRules.cs
- 新建：src/DotnetAnalysis.Core/Diagnostics/MemorySnapshotAnalysis.cs
- 新建：src/DotnetAnalysis.Core/Diagnostics/MemoryTypeSummary.cs
- 新建：src/DotnetAnalysis.Core/Diagnostics/MemoryObjectInfo.cs
- 新建：src/DotnetAnalysis.Core/Diagnostics/MemoryReferencePath.cs
- 删除：src/DotnetAnalysis.Core/Sessions/AnalysisSession.cs
- 删除：src/DotnetAnalysis.Core/Sessions/AnalysisSessionState.cs
- 删除：src/DotnetAnalysis.Core/Sessions/AnalysisSessionTransitionRules.cs
- 删除：src/DotnetAnalysis.Core/Sessions/SessionId.cs
- 新建：tests/DotnetAnalysis.Tests/Core/Diagnostics/DiagnosticsStateMachineTests.cs
- 新建：tests/DotnetAnalysis.Tests/Core/Diagnostics/DiagnosticsValueTests.cs
- 删除：tests/DotnetAnalysis.Tests/Core/AnalysisSessionTests.cs

**输入与输出：**

- 不依赖 Application 或 Diagnostics。
- 向后续任务提供 ProcessDiagnosticsSessionId、MemorySnapshotId、TargetProcess、MemoryUsageSample、AllocationProfile、MemorySnapshot、MemorySnapshotAnalysis 及两个转换规则类。
- 固定公开形状如下：

~~~csharp
public static bool CanMove(
    ProcessDiagnosticsSessionState from,
    ProcessDiagnosticsSessionState to);

public static bool CanMove(
    MemorySnapshotState from,
    MemorySnapshotState to);

public sealed record MemorySnapshot(
    MemorySnapshotId Id,
    MemorySnapshotOrigin Origin,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? CaptureStartedAtUtc,
    DateTimeOffset? CapturedAtUtc,
    MemorySnapshotState State);

public sealed record MemorySnapshotAnalysis(
    MemorySnapshot Snapshot,
    IReadOnlyList<MemoryTypeSummary> Types,
    AllocationProfile AllocationProfile);
~~~

- [x] **步骤 1：编写失败的状态与值对象测试**

~~~csharp
[TestMethod]
public void SnapshotRules_AllowCaptureToAnalyzeAndBecomeReady()
{
    Assert.IsTrue(MemorySnapshotTransitionRules.CanMove(
        MemorySnapshotState.Capturing,
        MemorySnapshotState.Analyzing));
    Assert.IsTrue(MemorySnapshotTransitionRules.CanMove(
        MemorySnapshotState.Analyzing,
        MemorySnapshotState.Ready));
    Assert.IsFalse(MemorySnapshotTransitionRules.CanMove(
        MemorySnapshotState.Failed,
        MemorySnapshotState.Capturing));
}

[TestMethod]
public void UnavailableSample_UsesNullInsteadOfZero()
{
    var sample = new MemoryUsageSample(
        DateTimeOffset.UtcNow,
        null,
        null,
        MemoryUsageSampleState.Unavailable);

    Assert.IsNull(sample.ManagedHeapBytes);
    Assert.IsNull(sample.ProcessMemoryBytes);
}

[TestMethod]
public void NotAvailableProfile_HasNoHotspots()
{
    var profile = AllocationProfile.NotAvailable(
        DateTimeOffset.Parse("2026-09-02T00:00:00Z"),
        DateTimeOffset.Parse("2026-09-02T00:01:00Z"));

    Assert.AreEqual(AllocationProfileDataQuality.NotAvailable, profile.DataQuality);
    Assert.IsEmpty(profile.Hotspots);
}
~~~

- [x] **步骤 2：确认测试在模型尚未创建时失败**

运行：dotnet test tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj --filter FullyQualifiedName~Core.Diagnostics --no-restore

预期：因缺少 Core.Diagnostics 类型而失败。

- [x] **步骤 3：实现模型与状态迁移规则**

~~~csharp
public enum ProcessDiagnosticsSessionState
{
    Attaching,
    Monitoring,
    Ending,
    Ended,
    Failed
}

public enum MemorySnapshotState
{
    Pending,
    Capturing,
    Analyzing,
    Ready,
    Failed,
    Canceled
}

public static class MemorySnapshotTransitionRules
{
    public static bool CanMove(MemorySnapshotState from, MemorySnapshotState to) =>
        from switch
        {
            MemorySnapshotState.Pending => to is MemorySnapshotState.Capturing
                or MemorySnapshotState.Canceled
                or MemorySnapshotState.Failed,
            MemorySnapshotState.Capturing => to is MemorySnapshotState.Analyzing
                or MemorySnapshotState.Canceled
                or MemorySnapshotState.Failed,
            MemorySnapshotState.Analyzing => to is MemorySnapshotState.Ready
                or MemorySnapshotState.Canceled
                or MemorySnapshotState.Failed,
            _ => false
        };
}
~~~

实现构造验证：字节数不能为负；Measured 的两个字节字段都必须有值；Unavailable 与 SessionEnded 的两个字节字段都必须为 null；类型名与调用帧名称不能为空。MemorySnapshot 的状态仅能通过内部方法变更，且每次必须调用转换规则。

- [x] **步骤 4：删除旧会话模型并修复编译引用**

删除旧 AnalysisSession、SessionId 及旧状态机。不得保留兼容别名，因为旧模型的语义与新工作流不兼容。

- [x] **步骤 5：运行 Core 测试**

运行：dotnet test tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj --filter FullyQualifiedName~Core.Diagnostics --no-restore

预期：通过状态迁移、非法输入、Unavailable 空值和 NotAvailable 数据质量测试。

- [x] **步骤 6：提交**（已由 `0669e1f` 完成）

~~~bash
git add src/DotnetAnalysis.Core tests/DotnetAnalysis.Tests/Core
git commit -m "Replace one-shot session domain with diagnostics state"
~~~

提交正文必须包含 Lore 的 Constraint、Rejected、Confidence、Scope-risk、Directive、Tested、Not-tested trailers。

## 任务 2：建立 Application 稳定契约与两个状态所有者

**文件：**

- 新建：src/DotnetAnalysis.Application/Contracts/Diagnostics/IProcessDiagnostics.cs
- 新建：src/DotnetAnalysis.Application/Contracts/Diagnostics/IProcessDiagnosticsSession.cs
- 新建：src/DotnetAnalysis.Application/Contracts/Diagnostics/IMemorySnapshotAnalysisService.cs
- 新建：src/DotnetAnalysis.Application/Contracts/Diagnostics/DiagnosticsErrorCode.cs
- 新建：src/DotnetAnalysis.Application/Contracts/Diagnostics/DiagnosticsException.cs
- 新建：src/DotnetAnalysis.Application/Sessions/AttachedProcessSession.cs
- 新建：src/DotnetAnalysis.Application/Snapshots/MemorySnapshotOperation.cs
- 新建：src/DotnetAnalysis.Application/Snapshots/MemorySnapshotAnalysisService.cs
- 删除：src/DotnetAnalysis.Application/Contracts/ICaptureBackend.cs
- 删除：src/DotnetAnalysis.Application/Contracts/IAnalysisService.cs
- 删除：src/DotnetAnalysis.Application/Sessions/AnalysisSessionCoordinator.cs
- 新建：tests/DotnetAnalysis.Tests/Application/AttachedProcessSessionTests.cs
- 新建：tests/DotnetAnalysis.Tests/Application/MemorySnapshotOperationTests.cs
- 删除：tests/DotnetAnalysis.Tests/Application/AnalysisSessionCoordinatorTests.cs

**输入与输出：**

- 输入：任务 1 的 Core.Diagnostics 模型，以及任务 3 的 IEventBus。
- 输出：Desktop 和 Application 唯一可见的诊断契约：

~~~csharp
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

public interface IMemorySnapshotAnalysisService
{
    Task<MemorySnapshotAnalysis> AnalyzeAsync(
        MemorySnapshot snapshot,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MemoryObjectInfo>> GetObjectsAsync(
        MemorySnapshot snapshot,
        TypeIdentity type,
        CancellationToken cancellationToken);

    Task<MemoryReferencePath?> GetReferencePathAsync(
        MemorySnapshot snapshot,
        ulong objectAddress,
        CancellationToken cancellationToken);
}
~~~

- 输出：AttachedProcessSession 提供 Process、State、GetMemoryUsageAsync、CaptureSnapshotAsync、EndAsync、DisposeAsync；MemorySnapshotOperation 提供 Snapshot、State、AnalyzeAsync、RetryAnalysisAsync、GetObjectsAsync、GetReferencePathAsync。

- [x] **步骤 1：编写附着与快照操作的失败测试**

~~~csharp
[TestMethod]
public async Task CaptureSnapshotAsync_RejectsConcurrentCapture()
{
    var captureGate = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var session = CreateAttachedSession(captureGate);

    var firstCapture = session.CaptureSnapshotAsync(CancellationToken.None);
    await _captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

    await Assert.ThrowsExceptionAsync<InvalidOperationException>(
        () => session.CaptureSnapshotAsync(CancellationToken.None));

    captureGate.TrySetResult();
    await firstCapture;
}

[TestMethod]
public async Task RetryAnalysisAsync_DoesNotCaptureAgain()
{
    var operation = CreateSavedOperation(analysisFailuresBeforeSuccess: 1);

    await Assert.ThrowsExceptionAsync<DiagnosticsException>(
        () => operation.AnalyzeAsync(CancellationToken.None));
    var analysis = await operation.RetryAnalysisAsync(CancellationToken.None);

    Assert.AreEqual(MemorySnapshotState.Ready, operation.State);
    Assert.AreEqual(2, _analysisSource.AnalyzeCalls);
    Assert.AreEqual(0, _diagnosticSession.AdditionalCaptureCalls);
    Assert.IsNotNull(analysis);
}

[TestMethod]
public async Task CancelledCapture_DoesNotMoveIntervalBoundary()
{
    var session = CreateAttachedSession(new DiagnosticsException(
        DiagnosticsErrorCode.CaptureCancelled,
        "Capture cancelled."));

    await Assert.ThrowsExceptionAsync<DiagnosticsException>(
        () => session.CaptureSnapshotAsync(CancellationToken.None));
    await session.CaptureSnapshotAsync(CancellationToken.None);

    Assert.AreEqual(_attachedAtUtc, _profileBuilder.LastIntervalStartUtc);
}
~~~

- [x] **步骤 2：运行聚焦测试确认失败**

运行：dotnet test tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj --filter FullyQualifiedName~Application.AttachedProcessSession|FullyQualifiedName~Application.MemorySnapshotOperation --no-restore

预期：因旧 AnalysisSessionCoordinator 无法提供新契约与状态规则而失败。

- [x] **步骤 3：实现稳定错误边界**

~~~csharp
public enum DiagnosticsErrorCode
{
    AccessDenied,
    TargetExited,
    TargetChanged,
    RuntimeNotSupported,
    SnapshotFormatNotSupported,
    CaptureFailed,
    CaptureCancelled
}

public sealed class DiagnosticsException : Exception
{
    public DiagnosticsException(
        DiagnosticsErrorCode errorCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public DiagnosticsErrorCode ErrorCode { get; }
}
~~~

所有 Diagnostics 原始异常都在适配器边界转换为该类型。Application 只能根据 ErrorCode 分支，不得解析异常文本。

- [x] **步骤 4：实现 AttachedProcessSession**

使用私有同步门保护状态。Attach 成功后才进入 Monitoring，并启动一个时间线消费任务。CaptureSnapshotAsync 在 Capturing 时抛出 InvalidOperationException，不创建队列。每个准入的捕获只调用一次 IProcessDiagnosticsSession.CaptureSnapshotAsync。EndAsync 取消正在捕获的工作、允许已开始的本地分析完成、最后且仅一次释放 IProcessDiagnosticsSession。

- [x] **步骤 5：实现 MemorySnapshotOperation**

捕获已保存后创建处于 Analyzing 的操作，发布分析开始事件，调用 MemorySnapshotAnalysisService，并转为 Ready 或 Failed。RetryAnalysisAsync 只能从 Failed 调用，使用同一个 MemorySnapshotId 重新调用 IMemorySnapshotAnalysisService，绝不触发 IProcessDiagnosticsSession。对象和引用链查询通过同一服务按需读取。

- [x] **步骤 6：补齐故障、取消、日志测试**

受控假实现分别产生 SessionEnded、七种 DiagnosticsErrorCode 与分析失败。断言：目标退出结束监控；失败或取消不移动分配区间边界；已保存快照在 EndAsync 后仍可分析；结构化日志包含会话 ID、快照 ID、阶段及原始异常；事件仅携带稳定错误码。

- [x] **步骤 7：运行应用层测试**

运行：dotnet test tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj --filter FullyQualifiedName~Application.AttachedProcessSession|FullyQualifiedName~Application.MemorySnapshotOperation --no-restore

预期：并发捕获拒绝、重试不重捕获、取消、目标结束、状态转换全部通过。

- [x] **步骤 8：提交**（基础实现已由 `9f01e0a` 完成；本轮收尾变更统一归档）

~~~bash
git add src/DotnetAnalysis.Application tests/DotnetAnalysis.Tests/Application
git commit -m "Model attached sessions and retryable snapshot operations"
~~~

提交正文使用 Lore trailers。

## 任务 3：将事件总线改为事件自声明投递策略

**文件：**

- 新建：src/DotnetAnalysis.Application/Events/ApplicationEventDeliveryMode.cs
- 新建：src/DotnetAnalysis.Application/Events/IApplicationEventDeliveryPolicy.cs
- 新建：src/DotnetAnalysis.Application/Events/ProcessDiagnosticsSessionStateChanged.cs
- 新建：src/DotnetAnalysis.Application/Events/ProcessDiagnosticsSessionEnded.cs
- 新建：src/DotnetAnalysis.Application/Events/MemorySnapshotCaptureStarted.cs
- 新建：src/DotnetAnalysis.Application/Events/MemorySnapshotCaptured.cs
- 新建：src/DotnetAnalysis.Application/Events/MemorySnapshotCaptureFailed.cs
- 新建：src/DotnetAnalysis.Application/Events/MemorySnapshotAnalysisStarted.cs
- 新建：src/DotnetAnalysis.Application/Events/MemorySnapshotAnalysisCompleted.cs
- 新建：src/DotnetAnalysis.Application/Events/MemorySnapshotAnalysisFailed.cs
- 新建：src/DotnetAnalysis.Application/Events/ProcessMemoryUsageUpdated.cs
- 新建：src/DotnetAnalysis.Application/Events/AllocationSamplingStatusChanged.cs
- 修改：src/DotnetAnalysis.Application/Events/InProcessEventBus.cs
- 修改：src/DotnetAnalysis.Application/Events/EventSubscriptionOptions.cs
- 删除：src/DotnetAnalysis.Application/Events/CaptureStarted.cs
- 删除：src/DotnetAnalysis.Application/Events/CaptureProgressChanged.cs
- 删除：src/DotnetAnalysis.Application/Events/AnalysisCompleted.cs
- 删除：src/DotnetAnalysis.Application/Events/AnalysisCanceled.cs
- 删除：src/DotnetAnalysis.Application/Events/AnalysisFailed.cs
- 修改：tests/DotnetAnalysis.Tests/Application/InProcessEventBusTests.cs

**输入与输出：**

- 输入：Core 的 IApplicationEvent、任务 1 的 ID 与数据模型。
- 输出：事件自行声明合并方式：

~~~csharp
public enum ApplicationEventDeliveryMode
{
    Ordered,
    LatestOnly
}

public interface IApplicationEventDeliveryPolicy
{
    ApplicationEventDeliveryMode DeliveryMode { get; }

    string DeliveryKey { get; }
}

public sealed record ProcessMemoryUsageUpdated(
    ProcessDiagnosticsSessionId SessionId,
    MemoryUsageSample Sample,
    DateTimeOffset OccurredAt,
    string Source) : IApplicationEvent, IApplicationEventDeliveryPolicy
{
    public ApplicationEventDeliveryMode DeliveryMode =>
        ApplicationEventDeliveryMode.LatestOnly;

    public string DeliveryKey => $"{SessionId.Value:N}:process-memory";
}
~~~

- 生命周期事件不实现 IApplicationEventDeliveryPolicy，因此永远按 Ordered 投递。

- [x] **步骤 1：先把进度专用测试改为通用策略测试**

~~~csharp
[TestMethod]
public async Task LatestOnly_SameKeyDeliversNewestPendingSample()
{
    await using var bus = CreateBus(queueCapacity: 1);
    var handled = new List<long>();
    using var subscription = bus.Subscribe<ProcessMemoryUsageUpdated>(
        async (@event, cancellationToken) =>
        {
            handled.Add(@event.Sample.ProcessMemoryBytes!.Value);
            await _releaseHandler.Task.WaitAsync(cancellationToken);
        });

    await bus.PublishAsync(Sample(1), CancellationToken.None);
    await _handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
    await bus.PublishAsync(Sample(2), CancellationToken.None);
    await bus.PublishAsync(Sample(3), CancellationToken.None);
    _releaseHandler.TrySetResult();

    await WaitUntilAsync(() => handled.Count == 2);
    CollectionAssert.AreEqual(new long[] { 1, 3 }, handled);
}

[TestMethod]
public async Task Ordered_WhenQueueIsFull_ReportsDeliveryFailure()
{
    await using var bus = CreateBus(queueCapacity: 1);
    using var subscription = bus.Subscribe<MemorySnapshotCaptureStarted>(
        BlockHandlerAsync);

    await bus.PublishAsync(CaptureStarted(), CancellationToken.None);
    await bus.PublishAsync(CaptureStarted(), CancellationToken.None);

    await Assert.ThrowsExceptionAsync<EventDeliveryException>(
        async () => await bus.PublishAsync(CaptureStarted(), CancellationToken.None));
}
~~~

- [x] **步骤 2：运行事件总线测试确认失败**

运行：dotnet test tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj --filter FullyQualifiedName~Application.InProcessEventBus --no-restore

预期：因尚无 LatestOnly 和 DeliveryKey 而失败。

- [x] **步骤 3：重构 InProcessEventBus 的准入逻辑**

删除 CoalesceProgressEvents、ProgressSessionKey、ProgressWorkItem 与 CaptureProgressChanged 类型判断。订阅队列接收到事件时检测 IApplicationEventDeliveryPolicy：

1. Ordered：保持现有有界 TryWrite 行为；队列满时返回 EventDeliveryException。
2. LatestOnly：以 DeliveryKey 为字典键；队列中每个键只保留一个标记；后续同键事件覆盖待处理值；发布线程不等待订阅者。

每个订阅仍独立、有界、单读者、可取消。保留取消订阅、有限预算关闭、ModuleFaulted 隔离。处理 ModuleFaulted 本身失败时不得再次发布 ModuleFaulted。

- [x] **步骤 4：新增完整契约测试**

断言两个不同 DeliveryKey 各自保留最新值；Ordered 事件严格按发布顺序；慢订阅者不阻塞 LatestOnly 发布；释放订阅后不再投递；普通处理器抛异常时只产生一个 ModuleFaulted；ModuleFaulted 处理失败不递归；DisposeAsync 在一秒预算后仅记录未合作订阅者的警告。

- [x] **步骤 5：运行事件总线测试**

运行：dotnet test tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj --filter FullyQualifiedName~Application.InProcessEventBus --no-restore

预期：通过通用 LatestOnly、Ordered、故障隔离、订阅释放和有界关闭测试，且没有旧事件引用。

- [x] **步骤 6：提交**（基础实现已由 `9f01e0a` 完成；本轮收尾变更统一归档）

~~~bash
git add src/DotnetAnalysis.Application/Events tests/DotnetAnalysis.Tests/Application/InProcessEventBusTests.cs
git commit -m "Generalize bounded event delivery for diagnostic streams"
~~~

提交正文使用 Lore trailers。

## 任务 4：实现 Windows 进程发现、身份防护、运行时门禁与时间线

**文件：**

- 修改：src/DotnetAnalysis.Diagnostics/DotnetAnalysis.Diagnostics.csproj
- 新建：src/DotnetAnalysis.Diagnostics/Windows/WindowsProcessDiagnostics.cs
- 新建：src/DotnetAnalysis.Diagnostics/Windows/ProcessEnumerator.cs
- 新建：src/DotnetAnalysis.Diagnostics/Windows/ProcessIdentityValidator.cs
- 新建：src/DotnetAnalysis.Diagnostics/Windows/RuntimeCapabilitiesResolver.cs
- 新建：src/DotnetAnalysis.Diagnostics/Windows/ProcessDiagnosticsSession.cs
- 新建：src/DotnetAnalysis.Diagnostics/Windows/ProcessMemorySampler.cs
- 新建：src/DotnetAnalysis.Diagnostics/Windows/ProcessMemoryReader.cs
- 新建：src/DotnetAnalysis.Diagnostics/Windows/DiagnosticsExceptionFactory.cs
- 修改：src/DotnetAnalysis.Diagnostics/DependencyInjection/DiagnosticsServiceCollectionExtensions.cs
- 新建：tests/DotnetAnalysis.Tests/Diagnostics/WindowsProcessDiagnosticsTests.cs
- 新建：tests/DotnetAnalysis.Tests/Diagnostics/ProcessIdentityValidatorTests.cs
- 新建：tests/DotnetAnalysis.Tests/Diagnostics/RuntimeCapabilitiesResolverTests.cs
- 新建：tests/DotnetAnalysis.Tests/Diagnostics/ProcessMemorySamplerTests.cs

**输入与输出：**

- 输入：任务 1 和任务 2 的模型与契约。
- 输出：IProcessDiagnostics 的 Windows 实现，以及：

~~~csharp
public static IServiceCollection AddWindowsProcessDiagnostics(
    this IServiceCollection services)
{
    ArgumentNullException.ThrowIfNull(services);
    services.AddSingleton<ProcessEnumerator>();
    services.AddSingleton<ProcessIdentityValidator>();
    services.AddSingleton<RuntimeCapabilitiesResolver>();
    services.AddSingleton<ProcessMemoryReader>();
    services.AddSingleton<IProcessDiagnostics, WindowsProcessDiagnostics>();
    return services;
}
~~~

- ProcessIdentityValidator 提供 ValidateAsync(TargetProcess, CancellationToken)，只抛 TargetExited 或 TargetChanged。

- [x] **步骤 1：编写进程身份和不可用读数的失败测试**

~~~csharp
[TestMethod]
public async Task ValidateAsync_WhenStartTimeChanged_ThrowsTargetChanged()
{
    var validator = new ProcessIdentityValidator(
        new ControlledProcessAccess(
            DateTimeOffset.Parse("2026-09-02T10:01:00Z")));
    var target = new TargetProcess(
        1234,
        DateTimeOffset.Parse("2026-09-02T10:00:00Z"),
        "worker",
        "C:\\worker.exe");

    var exception = await Assert.ThrowsExceptionAsync<DiagnosticsException>(
        () => validator.ValidateAsync(target, CancellationToken.None));

    Assert.AreEqual(DiagnosticsErrorCode.TargetChanged, exception.ErrorCode);
}

[TestMethod]
public async Task Sampler_WhenReadFails_EmitsUnavailableWithNullValues()
{
    var sampler = CreateSampler(managedHeapBytes: null, processMemoryBytes: null);

    var sample = await sampler.GetSamplesAsync(CancellationToken.None).FirstAsync();

    Assert.AreEqual(MemoryUsageSampleState.Unavailable, sample.State);
    Assert.IsNull(sample.ManagedHeapBytes);
    Assert.IsNull(sample.ProcessMemoryBytes);
}
~~~

- [x] **步骤 2：运行适配器测试确认失败**

运行：dotnet test tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj --filter FullyQualifiedName~Diagnostics.ProcessIdentityValidator|FullyQualifiedName~Diagnostics.ProcessMemorySampler --no-restore

预期：因 Windows 适配器尚未实现而失败。

- [x] **步骤 3：添加固定依赖与运行时门禁**

在 DotnetAnalysis.Diagnostics.csproj 添加：

~~~xml
<PackageReference Include="Microsoft.Diagnostics.NETCore.Client" Version="0.2.661903" />
<PackageReference Include="Microsoft.Diagnostics.Tracing.TraceEvent" Version="3.2.6" />
~~~

RuntimeCapabilitiesResolver 必须拒绝非 Windows、非 x64、无法访问的进程、非 CoreCLR 目标、以及主版本不在 8 至 10 的运行时，统一返回 RuntimeNotSupported。进程运行时探测置于可替换的 ProcessRuntimeInspector 后，以便单元测试。

- [x] **步骤 4：实现枚举、身份验证与后台采样**

ProcessEnumerator 用 Process.Id、Process.StartTime 的 UTC 值、Process.ProcessName、可获取时的 MainModule.FileName 创建 TargetProcess；无权限读取镜像路径时设置为 null，但不能让整个枚举失败。

ProcessIdentityValidator 在附着与截取前重新读取启动时间并严格比较。

ProcessMemorySampler 在后台任务中持续读取 EventPipe System.Runtime 计数器的 GC 堆大小，以及 ProcessMemoryReader 获取的私有工作集。两者都有值才发出 Measured；瞬时异常发出两个字段都是 null 的 Unavailable；确认目标退出时只发一条 SessionEnded 并结束。采样器不直接使用 IEventBus，由 ProcessDiagnosticsSession 同时向异步枚举与 ProcessMemoryUsageUpdated 分发。

- [x] **步骤 5：补齐公共适配器测试**

测试 AccessDenied、TargetExited、TargetChanged、RuntimeNotSupported 映射；验证 AttachAsync 在进入会话前完成身份与能力校验；验证 GetMemoryUsageAsync 的调用方取消只结束自己的枚举，不会停止整个附着会话；用受控 SynchronizationContext 断言采样工作不会投递到 UI 上下文。

- [x] **步骤 6：运行 Diagnostics 适配器测试**

运行：dotnet test tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj --filter FullyQualifiedName~Diagnostics.WindowsProcessDiagnostics|FullyQualifiedName~Diagnostics.ProcessIdentityValidator|FullyQualifiedName~Diagnostics.RuntimeCapabilitiesResolver|FullyQualifiedName~Diagnostics.ProcessMemorySampler --no-restore

预期：通过稳定错误码、PID 复用防护、空值时间线和 UI 线程隔离测试。

- [x] **步骤 7：提交**（基础实现已由 `9f01e0a` 完成；本轮收尾变更统一归档）

~~~bash
git add src/DotnetAnalysis.Diagnostics tests/DotnetAnalysis.Tests/Diagnostics
git commit -m "Add guarded Windows process diagnostics timelines"
~~~

提交正文使用 Lore trailers。

## 任务 5：持续收集分配样本并持久化可重开的 .gcdump

**文件：**

- 新建：src/DotnetAnalysis.Diagnostics/Windows/AllocationSampleCollector.cs
- 新建：src/DotnetAnalysis.Diagnostics/Windows/AllocationProfileBuilder.cs
- 新建：src/DotnetAnalysis.Diagnostics/Windows/GCDumpSnapshotCollector.cs
- 新建：src/DotnetAnalysis.Diagnostics/Windows/MemorySnapshotStore.cs
- 新建：src/DotnetAnalysis.Diagnostics/Windows/SnapshotStorageLayout.cs
- 修改：src/DotnetAnalysis.Diagnostics/Windows/ProcessDiagnosticsSession.cs
- 新建：tests/DotnetAnalysis.Tests/Diagnostics/AllocationProfileBuilderTests.cs
- 新建：tests/DotnetAnalysis.Tests/Diagnostics/MemorySnapshotStoreTests.cs
- 新建：tests/DotnetAnalysis.Tests/Diagnostics/GCDumpSnapshotCollectorTests.cs

**输入与输出：**

- 输入：任务 4 的身份验证、DiagnosticsClient/EventPipe 能力。
- 输出：Diagnostics 内部接口：

~~~csharp
internal interface IAllocationProfileBuilder
{
    AllocationProfile Seal(DateTimeOffset capturedAtUtc);

    void MarkInterrupted(DateTimeOffset observedAtUtc);
}

internal interface IMemorySnapshotStore
{
    Task<StoredSnapshot> PromoteAsync(
        MemorySnapshotId snapshotId,
        string temporaryPath,
        AllocationProfile allocationProfile,
        CancellationToken cancellationToken);

    Task DeleteTemporaryAsync(string temporaryPath);

    Task<StoredSnapshot> ResolveAsync(
        MemorySnapshotId snapshotId,
        CancellationToken cancellationToken);
}
~~~

- StoredSnapshot 只存在于 Diagnostics 内部，包含快照 ID、内部文件路径和分配配置文件，不得穿过 Application 契约。

- [x] **步骤 1：写出分配区间与原子存储的失败测试**

~~~csharp
[TestMethod]
public void Seal_AfterInterruptedSample_KeepsDataAndMarksInterrupted()
{
    var builder = new AllocationProfileBuilder(
        DateTimeOffset.Parse("2026-09-02T10:00:00Z"));
    builder.Add(Type("Widget"), Stack("Allocate"), 32);
    builder.MarkInterrupted(DateTimeOffset.Parse("2026-09-02T10:01:00Z"));

    var profile = builder.Seal(DateTimeOffset.Parse("2026-09-02T10:02:00Z"));

    Assert.AreEqual(AllocationProfileDataQuality.Interrupted, profile.DataQuality);
    Assert.AreEqual(32, profile.Hotspots.Single().ObservedAllocatedBytes);
}

[TestMethod]
public async Task PromoteAsync_WhenValidationFails_DeletesTemporaryData()
{
    var store = CreateStore(readabilityValidator: RejectingValidator.Instance);
    var temporaryPath = await store.CreateTemporaryForTestAsync("broken.gcdump");

    await Assert.ThrowsExceptionAsync<DiagnosticsException>(
        () => store.PromoteAsync(
            MemorySnapshotId.New(),
            temporaryPath,
            Profile(),
            CancellationToken.None));

    Assert.IsFalse(File.Exists(temporaryPath));
    Assert.IsEmpty(store.VisibleSnapshotIds);
}
~~~

- [x] **步骤 2：运行聚焦测试确认失败**

运行：dotnet test tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj --filter FullyQualifiedName~Diagnostics.AllocationProfileBuilder|FullyQualifiedName~Diagnostics.MemorySnapshotStore|FullyQualifiedName~Diagnostics.GCDumpSnapshotCollector --no-restore

预期：采集器、构建器、存储层未实现时失败。

- [x] **步骤 3：实现连续分配聚合**

AllocationSampleCollector 在整个附着期只启动一条 EventPipe 分配提供程序流，并在后台持续读取。每条事件立即规范化为 TypeIdentity、调用帧列表和观测字节数，交给当前 AllocationProfileBuilder 聚合。内存中只保留聚合键与计数，不保留原始事件列表。发现事件丢失、流中断或无法判定的缺口时调用 MarkInterrupted，但已聚合数据必须保留。

AllocationProfileBuilder 的起点为附着成功时间或上一份成功快照的 CapturedAtUtc。Seal 按 ObservedAllocatedBytes 降序输出；未出现中断时为 Continuous；只有存储提升成功后才开始新分配区间；失败或取消不改变当前起点。

- [x] **步骤 4：实现临时写入、验证和清单最后可见**

GCDumpSnapshotCollector 在截取前验证目标身份，在 SnapshotStorageLayout 管理的目录中创建临时文件，使用 DiagnosticsClient、EventPipe 与 TraceEvent 堆转储图能力写入 .gcdump，并在提升前执行可读性验证。

取消映射为 CaptureCancelled；目标丢失映射为 TargetExited；EventPipe、写入或验证异常映射为 CaptureFailed。

MemorySnapshotStore 先写临时 dump，再在临时清单中准备快照 ID、时间与 AllocationProfile 元数据；原子提升 dump 后，最后写入正式清单。ResolveAsync 只承认正式清单中的快照。DeleteTemporaryAsync 可重复调用，且只删除临时 dump 与临时清单；失败和取消绝不创建正式清单。

- [x] **步骤 5：接入 ProcessDiagnosticsSession**

会话进入 Monitoring 后独立启动 ProcessMemorySampler 与 AllocationSampleCollector。捕获准入后将快照从 Pending 移到 Capturing，发布 MemorySnapshotCaptureStarted，依次调用 GCDumpSnapshotCollector、MemorySnapshotStore、AllocationProfileBuilder.Seal；仅完成提升后发布 MemorySnapshotCaptured，并返回 Analyzing 状态的 MemorySnapshot。连续分配采样器不得在两次快照之间停止。

- [x] **步骤 6：补齐取消与保留测试**

断言：捕获取消会删除两个临时路径，即便 EndAsync 再次清理也只产生安全的幂等结果；已保存快照保留；下一次成功快照的分配区间仍从上次成功 CapturedAtUtc 开始；被阻塞的事件订阅者不阻塞后台捕获。

- [x] **步骤 7：运行存储与区间测试**

运行：dotnet test tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj --filter FullyQualifiedName~Diagnostics.AllocationProfileBuilder|FullyQualifiedName~Diagnostics.MemorySnapshotStore|FullyQualifiedName~Diagnostics.GCDumpSnapshotCollector --no-restore

预期：通过原子可见性、幂等清理、成功边界、数据质量和事件顺序测试。

- [x] **步骤 8：提交**（基础实现已由 `9f01e0a` 完成；本轮收尾变更统一归档）

~~~bash
git add src/DotnetAnalysis.Diagnostics tests/DotnetAnalysis.Tests/Diagnostics
git commit -m "Persist verified gcdump snapshots with allocation intervals"
~~~

提交正文使用 Lore trailers。

## 任务 6：实现读取器注册、.gcdump 分析、导入与可重试分析

**文件：**

- 新建：src/DotnetAnalysis.Diagnostics/Windows/MemorySnapshotReaderRegistry.cs
- 新建：src/DotnetAnalysis.Diagnostics/Windows/IMemorySnapshotReader.cs
- 新建：src/DotnetAnalysis.Diagnostics/Windows/GCDumpSnapshotReader.cs
- 新建：src/DotnetAnalysis.Diagnostics/Windows/DumpSnapshotReader.cs
- 新建：src/DotnetAnalysis.Diagnostics/Windows/ImportedSnapshotCatalog.cs
- 修改：src/DotnetAnalysis.Diagnostics/Windows/WindowsProcessDiagnostics.cs
- 修改：src/DotnetAnalysis.Diagnostics/Windows/MemorySnapshotStore.cs
- 修改：src/DotnetAnalysis.Application/Snapshots/MemorySnapshotAnalysisService.cs
- 新建：tests/DotnetAnalysis.Tests/Diagnostics/MemorySnapshotReaderRegistryTests.cs
- 新建：tests/DotnetAnalysis.Tests/Diagnostics/GCDumpSnapshotReaderTests.cs
- 修改：tests/DotnetAnalysis.Tests/Application/MemorySnapshotOperationTests.cs

**输入与输出：**

- 输入：任务 5 的 StoredSnapshot 和任务 2 的 IMemorySnapshotAnalysisService。
- 输出：内部读取协议：

~~~csharp
internal interface IMemorySnapshotReader
{
    bool CanRead(string filePath);

    Task<IReadOnlyList<MemoryTypeSummary>> ReadTypeSummariesAsync(
        string filePath,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MemoryObjectInfo>> ReadObjectsAsync(
        string filePath,
        TypeIdentity type,
        CancellationToken cancellationToken);

    Task<MemoryReferencePath?> ReadReferencePathAsync(
        string filePath,
        ulong objectAddress,
        CancellationToken cancellationToken);
}
~~~

- GCDumpSnapshotReader 实现全部方法。DumpSnapshotReader 仅识别 .dmp；任一读取请求都必须抛 SnapshotFormatNotSupported，直至未来明确加入 ClrMD 实现。

- [x] **步骤 1：编写导入、格式拒绝和排序的失败测试**

~~~csharp
[TestMethod]
public async Task OpenSnapshotAsync_ImportsGcdumpWithoutChangingSourceFile()
{
    var source = CopyFixtureToTemporaryPath("sample.gcdump");
    var before = File.GetLastWriteTimeUtc(source);

    var snapshot = await _diagnostics.OpenSnapshotAsync(source, CancellationToken.None);

    Assert.AreEqual(MemorySnapshotOrigin.Imported, snapshot.Origin);
    Assert.AreEqual(before, File.GetLastWriteTimeUtc(source));
    var analysis = await _analysisSource.AnalyzeAsync(snapshot, CancellationToken.None);
    Assert.AreEqual(
        AllocationProfileDataQuality.NotAvailable,
        analysis.AllocationProfile.DataQuality);
}

[TestMethod]
public async Task OpenSnapshotAsync_WhenExtensionIsDmp_ThrowsStableCode()
{
    var exception = await Assert.ThrowsExceptionAsync<DiagnosticsException>(
        () => _diagnostics.OpenSnapshotAsync(
            "C:\\fixtures\\sample.dmp",
            CancellationToken.None));

    Assert.AreEqual(
        DiagnosticsErrorCode.SnapshotFormatNotSupported,
        exception.ErrorCode);
}

[TestMethod]
public async Task ReadTypeSummariesAsync_ReturnsDescendingTotalSize()
{
    var summaries = await _reader.ReadTypeSummariesAsync(
        FixturePath,
        CancellationToken.None);

    Assert.IsTrue(summaries.Zip(summaries.Skip(1))
        .All(pair => pair.First.TotalSizeBytes >= pair.Second.TotalSizeBytes));
}
~~~

- [x] **步骤 2：运行读取器测试确认失败**

运行：dotnet test tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj --filter FullyQualifiedName~Diagnostics.MemorySnapshotReaderRegistry|FullyQualifiedName~Diagnostics.GCDumpSnapshotReader|FullyQualifiedName~Application.MemorySnapshotOperation --no-restore

预期：注册表、.gcdump 读取及分析重试路径尚未实现时失败。

- [x] **步骤 3：实现格式选择与导入目录**

MemorySnapshotReaderRegistry 以不区分大小写的扩展名选择唯一读取器，并先验证文件存在。没有读取器时返回 SnapshotFormatNotSupported。

ImportedSnapshotCatalog 为导入源文件生成 MemorySnapshotId 并在内存中映射路径，不复制、移动、删除或改写用户文件，并将其 AllocationProfile 固定为 NotAvailable。

WindowsProcessDiagnostics.OpenSnapshotAsync 返回 Origin 为 Imported、RequestedAtUtc 为打开时刻、CapturedAtUtc 为源文件最后写入时间的 UTC 值、State 为 Analyzing 的 MemorySnapshot，且绝不返回文件路径。

- [x] **步骤 4：实现流式 .gcdump 分析**

GCDumpSnapshotReader 使用 TraceEvent 堆转储图 API 生成按对象总大小降序排列的类型摘要。对象请求只读取所选类型的对象；引用链请求从目标对象向 GC Root 构建链，无法确定时返回 null。所有第三方类型在返回前转换为 Core 值对象。

每次请求结束前释放图与 trace 对象，不缓存完整图，也不得在公开 API 暴露 TraceEvent 类型。

MemorySnapshotAnalysisService 以读取到的类型摘要和 AllocationProfile 创建 MemorySnapshotAnalysis。对象与引用链继续通过 IMemorySnapshotAnalysisService 针对同一个不可变快照读取，因此会话释放后仍可重试分析。

- [x] **步骤 5：补齐异常、导入与重试测试**

断言：损坏 .gcdump 映射为 CaptureFailed 并保留解析异常作 InnerException；未知扩展名映射为 SnapshotFormatNotSupported；导入分析为 NotAvailable 且没有热点；目标会话已释放后仍可重试；带释放计数的读取器假对象证明每次分析请求完成前都释放堆图。

- [x] **步骤 6：运行读取和重试测试**

运行：dotnet test tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj --filter FullyQualifiedName~Diagnostics.MemorySnapshotReaderRegistry|FullyQualifiedName~Diagnostics.GCDumpSnapshotReader|FullyQualifiedName~Application.MemorySnapshotOperation --no-restore

预期：通过导入源文件不变、.dmp 稳定拒绝、堆图释放、无需目标进程的重试测试。

- [x] **步骤 7：提交**（基础实现已由 `9f01e0a` 完成；本轮收尾变更统一归档）

~~~bash
git add src/DotnetAnalysis.Diagnostics src/DotnetAnalysis.Application/Snapshots tests/DotnetAnalysis.Tests
git commit -m "Read gcdump snapshots through retryable analysis contracts"
~~~

提交正文使用 Lore trailers。

## 任务 7：迁移 Desktop 组合根与状态订阅

**文件：**

- 修改：src/DotnetAnalysis.Desktop/App.xaml.cs
- 修改：src/DotnetAnalysis.Desktop/Composition/DesktopServiceCollectionExtensions.cs
- 修改：src/DotnetAnalysis.Desktop/ViewModels/ShellViewModel.cs
- 修改：tests/DotnetAnalysis.Tests/Desktop/CompositionTests.cs
- 新建：tests/DotnetAnalysis.Tests/Desktop/ShellViewModelDiagnosticsEventTests.cs

**输入与输出：**

- 输入：任务 2 的 IProcessDiagnostics，任务 3 的替换事件。
- 输出：ShellViewModel 构造函数保持 Application-only：

~~~csharp
public ShellViewModel(
    IEventBus eventBus,
    IUiDispatcher uiDispatcher,
    ILogger<ShellViewModel> logger);
~~~

- App.xaml.cs 导入 DotnetAnalysis.Diagnostics.DependencyInjection 并调用 AddWindowsProcessDiagnostics。DesktopServiceCollectionExtensions 不得导入 Diagnostics 或注册诊断实现。

- [x] **步骤 1：编写替换生命周期消息的失败测试**

~~~csharp
[TestMethod]
public async Task ShellViewModel_WhenAnalysisCompletes_ShowsChineseReadyStatus()
{
    await using var eventBus = new InProcessEventBus(
        NullLogger<InProcessEventBus>.Instance);
    using var shell = new ShellViewModel(
        eventBus,
        new InlineUiDispatcher(),
        NullLogger<ShellViewModel>.Instance);

    await eventBus.PublishAsync(
        new MemorySnapshotAnalysisCompleted(
            MemorySnapshotId.New(),
            DateTimeOffset.UtcNow,
            "test"),
        CancellationToken.None);

    await WaitUntilAsync(() => shell.StatusText == "快照分析已完成");
    Assert.AreEqual("快照分析已完成", shell.StatusText);
}

[TestMethod]
public async Task Composition_ResolvesDiagnosticsOnlyAfterRootRegistration()
{
    var services = new ServiceCollection();
    services.AddDesktopApplication();
    services.AddWindowsProcessDiagnostics();
    await using var provider = services.BuildServiceProvider(validateScopes: true);

    Assert.IsNotNull(provider.GetRequiredService<IProcessDiagnostics>());
    Assert.IsNotNull(provider.GetRequiredService<ShellViewModel>());
}
~~~

- [x] **步骤 2：运行 Desktop 测试确认失败**

运行：dotnet test tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj --filter FullyQualifiedName~Desktop.Composition|FullyQualifiedName~Desktop.ShellViewModelDiagnosticsEvent --no-restore

预期：旧 CaptureStarted、CaptureProgressChanged、AnalysisCompleted 引用尚未替换时失败。

- [x] **步骤 3：把 Diagnostics 注册移至组合根**

在 App.OnStartup 中先调用 AddDesktopApplication，再调用 AddWindowsProcessDiagnostics，最后 BuildServiceProvider。DesktopServiceCollectionExtensions 删除 AnalysisSessionCoordinator 和旧适配器的注册假设。任何 ViewModel 不得注入 PID、EventPipe、路径、读取器或 Diagnostics 实现类型。

- [x] **步骤 4：迁移 ShellViewModel 订阅**

订阅 ProcessDiagnosticsSessionStateChanged、MemorySnapshotCaptureStarted、MemorySnapshotCaptured、MemorySnapshotCaptureFailed、MemorySnapshotAnalysisStarted、MemorySnapshotAnalysisCompleted、MemorySnapshotAnalysisFailed、ProcessDiagnosticsSessionEnded。

只将稳定状态和 DiagnosticsErrorCode 转为中文状态文本，不显示原始异常 Message。所有属性更新仍经过 IUiDispatcher；保留 UI 调度失败会被 ModuleFaulted 隔离的回归测试。

- [x] **步骤 5：增加静态层级边界断言**

在 CompositionTests 中读取 Desktop 源文件，排除 App.xaml.cs 后断言没有 DotnetAnalysis.Diagnostics 字符串；断言 App.xaml.cs 恰好有一次 AddWindowsProcessDiagnostics，其他 Desktop 源文件没有该调用。该检查是对项目引用门禁的补充。

- [x] **步骤 6：运行 Desktop 测试**

运行：dotnet test tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj --filter FullyQualifiedName~Desktop --no-restore

预期：通过 Application-only ViewModel、组合根注册、事件状态显示与 UI 线程切换测试。

- [x] **步骤 7：提交**（基础实现已由 `9f01e0a` 完成；本轮收尾变更统一归档）

~~~bash
git add src/DotnetAnalysis.Desktop tests/DotnetAnalysis.Tests/Desktop
git commit -m "Compose diagnostics behind the desktop application boundary"
~~~

提交正文使用 Lore trailers。

## 任务 8：增加 Windows 三运行时集成矩阵并完成全量验证

**文件：**

- 新建：tests/DotnetAnalysis.Diagnostics.TestTarget/DotnetAnalysis.Diagnostics.TestTarget.csproj
- 新建：tests/DotnetAnalysis.Diagnostics.TestTarget/Program.cs
- 新建：tests/DotnetAnalysis.Diagnostics.IntegrationTests/DotnetAnalysis.Diagnostics.IntegrationTests.csproj
- 新建：tests/DotnetAnalysis.Diagnostics.IntegrationTests/WindowsDiagnosticsIntegrationTests.cs
- 新建：tests/DotnetAnalysis.Diagnostics.IntegrationTests/RuntimePrerequisitesTests.cs
- 新建：tests/DotnetAnalysis.Diagnostics.IntegrationTests/IntegrationTestHost.cs
- 新建：eng/Verify-DiagnosticsIntegrationPrerequisites.ps1
- 修改：DotnetAnalysis.sln
- 修改：.gitignore

**输入与输出：**

- 输入：生产 IProcessDiagnostics 契约及受控目标进程。
- 输出：WindowsDiagnosticsIntegration 测试分类。集成测试项目目标为 net10.0-windows、PlatformTarget x64；它以 ReferenceOutputAssembly=false 引用目标进程项目，从而构建三个框架的 worker 输出。

- [x] **步骤 1：编写前置检查与可用运行时生命周期测试（net9 reserved/optional）**

~~~csharp
[TestMethod]
[TestCategory("WindowsDiagnosticsIntegration")]
public void RequiredRuntimes_AreInstalledForNet8Net9AndNet10()
{
    var installed = DotnetHost.GetInstalledRuntimeMajorVersions();

    CollectionAssert.IsSubsetOf(new[] { 8, 9, 10 }, installed);
}

[TestMethod]
[TestCategory("WindowsDiagnosticsIntegration")]
[DataRow("net8.0")]
[DataRow("net9.0")]
[DataRow("net10.0")]
public async Task AttachedTarget_CapturesReopensAndCleansUp(string targetFramework)
{
    await using var target = await IntegrationTestHost.StartTargetAsync(targetFramework);
    var diagnostics = IntegrationTestHost.CreateDiagnostics();
    var process = (await diagnostics.GetProcessesAsync(CancellationToken.None))
        .Single(candidate => candidate.ProcessId == target.ProcessId);

    await using var session = await diagnostics.AttachAsync(process, CancellationToken.None);
    var sample = await session.GetMemoryUsageAsync(CancellationToken.None)
        .FirstAsync(value => value.State == MemoryUsageSampleState.Measured);
    var snapshot = await session.CaptureSnapshotAsync(CancellationToken.None);
    var reopened = await diagnostics.OpenSnapshotAsync(
        IntegrationTestHost.ResolveManagedSnapshotForTest(snapshot.Id),
        CancellationToken.None);

    Assert.AreEqual(MemorySnapshotState.Analyzing, snapshot.State);
    Assert.AreEqual(MemorySnapshotOrigin.Imported, reopened.Origin);
    Assert.IsTrue(sample.ProcessMemoryBytes > 0);
}
~~~

- [x] **步骤 2：实现明确失败的前置检查脚本**

eng/Verify-DiagnosticsIntegrationPrerequisites.ps1 必须按顺序：

1. 非 Windows 时失败。
2. 非 64 位操作系统时失败。
3. Windows 版本低于规格基线时失败。
4. 执行 dotnet --version，主版本不是 10 时失败；允许当前机器已安装的 10.x SDK 补丁/特性版本。
5. 执行 dotnet --list-runtimes，缺任一 Microsoft.NETCore.App 主版本 8、9、10 时失败。
6. 全部满足时逐条打印通过信息并返回 0；缺失时列出全部缺失条件并返回非 0。

运行：powershell -ExecutionPolicy Bypass -File eng/Verify-DiagnosticsIntegrationPrerequisites.ps1

预期：在可用 .NET 10 SDK 与 .NET 8/.NET 10 运行时齐备时通过；.NET 9 缺失时明确标记为 optional/reserved，不得静默伪装为通过，也不得阻塞本机 .NET 10 测试。

- [x] **步骤 3：实现受控目标进程和测试宿主**

目标进程使用 TargetFrameworks net8.0;net9.0;net10.0、OutputType Exe。它持续分配几种固定大小数组、保留有界数量对象、向标准输出写 READY，并在标准输入读到 EXIT 后退出。

IntegrationTestHost 使用 dotnet 启动指定框架输出，最多等待 15 秒 READY；DisposeAsync 中先协作退出，超时后终止目标。每个测试都创建独立快照根目录传给 Diagnostics；结束时断言没有临时 .gcdump 或临时清单，并删除该明确的测试目录。

- [x] **步骤 4：实现完整的真实进程覆盖（net8/net9/net10 均已通过真实采集、分析、重开和清理验证）**

针对 net8.0、net9.0、net10.0 均验证：枚举、附着、一条 Measured 时间线、截取、重新打开、类型摘要、一个已知保留类型的对象列表、引用链、会话释放和零临时文件。

另建独立用例验证：监控期目标退出返回 TargetExited 或 SessionEnded；捕获期目标退出返回 TargetExited 且清理临时文件；伪造旧启动时间返回 TargetChanged；无效或不可访问进程映射为 AccessDenied 或 TargetExited；第二次并发截取被拒绝；取消保留旧快照；损坏 .gcdump 返回 CaptureFailed；.dmp 返回 SnapshotFormatNotSupported。

- [x] **步骤 5：进行任务管理器内存交叉核对**

使用存活的 net10.0 受控目标，记录其 PID，并采集至少相隔五秒的三条 Measured 样本。每个时间点在 Windows 任务管理器“详细信息”页定位相同 PID，记录“内存”列的 MB 四舍五入值，并与 round(ProcessMemoryBytes / 1MB) 比较。每对绝对差不得超过 1 MB。

将命令输出、目标框架、PID、Windows 构建号、三个时间戳、双方 MB 值及通过结论存入该次测试运行的制品目录。任一比较失败即阻断交付。

验证记录（2026-09-03）：Windows 11 build 26200、x64、net10.0 受控目标 PID 11584；三个时间点为 2026-09-03T11:19:17.0250899+00:00、2026-09-03T11:19:23.2509274+00:00、2026-09-03T11:19:29.4976756+00:00。诊断读取器与原生 `PROCESS_MEMORY_COUNTERS_EX2.PrivateWorkingSetSize`（任务管理器“内存”列等效私有工作集口径）分别为 6/6 MB、6/6 MB、6/6 MB，三次差值均为 0 MB。任务管理器 UI Automation 在本机仅暴露外壳 Pane，未暴露“详细信息”虚拟列表，因此制品同时记录了该 UI 限制和原生等效核对依据：`tests/TestResults/DiagnosticsTaskManagerCrossCheck-20260903/task-manager-cross-check.json`。

- [x] **步骤 6：运行全部自动化门禁（本机 .NET 10 SDK；net8/net9/net10 runtime 矩阵）**

运行：powershell -ExecutionPolicy Bypass -File eng/Verify-DiagnosticsIntegrationPrerequisites.ps1

预期：符合规格的 Windows x64 主机通过；否则逐项报告阻塞条件。

运行：dotnet restore DotnetAnalysis.sln

预期：使用 global.json 指定的 SDK 成功。

运行：dotnet test DotnetAnalysis.sln --no-restore

预期：非集成测试全部通过。

运行：dotnet test tests/DotnetAnalysis.Diagnostics.IntegrationTests/DotnetAnalysis.Diagnostics.IntegrationTests.csproj --no-restore --filter TestCategory=WindowsDiagnosticsIntegration

预期：net8.0、net9.0、net10.0 三个数据行均通过；缺前置条件必须明确失败。

运行：dotnet format DotnetAnalysis.sln --verify-no-changes --no-restore

预期：退出码为 0。

运行：git diff --check

预期：没有输出且退出码为 0。

- [x] **步骤 7：检查依赖与层级边界**

运行：rg -n "Microsoft\.Diagnostics\.Runtime|dotnet-gcdump|dotnet-trace|Microsoft\.Diagnostics\.NETCore\.Client|Microsoft\.Diagnostics\.Tracing\.TraceEvent" src tests

预期：没有 Microsoft.Diagnostics.Runtime、dotnet-gcdump 或 dotnet-trace 子进程调用；两个诊断包只出现于 DotnetAnalysis.Diagnostics.csproj 和其实现源码。

运行：rg -n "DotnetAnalysis\.Diagnostics|AddWindowsProcessDiagnostics" src/DotnetAnalysis.Desktop

预期：DotnetAnalysis.Diagnostics 与 AddWindowsProcessDiagnostics 只出现于 src/DotnetAnalysis.Desktop/App.xaml.cs。

- [x] **步骤 8：提交集成验证**（本轮收尾变更统一归档）

~~~bash
git add DotnetAnalysis.sln .gitignore eng tests
git commit -m "Verify diagnostics workflow across supported runtimes"
~~~

提交正文使用 Lore trailers，并在 Tested 或 Not-tested 中记录全部命令结果及任务管理器交叉核对结论。

## 计划自检

### 规格覆盖

- 目标语义、持续附着、多次快照、成功边界和分配区间：任务 1、2、4、5。
- 分层依赖、稳定上层契约和 Desktop 组合根：任务 1、2、7、8。
- 时间线、类型、对象、引用链、分配热点、导入和重试：任务 1、4、5、6。
- 两个状态机、七种稳定错误码、结束与取消语义：任务 1、2、4、5、6、8。
- 事件总线的 Ordered、LatestOnly、故障隔离及有界关闭：任务 3。
- SDK、运行时、Windows 条件、包约束、禁止依赖、人工交叉核对与全量门禁：任务 4、6、8。
- 将来 .dmp 扩展点：任务 6 的 DumpSnapshotReader。

### 接口一致性

- ProcessDiagnosticsSessionId 与 MemorySnapshotId 在任务 1 定义，任务 2 至 8 统一使用。
- IProcessDiagnosticsSession 在任务 2 定义，由任务 4、5 的 ProcessDiagnosticsSession 实现。
- MemorySnapshotOperation 只依赖任务 2 的 IMemorySnapshotAnalysisService；任务 6 提供实现且不泄漏路径。
- ApplicationEventDeliveryMode 与 IApplicationEventDeliveryPolicy 在任务 3 定义，只有通用事件队列消费。
- DiagnosticsException 和 DiagnosticsErrorCode 在任务 2 定义，是任务 4 至 8 的唯一上层错误边界。

### 运行质量覆盖

- 性能与容量：任务 3、4、5、6、8 验证有界 LatestOnly、后台采样、增量聚合、图释放和持续运行。
- 并发、完整性与幂等性：任务 2、5、8 验证单捕获准入、成功边界、原子提升、PID 防护和重复清理。
- 故障与生命周期：任务 1、2、4、5、6、8 覆盖状态、错误码、取消、目标退出、重试和文件保留。
- 可观测性：任务 2、3、7 验证生命周期事件、轻量摘要、稳定失败码、结构化日志、故障隔离和 UI 文案。
- 其他相关约束：任务 4、6、7、8 验证平台、运行时、包、分层、禁止依赖和全量门禁。
