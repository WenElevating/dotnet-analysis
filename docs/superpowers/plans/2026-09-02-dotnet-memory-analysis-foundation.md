# .NET 内存分析工具基础框架 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** 建立可构建、可测试的 Windows x64 .NET 内存分析工具基础框架，落实项目边界、会话状态机、自研事件总线和自研轻量 MVVM；本计划不实现真实 EventPipe 采集或采集文件解析。

**Architecture:** 五项目 solution：WPF Desktop 只调用 Application；Application 编排用例、会话状态和进程内事件总线；Core 保持纯模型和规则；Diagnostics 为后续适配器预留实现位置。首个可运行程序只验证组合根、事件流和 UI 基础设施，不提供尚未接入的采集按钮。

**Tech Stack:** .NET SDK 10.0.303、C#、WPF、MSTest、Microsoft.Extensions.DependencyInjection、Microsoft.Extensions.Logging.Abstractions；不使用第三方 MVVM 或事件总线。

**Spec:** docs/superpowers/specs/2026-09-02-dotnet-memory-analysis-design.md

## Global Constraints

- 目标为 Windows x64 本机 .NET 8/9/10 CoreCLR；基础项目用 net10.0，WPF 项目用 net10.0-windows。
- global.json 固定 SDK 10.0.303，rollForward 为 latestPatch，不依赖 .NET 11 预览 SDK。
- Core 不得依赖 WPF、诊断 SDK、文件 I/O 或依赖注入容器。
- Desktop 不得引用 Diagnostics，也不得直接操作 PID、EventPipe 或采集文件。
- 本计划不安装诊断 SDK，不启动外部 dotnet-gcdump 或 dotnet-trace 进程。
- MVVM 只能使用自研 ObservableObject、RelayCommand 和 AsyncRelayCommand。
- 事件总线是进程内的强类型异步通知；命令、查询、返回值和取消必须走显式接口。
- 所有会话相关事件携带 SessionId；事件不传输大对象图、调用树或原始字节。
- UI 线程不得执行诊断采集、文件解析或事件订阅处理器的后台工作。
- 每个任务先写失败测试，再写最小实现，再运行测试。交付前执行双重 QA：需求核对、自动化验证、独立审查、修复并复审，最多三轮。

## Scope Boundary

本计划仅交付可运行的基础框架。真实进程预检、EventPipe 分配追踪、.gcdump 采集、.gcdump/.nettrace 读取、类型排行、调用树和文件导入将作为后续独立的垂直切片计划：它们依赖此处的状态机和接口，但可分别验收，不能与框架正确性混在一次修改中。

## Planned File Structure

~~~
global.json
Directory.Build.props
.gitignore
DotnetAnalysis.sln
src/
  DotnetAnalysis.Core/
    Events/IApplicationEvent.cs
    Sessions/SessionId.cs
    Sessions/AnalysisSessionState.cs
    Sessions/AnalysisSession.cs
    Sessions/AnalysisSessionTransitionRules.cs
  DotnetAnalysis.Application/
    Contracts/ICaptureBackend.cs
    Contracts/IAnalysisService.cs
    Events/IEventBus.cs
    Events/EventSubscriptionOptions.cs
    Events/InProcessEventBus.cs
    Events/CaptureStarted.cs
    Events/CaptureProgressChanged.cs
    Events/AnalysisCompleted.cs
    Events/AnalysisCanceled.cs
    Events/AnalysisFailed.cs
    Events/ModuleFaulted.cs
    Sessions/AnalysisSessionCoordinator.cs
  DotnetAnalysis.Diagnostics/
    DependencyInjection/DiagnosticsServiceCollectionExtensions.cs
  DotnetAnalysis.Desktop/
    Infrastructure/ObservableObject.cs
    Infrastructure/RelayCommand.cs
    Infrastructure/AsyncRelayCommand.cs
    Infrastructure/IUiDispatcher.cs
    Infrastructure/WpfUiDispatcher.cs
    Composition/DesktopServiceCollectionExtensions.cs
    ViewModels/ShellViewModel.cs
    App.xaml
    App.xaml.cs
    MainWindow.xaml
tests/
  DotnetAnalysis.Tests/
    Core/AnalysisSessionTests.cs
    Application/InProcessEventBusTests.cs
    Application/AnalysisSessionCoordinatorTests.cs
    Desktop/ObservableObjectTests.cs
    Desktop/RelayCommandTests.cs
    Desktop/AsyncRelayCommandTests.cs
    Desktop/CompositionTests.cs
~~~

---

### Task 1: Initialize the repository and enforce the project reference boundary

**Files:**
- Create: global.json
- Create: Directory.Build.props
- Create: .gitignore
- Create: DotnetAnalysis.sln
- Create: src/DotnetAnalysis.Core/DotnetAnalysis.Core.csproj
- Create: src/DotnetAnalysis.Application/DotnetAnalysis.Application.csproj
- Create: src/DotnetAnalysis.Diagnostics/DotnetAnalysis.Diagnostics.csproj
- Create: src/DotnetAnalysis.Desktop/DotnetAnalysis.Desktop.csproj
- Create: tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj

**Interfaces:**
- Consumes: none.
- Produces: the solution and reference graph required by every subsequent task.

- [ ] **Step 1: Initialize Git and create the five projects**

Run:

~~~powershell
git init -b main
dotnet new sln --name DotnetAnalysis
New-Item -ItemType Directory -Force src, tests | Out-Null
dotnet new classlib --name DotnetAnalysis.Core --output src/DotnetAnalysis.Core --framework net10.0
dotnet new classlib --name DotnetAnalysis.Application --output src/DotnetAnalysis.Application --framework net10.0
dotnet new classlib --name DotnetAnalysis.Diagnostics --output src/DotnetAnalysis.Diagnostics --framework net10.0
dotnet new wpf --name DotnetAnalysis.Desktop --output src/DotnetAnalysis.Desktop --framework net10.0
dotnet new mstest --name DotnetAnalysis.Tests --output tests/DotnetAnalysis.Tests --framework net10.0
~~~

- [ ] **Step 2: Add projects and only the allowed references**

Run:

~~~powershell
dotnet sln DotnetAnalysis.sln add src/DotnetAnalysis.Core/DotnetAnalysis.Core.csproj
dotnet sln DotnetAnalysis.sln add src/DotnetAnalysis.Application/DotnetAnalysis.Application.csproj
dotnet sln DotnetAnalysis.sln add src/DotnetAnalysis.Diagnostics/DotnetAnalysis.Diagnostics.csproj
dotnet sln DotnetAnalysis.sln add src/DotnetAnalysis.Desktop/DotnetAnalysis.Desktop.csproj
dotnet sln DotnetAnalysis.sln add tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj
dotnet add src/DotnetAnalysis.Application/DotnetAnalysis.Application.csproj reference src/DotnetAnalysis.Core/DotnetAnalysis.Core.csproj
dotnet add src/DotnetAnalysis.Diagnostics/DotnetAnalysis.Diagnostics.csproj reference src/DotnetAnalysis.Core/DotnetAnalysis.Core.csproj src/DotnetAnalysis.Application/DotnetAnalysis.Application.csproj
dotnet add src/DotnetAnalysis.Desktop/DotnetAnalysis.Desktop.csproj reference src/DotnetAnalysis.Core/DotnetAnalysis.Core.csproj src/DotnetAnalysis.Application/DotnetAnalysis.Application.csproj
dotnet add tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj reference src/DotnetAnalysis.Core/DotnetAnalysis.Core.csproj src/DotnetAnalysis.Application/DotnetAnalysis.Application.csproj src/DotnetAnalysis.Desktop/DotnetAnalysis.Desktop.csproj
~~~

- [ ] **Step 3: Write the SDK and compiler policy**

Create global.json:

~~~json
{
  "sdk": {
    "version": "10.0.303",
    "rollForward": "latestPatch",
    "allowPrerelease": false
  }
}
~~~

Create Directory.Build.props:

~~~xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
~~~

Set the Desktop target to net10.0-windows and set UseWPF to true. Delete generated Class1.cs files and the generated MSTest sample file.

- [ ] **Step 4: Add ignored diagnostic output and verify the build fails before restore**

Create .gitignore:

~~~gitignore
.vs/
.worktrees/
bin/
obj/
TestResults/
*.user
*.suo
*.gcdump
*.nettrace
*.dmp
~~~

Run:

~~~powershell
dotnet build DotnetAnalysis.sln --configuration Debug --no-restore
~~~

Expected: fail because restore has not occurred.

- [ ] **Step 5: Restore, build, and prove the test host starts**

Run:

~~~powershell
dotnet restore DotnetAnalysis.sln
dotnet build DotnetAnalysis.sln --configuration Debug --no-restore
dotnet test tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj --configuration Debug --no-restore
dotnet list DotnetAnalysis.sln reference
~~~

Expected: zero warnings/errors; Desktop lists Core and Application only, never Diagnostics.

- [ ] **Step 6: Inspect and commit the framework bootstrap**

Run:

~~~powershell
git status --short
git diff --check
git add global.json Directory.Build.props .gitignore DotnetAnalysis.sln src tests docs
git commit -m "chore: initialize dotnet analysis solution"
~~~

Expected: no build output is staged.

---

### Task 2: Add the Core session model and enforce its state machine

**Files:**
- Create: src/DotnetAnalysis.Core/Sessions/SessionId.cs
- Create: src/DotnetAnalysis.Core/Sessions/AnalysisSessionState.cs
- Create: src/DotnetAnalysis.Core/Sessions/AnalysisSession.cs
- Create: src/DotnetAnalysis.Core/Sessions/AnalysisSessionTransitionRules.cs
- Create: src/DotnetAnalysis.Core/Events/IApplicationEvent.cs
- Test: tests/DotnetAnalysis.Tests/Core/AnalysisSessionTests.cs

**Interfaces:**
- Consumes: Core project from Task 1.
- Produces: SessionId, AnalysisSessionState, AnalysisSession, AnalysisSessionTransitionRules, and IApplicationEvent.

- [ ] **Step 1: Write failing state-transition tests**

~~~csharp
[TestClass]
public sealed class AnalysisSessionTests
{
    [TestMethod]
    public void Created_CanMoveToPreflighting()
    {
        var session = AnalysisSession.Create(SessionId.New(), DateTimeOffset.UtcNow);

        Assert.IsTrue(session.TryMoveTo(AnalysisSessionState.Preflighting));
        Assert.AreEqual(AnalysisSessionState.Preflighting, session.State);
    }

    [TestMethod]
    public void Created_CannotMoveDirectlyToCompleted()
    {
        var session = AnalysisSession.Create(SessionId.New(), DateTimeOffset.UtcNow);

        Assert.IsFalse(session.TryMoveTo(AnalysisSessionState.Completed));
        Assert.AreEqual(AnalysisSessionState.Created, session.State);
    }

    [TestMethod]
    public void ActiveState_CanMoveToCancelingThenCanceled()
    {
        var session = AnalysisSession.Create(SessionId.New(), DateTimeOffset.UtcNow);
        Assert.IsTrue(session.TryMoveTo(AnalysisSessionState.Preflighting));

        Assert.IsTrue(session.TryMoveTo(AnalysisSessionState.Canceling));
        Assert.IsTrue(session.TryMoveTo(AnalysisSessionState.Canceled));
    }
}
~~~

- [ ] **Step 2: Run the test and verify the initial failure**

Run:

~~~powershell
dotnet test tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj --filter FullyQualifiedName~AnalysisSessionTests --no-restore
~~~

Expected: fail because the session types do not exist.

- [ ] **Step 3: Implement the value types and transition table**

Create this public surface:

~~~csharp
public readonly record struct SessionId(Guid Value)
{
    public static SessionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public enum AnalysisSessionState
{
    Created,
    Preflighting,
    CapturingAllocations,
    FinishingTrace,
    CapturingHeapSnapshot,
    Analyzing,
    Completed,
    Canceling,
    Canceled,
    Failed
}

public interface IApplicationEvent
{
    DateTimeOffset OccurredAt { get; }
    SessionId? SessionId { get; }
    string Source { get; }
}
~~~

Implement AnalysisSessionTransitionRules.CanMove(from, to). Permit only this normal sequence:

~~~text
Created -> Preflighting -> CapturingAllocations -> FinishingTrace
FinishingTrace -> CapturingHeapSnapshot -> Analyzing -> Completed
~~~

Every nonterminal state may transition to Canceling or Failed. Canceling may transition only to Canceled. Completed, Canceled, and Failed are terminal. AnalysisSession.TryMoveTo uses this table and changes state only after a valid transition.

- [ ] **Step 4: Add terminal-state regression tests**

Add tests that Completed, Canceled, and Failed reject every next state, and that Canceling rejects Completed.

- [ ] **Step 5: Run focused tests and review the state names**

Run:

~~~powershell
dotnet test tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj --filter FullyQualifiedName~AnalysisSessionTests --no-restore
dotnet build src/DotnetAnalysis.Core/DotnetAnalysis.Core.csproj --no-restore
~~~

Expected: all tests pass with zero warnings/errors.

- [ ] **Step 6: Commit the Core model**

Review that the enum uses FinishingTrace, not FinishedTrace, and that terminal sessions cannot restart. Then run:

~~~powershell
git diff --check
git add src/DotnetAnalysis.Core tests/DotnetAnalysis.Tests/Core
git commit -m "feat: add analysis session state model"
~~~

---

### Task 3: Implement the self-owned typed in-process event bus

**Files:**
- Create: src/DotnetAnalysis.Application/Events/IEventBus.cs
- Create: src/DotnetAnalysis.Application/Events/EventSubscriptionOptions.cs
- Create: src/DotnetAnalysis.Application/Events/InProcessEventBus.cs
- Create: src/DotnetAnalysis.Application/Events/CaptureStarted.cs
- Create: src/DotnetAnalysis.Application/Events/CaptureProgressChanged.cs
- Create: src/DotnetAnalysis.Application/Events/ModuleFaulted.cs
- Test: tests/DotnetAnalysis.Tests/Application/InProcessEventBusTests.cs

**Interfaces:**
- Consumes: IApplicationEvent and SessionId from Task 2.
- Produces: IEventBus for Task 4 and the Desktop layer.

- [ ] **Step 1: Write failing lifecycle delivery and disposal tests**

~~~csharp
[TestMethod]
public async Task PublishAsync_DeliversLifecycleEventToMatchingSubscriber()
{
    await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
    var received = new TaskCompletionSource<CaptureStarted>();
    using var subscription = bus.Subscribe<CaptureStarted>((@event, _) =>
    {
        received.TrySetResult(@event);
        return ValueTask.CompletedTask;
    });

    var expected = new CaptureStarted(SessionId.New(), DateTimeOffset.UtcNow, "Coordinator");
    await bus.PublishAsync(expected, CancellationToken.None);

    Assert.AreEqual(expected, await received.Task.WaitAsync(TimeSpan.FromSeconds(1)));
}

[TestMethod]
public async Task DisposedSubscription_DoesNotReceiveSubsequentEvents()
{
    await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
    var calls = 0;
    var subscription = bus.Subscribe<CaptureStarted>((_, _) =>
    {
        calls++;
        return ValueTask.CompletedTask;
    });

    subscription.Dispose();
    await bus.PublishAsync(new CaptureStarted(SessionId.New(), DateTimeOffset.UtcNow, "Coordinator"), CancellationToken.None);
    await Task.Delay(50);

    Assert.AreEqual(0, calls);
}
~~~

- [ ] **Step 2: Run the event-bus tests and verify failure**

Run:

~~~powershell
dotnet test tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj --filter FullyQualifiedName~InProcessEventBusTests --no-restore
~~~

Expected: fail because IEventBus, InProcessEventBus, and event records do not exist.

- [ ] **Step 3: Define the event bus contract and event records**

~~~csharp
public interface IEventBus : IAsyncDisposable
{
    ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken)
        where TEvent : IApplicationEvent;

    IDisposable Subscribe<TEvent>(
        Func<TEvent, CancellationToken, ValueTask> handler,
        EventSubscriptionOptions? options = null)
        where TEvent : IApplicationEvent;
}
~~~

EventSubscriptionOptions defaults QueueCapacity to 64 and CoalesceProgressEvents to true. Add immutable record events:

~~~csharp
public sealed record CaptureStarted(SessionId SessionId, DateTimeOffset OccurredAt, string Source) : IApplicationEvent;
public sealed record CaptureProgressChanged(SessionId SessionId, int Percent, DateTimeOffset OccurredAt, string Source) : IApplicationEvent;
public sealed record ModuleFaulted(SessionId? SessionId, string Module, string Message, DateTimeOffset OccurredAt, string Source) : IApplicationEvent;
~~~

- [ ] **Step 4: Implement independent bounded subscription queues**

InProcessEventBus owns subscription objects, not a static global registry. Each subscription owns a bounded Channel and one consumer task. PublishAsync takes a snapshot of matching subscriptions and enqueues independently. CaptureStarted and ModuleFaulted must never coalesce.

A handler exception is logged and causes one ModuleFaulted notification. If the handler that failed was processing ModuleFaulted, log only; do not recursively publish another ModuleFaulted.

- [ ] **Step 5: Write the failing slow-subscriber progress test**

~~~csharp
[TestMethod]
public async Task PublishAsync_CoalescesProgressToLatestValueForSlowSubscriber()
{
    await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
    var gate = new TaskCompletionSource();
    var received = new List<int>();
    using var subscription = bus.Subscribe<CaptureProgressChanged>(async (@event, _) =>
    {
        await gate.Task;
        received.Add(@event.Percent);
    });

    var sessionId = SessionId.New();
    for (var percent = 1; percent <= 100; percent++)
    {
        await bus.PublishAsync(new CaptureProgressChanged(sessionId, percent, DateTimeOffset.UtcNow, "Capture"), CancellationToken.None);
    }

    gate.SetResult();
    await Task.Delay(100);
    Assert.AreEqual(100, received[^1]);
}
~~~

Run the focused test and verify it fails before adding progress replacement.

- [ ] **Step 6: Implement progress replacement and fault isolation**

While a subscription is busy, retain only the newest CaptureProgressChanged for that SessionId. On disposal, remove the subscription, complete its channel, and ensure the bus DisposeAsync waits for each consumer task.

Add a test with one throwing CaptureStarted subscriber and one healthy subscriber. Assert the healthy subscriber still receives CaptureStarted.

- [ ] **Step 7: Run focused tests and commit**

Run:

~~~powershell
dotnet test tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj --filter FullyQualifiedName~InProcessEventBusTests --no-restore
rg -n -i "static.*IEventBus|static.*EventBus|string.*Topic|CommunityToolkit|Prism|ReactiveUI|MediatR" src tests
git diff --check
git add src/DotnetAnalysis.Application/Events tests/DotnetAnalysis.Tests/Application/InProcessEventBusTests.cs
git commit -m "feat: add typed in-process event bus"
~~~

Expected: tests pass and the search has no matches.

---

### Task 4: Add application contracts and the serialized session coordinator

**Files:**
- Create: src/DotnetAnalysis.Application/Contracts/ICaptureBackend.cs
- Create: src/DotnetAnalysis.Application/Contracts/IAnalysisService.cs
- Create: src/DotnetAnalysis.Application/Sessions/AnalysisSessionCoordinator.cs
- Create: src/DotnetAnalysis.Application/Events/AnalysisCompleted.cs
- Create: src/DotnetAnalysis.Application/Events/AnalysisCanceled.cs
- Create: src/DotnetAnalysis.Application/Events/AnalysisFailed.cs
- Test: tests/DotnetAnalysis.Tests/Application/AnalysisSessionCoordinatorTests.cs

**Interfaces:**
- Consumes: session model from Task 2 and IEventBus from Task 3.
- Produces: AnalysisSessionCoordinator and the contracts implemented by the later Diagnostics vertical slice.

- [ ] **Step 1: Write the failing normal completion test**

~~~csharp
[TestMethod]
public async Task FinishAsync_StopsTraceBeforeSnapshotBeforeAnalysis()
{
    var calls = new List<string>();
    var backend = new RecordingCaptureBackend(calls);
    var analyzer = new RecordingAnalysisService(calls);
    await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
    var coordinator = new AnalysisSessionCoordinator(backend, analyzer, bus, TimeProvider.System);

    var session = await coordinator.StartAsync(CancellationToken.None);
    await coordinator.FinishAsync(session.Id, CancellationToken.None);

    CollectionAssert.AreEqual(new[] { "start", "stop", "snapshot", "analyze" }, calls);
    Assert.AreEqual(AnalysisSessionState.Completed, session.State);
}
~~~

- [ ] **Step 2: Run the coordinator test and verify failure**

Run:

~~~powershell
dotnet test tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj --filter FullyQualifiedName~AnalysisSessionCoordinatorTests --no-restore
~~~

Expected: fail because coordinator and contracts do not exist.

- [ ] **Step 3: Define capture and analysis contracts**

~~~csharp
public interface ICaptureBackend
{
    Task StartAllocationTraceAsync(AnalysisSession session, CancellationToken cancellationToken);
    Task StopAllocationTraceAsync(AnalysisSession session, CancellationToken cancellationToken);
    Task CaptureHeapSnapshotAsync(AnalysisSession session, CancellationToken cancellationToken);
    Task CancelAsync(AnalysisSession session, CancellationToken cancellationToken);
}

public interface IAnalysisService
{
    Task AnalyzeAsync(AnalysisSession session, CancellationToken cancellationToken);
}
~~~

Create immutable AnalysisCompleted, AnalysisCanceled, and AnalysisFailed records. AnalysisFailed carries a stable user-facing failure code and message, never a raw exception.

- [ ] **Step 4: Implement the normal completion order**

AnalysisSessionCoordinator keeps sessions indexed by SessionId and serializes each session's operations. StartAsync creates a session, moves Created to Preflighting to CapturingAllocations, calls StartAllocationTraceAsync, then publishes CaptureStarted.

FinishAsync rejects unknown sessions and states other than CapturingAllocations. It executes exactly this order:

~~~text
FinishingTrace -> StopAllocationTraceAsync
CapturingHeapSnapshot -> CaptureHeapSnapshotAsync
Analyzing -> AnalyzeAsync
Completed -> publish AnalysisCompleted
~~~

If a non-cancellation exception occurs, move to Failed, publish AnalysisFailed, and do not start the next stage.

- [ ] **Step 5: Write and run the failing cancellation regression test**

~~~csharp
[TestMethod]
public async Task CancelAsync_DoesNotCaptureSnapshotOrAnalyze()
{
    var calls = new List<string>();
    await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
    var coordinator = new AnalysisSessionCoordinator(
        new RecordingCaptureBackend(calls),
        new RecordingAnalysisService(calls),
        bus,
        TimeProvider.System);

    var session = await coordinator.StartAsync(CancellationToken.None);
    await coordinator.CancelAsync(session.Id, CancellationToken.None);

    CollectionAssert.AreEqual(new[] { "start", "cancel" }, calls);
    Assert.AreEqual(AnalysisSessionState.Canceled, session.State);
}
~~~

Run the focused test and verify it fails before implementing CancelAsync.

- [ ] **Step 6: Implement cancellation and duplicate-request guards**

CancelAsync accepts nonterminal sessions, moves to Canceling, invokes ICaptureBackend.CancelAsync, moves to Canceled, and publishes AnalysisCanceled. It never calls CaptureHeapSnapshotAsync or AnalyzeAsync.

Repeated FinishAsync or CancelAsync while the same session operation is active returns the same in-flight Task and never invokes backend methods twice.

- [ ] **Step 7: Add failure coverage, validate, and commit**

Add a backend fake that throws from StopAllocationTraceAsync. Assert snapshot and analyzer are not called, state becomes Failed, and AnalysisFailed is published.

Run:

~~~powershell
dotnet test tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj --filter FullyQualifiedName~AnalysisSessionCoordinatorTests --no-restore
git diff --check
git add src/DotnetAnalysis.Application/Contracts src/DotnetAnalysis.Application/Sessions src/DotnetAnalysis.Application/Events tests/DotnetAnalysis.Tests/Application/AnalysisSessionCoordinatorTests.cs
git commit -m "feat: add analysis session coordinator"
~~~

---

### Task 5: Add self-owned WPF MVVM primitives and compose the shell

**Files:**
- Create: src/DotnetAnalysis.Desktop/Infrastructure/ObservableObject.cs
- Create: src/DotnetAnalysis.Desktop/Infrastructure/RelayCommand.cs
- Create: src/DotnetAnalysis.Desktop/Infrastructure/AsyncRelayCommand.cs
- Create: src/DotnetAnalysis.Desktop/Infrastructure/IUiDispatcher.cs
- Create: src/DotnetAnalysis.Desktop/Infrastructure/WpfUiDispatcher.cs
- Create: src/DotnetAnalysis.Desktop/Composition/DesktopServiceCollectionExtensions.cs
- Create: src/DotnetAnalysis.Desktop/ViewModels/ShellViewModel.cs
- Create: src/DotnetAnalysis.Diagnostics/DependencyInjection/DiagnosticsServiceCollectionExtensions.cs
- Modify: src/DotnetAnalysis.Desktop/App.xaml
- Modify: src/DotnetAnalysis.Desktop/App.xaml.cs
- Modify: src/DotnetAnalysis.Desktop/MainWindow.xaml
- Test: tests/DotnetAnalysis.Tests/Desktop/ObservableObjectTests.cs
- Test: tests/DotnetAnalysis.Tests/Desktop/RelayCommandTests.cs
- Test: tests/DotnetAnalysis.Tests/Desktop/AsyncRelayCommandTests.cs
- Test: tests/DotnetAnalysis.Tests/Desktop/CompositionTests.cs

**Interfaces:**
- Consumes: IEventBus and AnalysisSessionCoordinator from Tasks 3–4.
- Produces: a WPF application that starts, resolves services, and disposes the event bus on shutdown without diagnostics coupling.

- [ ] **Step 1: Write failing ObservableObject and RelayCommand tests**

~~~csharp
[TestMethod]
public void SetProperty_WhenValueChanges_RaisesPropertyChangedOnce()
{
    var viewModel = new TestViewModel();
    var names = new List<string?>();
    viewModel.PropertyChanged += (_, args) => names.Add(args.PropertyName);

    viewModel.Name = "heap";
    viewModel.Name = "heap";

    CollectionAssert.AreEqual(new[] { "Name" }, names);
}

[TestMethod]
public void RelayCommand_UsesCanExecutePredicate()
{
    var enabled = false;
    var calls = 0;
    var command = new RelayCommand(() => calls++, () => enabled);

    Assert.IsFalse(command.CanExecute(null));
    enabled = true;
    command.NotifyCanExecuteChanged();
    Assert.IsTrue(command.CanExecute(null));
    command.Execute(null);

    Assert.AreEqual(1, calls);
}
~~~

- [ ] **Step 2: Run the MVVM tests and verify failure**

Run:

~~~powershell
dotnet test tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj --filter "FullyQualifiedName~ObservableObjectTests|FullyQualifiedName~RelayCommandTests|FullyQualifiedName~AsyncRelayCommandTests" --no-restore
~~~

Expected: fail because the MVVM types do not exist.

- [ ] **Step 3: Implement ObservableObject and RelayCommand**

~~~csharp
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
~~~

RelayCommand takes Action execute and optional Func<bool> canExecute, implements ICommand, and exposes NotifyCanExecuteChanged. It uses no reflection and no generic message service.

- [ ] **Step 4: Write the failing async-command cancellation test**

~~~csharp
[TestMethod]
public async Task AsyncRelayCommand_Cancel_CancelsCurrentExecutionAndRestoresState()
{
    var entered = new TaskCompletionSource();
    var command = new AsyncRelayCommand(async token =>
    {
        entered.SetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
    });

    command.Execute(null);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
    Assert.IsTrue(command.IsRunning);
    Assert.IsTrue(command.CanCancel);

    command.Cancel();
    await command.ExecutionTask!;

    Assert.IsFalse(command.IsRunning);
    Assert.IsFalse(command.CanCancel);
}
~~~

Run this test and verify failure before implementation.

- [ ] **Step 5: Implement AsyncRelayCommand and the dispatcher boundary**

AsyncRelayCommand accepts Func<CancellationToken, Task>, exposes IsRunning, CanCancel, ExecutionTask, LastError, Cancel(), and NotifyCanExecuteChanged(). It treats cancellation from its own token as normal completion; any other exception sets LastError and raises ExecutionFailed.

Create:

~~~csharp
public interface IUiDispatcher
{
    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
}
~~~

WpfUiDispatcher wraps Application.Current.Dispatcher.InvokeAsync. No Application type references Dispatcher.

- [ ] **Step 6: Write the failing composition-root test**

~~~csharp
[TestMethod]
public void AddDesktopApplication_ResolvesShellAndSingleEventBus()
{
    var services = new ServiceCollection();
    services.AddDesktopApplication();
    using var provider = services.BuildServiceProvider(validateScopes: true);

    var shell = provider.GetRequiredService<ShellViewModel>();
    var firstBus = provider.GetRequiredService<IEventBus>();
    var secondBus = provider.GetRequiredService<IEventBus>();

    Assert.IsNotNull(shell);
    Assert.AreSame(firstBus, secondBus);
    Assert.AreEqual("尚未开始分析", shell.StatusText);
}
~~~

Run it and verify failure before the registration extension is written.

- [ ] **Step 7: Compose the application shell**

DesktopServiceCollectionExtensions.AddDesktopApplication registers InProcessEventBus, AnalysisSessionCoordinator, IUiDispatcher, and ShellViewModel as singletons.

ShellViewModel inherits ObservableObject. StatusText starts as 尚未开始分析. Its event handlers use IUiDispatcher.InvokeAsync before setting bindable state.

DiagnosticsServiceCollectionExtensions.AddDiagnosticsContracts returns IServiceCollection without registering a fake collector; it establishes the only allowed future registration location.

App.OnStartup builds the provider, resolves MainWindow and ShellViewModel, sets DataContext, and shows the window. Store the provider in a private field. App.OnExit is synchronous, so it must call `_serviceProvider.DisposeAsync().AsTask().GetAwaiter().GetResult()` before `base.OnExit(e)`; this completes event-bus consumers without fire-and-forget disposal. MainWindow title is .NET 内存分析 and displays StatusText. Do not add nonfunctional capture controls.

- [ ] **Step 8: Run tests, smoke test the window, inspect boundaries, and commit**

Run:

~~~powershell
dotnet test tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj --filter "FullyQualifiedName~ObservableObjectTests|FullyQualifiedName~RelayCommandTests|FullyQualifiedName~AsyncRelayCommandTests|FullyQualifiedName~CompositionTests" --no-restore
dotnet build DotnetAnalysis.sln --configuration Debug --no-restore
dotnet list src/DotnetAnalysis.Desktop/DotnetAnalysis.Desktop.csproj reference
rg -n "DotnetAnalysis.Diagnostics|CommunityToolkit|Prism|ReactiveUI|MediatR" src/DotnetAnalysis.Desktop tests/DotnetAnalysis.Tests
~~~

Launch src/DotnetAnalysis.Desktop/bin/Debug/net10.0-windows/DotnetAnalysis.Desktop.exe. Expected: a window titled .NET 内存分析 displays 尚未开始分析 and closes without an unhandled exception.

Then run:

~~~powershell
git diff --check
git add src/DotnetAnalysis.Desktop src/DotnetAnalysis.Diagnostics/DependencyInjection tests/DotnetAnalysis.Tests/Desktop
git commit -m "feat: add desktop application shell"
~~~

---

### Task 6: Run the foundation quality gates and prepare the next vertical slice

**Files:**
- Modify: docs/superpowers/plans/2026-09-02-dotnet-memory-analysis-foundation.md by checking completed steps during execution.
- Modify: docs/superpowers/specs/2026-09-02-dotnet-memory-analysis-design.md only when verified implementation evidence requires a design correction.

**Interfaces:**
- Consumes: all projects from Tasks 1–5.
- Produces: a clean, verified baseline for the CoreCLR process-discovery and EventPipe capture plan.

- [ ] **Step 1: Run all automated validation**

~~~powershell
dotnet restore DotnetAnalysis.sln
dotnet build DotnetAnalysis.sln --configuration Debug --no-restore
dotnet test DotnetAnalysis.sln --configuration Debug --no-restore
dotnet format DotnetAnalysis.sln --verify-no-changes --no-restore
~~~

Expected: every command exits 0; build and tests report zero warnings/errors.

- [ ] **Step 2: Perform requirement QA**

Verify each item with source and test evidence:

~~~text
[ ] SDK pin is 10.0.303 and .NET 11 is not selected.
[ ] Desktop references Application and Core only.
[ ] Core references no WPF, diagnostics, I/O, or DI package.
[ ] There is no third-party MVVM or event-bus package.
[ ] The event bus is strong-typed, instance-owned, asynchronous, bounded, and releases subscriptions.
[ ] Progress may coalesce; terminal events cannot silently disappear.
[ ] Finish order is stop trace, snapshot, analyze.
[ ] Cancellation never creates a snapshot or analysis result.
[ ] Application start and close leave no event-bus consumer task running.
~~~

- [ ] **Step 3: Perform independent implementation QA**

Run:

~~~powershell
git diff main -- src tests
rg -n -i "CommunityToolkit|Prism|ReactiveUI|MediatR|static.*EventBus|static.*IEventBus|dotnet-gcdump|dotnet-trace" src tests
dotnet list DotnetAnalysis.sln package --include-transitive
~~~

Expected: no forbidden runtime dependency, no global event bus, no CLI wrapper, and no Desktop-to-Diagnostics reference.

- [ ] **Step 4: Repair every actionable finding and repeat validation**

For every finding: add a regression test, run it to observe failure, apply the smallest repair, rerun the focused test, then rerun Steps 1–3. Repeat no more than three review/repair loops. If a check is blocked by the machine, record the exact command and observed blocker in the delivery note.

- [ ] **Step 5: Confirm repository state**

Run:

~~~powershell
git status --short
git diff --check
git log --oneline -6
~~~

Expected: worktree is clean after task commits. Do not make a no-op quality-only commit.

## Plan Self-Review

### Spec coverage

| Approved requirement | Plan task |
| --- | --- |
| .NET 10, WPF, self-owned MVVM | Tasks 1 and 5 |
| Core/Application/Diagnostics/Desktop boundaries | Tasks 1, 2, 4 and 5 |
| Strongly typed self-owned event bus | Task 3 |
| Queue capacity, progress coalescing, lifecycle, fault isolation | Task 3 |
| Session state machine and finish/cancel semantics | Tasks 2 and 4 |
| UI-thread boundary and composition root | Task 5 |
| No direct Desktop-to-Diagnostics reference | Tasks 1, 5 and 6 |
| Automated validation and independent QA | Every task and Task 6 |

The real diagnostic collector, file readers, result analysis, type-ranking UI, and call-tree UI are intentionally excluded from this foundation plan. They depend on these interfaces and will be delivered through independent vertical-slice plans.

### Placeholder scan

The plan contains no unassigned implementation markers. Every task names files, interfaces, tests, commands, state transitions, and acceptance criteria.

### Type consistency

SessionId, AnalysisSession, AnalysisSessionState, IApplicationEvent, IEventBus, ICaptureBackend, IAnalysisService, and AnalysisSessionCoordinator are introduced before subsequent tasks consume them. FinishAsync, CancelAsync, and each event record retain the same name throughout the plan.

## Execution Handoff

Plan complete and saved to docs/superpowers/plans/2026-09-02-dotnet-memory-analysis-foundation.md. Two execution options:

1. **Subagent-Driven (recommended)** - I dispatch a fresh subagent per task, review between tasks, fast iteration.

2. **Inline Execution** - Execute tasks in this session using executing-plans, batch execution with checkpoints.

Which approach?
