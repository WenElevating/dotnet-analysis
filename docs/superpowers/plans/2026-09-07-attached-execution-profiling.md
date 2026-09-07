# 附着会话执行采样诊断层 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为已附着的 Windows x64 .NET 8/9/10 CoreCLR 进程提供全会话执行采样、任意时间范围调用树/热点查询和本地 PDB 源码定位，并以 MQTTnet.TestApp 完成真实应用验收。

**Architecture:** `Core` 提供不可变查询模型，`Application` 仅扩展 `IProcessDiagnosticsSession` 显式查询契约；`Diagnostics` 以独立 EventPipe Sample Profiler 会话写入私有追加式压缩存储，在查询阶段聚合调用树并按需解析本地符号。执行采样与现有内存时间线、分配采样及 `.gcdump` 捕获并列，互不改变彼此的失败语义或数据模型。

**Tech Stack:** .NET 10/C# latest、Microsoft.Diagnostics.NETCore.Client 0.2.661903、Microsoft.Diagnostics.Tracing.TraceEvent 3.2.6、EventPipe、TraceEvent 本地 PDB 符号读取、MSTest 4、Windows x64。

**Spec:** `docs/superpowers/specs/2026-09-07-attached-execution-profiling-design.md`

## Global Constraints

- 保持依赖方向 `Core <- Application <- Diagnostics`；不改 Desktop/UI，Application/Desktop 不得看到 EventPipe、TraceEvent、PDB 读取器或会话临时目录。
- 仅支持 Windows x64 .NET 8/.NET 9/.NET 10 CoreCLR 已运行进程；复用既有 PID 与启动时间验证。
- 执行采样从附着成功完整保留到会话结束，不使用滑动窗口；会话结束后排空、等待查询并清理私有数据，不支持重开或导入。
- 不伪造源码位置：只有本地匹配 PDB 和存在的源码文件可解析时才返回 `SourceLocation`；禁止联网 Source Link、符号服务器和源码下载。
- 采样消费线程只做栈规范化、去重和追加；PDB 解析、调用树合并及范围查询不能运行在该线程。
- 新增公开类型、枚举值、接口成员及异常语义必须包含中文 XML 文档；分析器警告即错误。
- 必须完成单元、Windows 集成、60 分钟性能、2 小时压力和 MQTTnet 真实应用验收；测试目标不能替代真实应用验收。
- 每个实施任务均先写失败测试、再最小实现、再运行受影响测试、再提交；交付前执行两轮独立 QA，最多修复并复审三轮。

## 文件结构与职责

| 路径 | 责任 |
| --- | --- |
| `src/DotnetAnalysis.Core/Diagnostics/ExecutionTimeRange.cs` | 半开 UTC 查询范围及参数不变量。 |
| `src/DotnetAnalysis.Core/Diagnostics/SourceLocation.cs` | 已验证的本地源码文件、行与可选列。 |
| `src/DotnetAnalysis.Core/Diagnostics/ExecutionFrame.cs` | 对上层可见的方法、模块与可选源码位置。 |
| `src/DotnetAnalysis.Core/Diagnostics/ExecutionHotspot.cs` | 帧级包含/独占样本计数。 |
| `src/DotnetAnalysis.Core/Diagnostics/ExecutionCallTreeNode.cs` | 不可变自顶向下调用树节点。 |
| `src/DotnetAnalysis.Core/Diagnostics/ExecutionProfile.cs` | 一个范围内的样本数、累计丢失数、热点和调用树。 |
| `src/DotnetAnalysis.Diagnostics/Windows/ExecutionCaptureStore.cs` | 会话私有的帧表、栈表、分段记录、读取边界和删除。 |
| `src/DotnetAnalysis.Diagnostics/Windows/ExecutionProfileBuilder.cs` | 流式范围聚合、热点排序与调用树构建。 |
| `src/DotnetAnalysis.Diagnostics/Windows/EventPipeExecutionSampler.cs` | EventPipe Sample Profiler 生命周期、栈规范化和丢失事件计数。 |
| `src/DotnetAnalysis.Diagnostics/Windows/ExecutionSamplingSession.cs` | 存储、采样、查询、取消与会话结束协调。 |
| `src/DotnetAnalysis.Diagnostics/Windows/ExecutionSymbolResolver.cs` | 本地 PDB 单飞解析和 `SourceLocation` 缓存。 |
| `tests/DotnetAnalysis.Diagnostics.TestTarget/ExecutionSamplingWorkload.cs` | .NET 8/9/10 受控目标的可定位 CPU 工作负载。 |
| `tests/DotnetAnalysis.Diagnostics.IntegrationTests/ExecutionSamplingCompatibilityProbe.cs` | 不依赖公开契约的最小 EventPipe Sample Profiler 读取器。 |
| `tests/DotnetAnalysis.Diagnostics.IntegrationTests/ExecutionSamplingCompatibilityProbeTests.cs` | 公开契约前的 EventPipe/栈/PDB 兼容性探针。 |
| `tests/DotnetAnalysis.Diagnostics.IntegrationTests/ExecutionSamplingIntegrationTests.cs` | Windows .NET 8/9/10 附着、查询、符号、快照并发集成测试。 |
| `tests/DotnetAnalysis.Diagnostics.IntegrationTests/ExecutionSamplingPerformanceTests.cs` | 60 分钟基准、2 小时压力及 JSON 原始证据。 |
| `tests/DotnetAnalysis.Diagnostics.IntegrationTests/ExecutionSamplingRealApplicationAcceptanceTests.cs` | 已运行 MQTTnet.TestApp 的 10 分钟真实附着验收。 |

---

### Task 1: 建立执行采样兼容性探针和受控 CPU 工作负载

**Files:**
- Create: `tests/DotnetAnalysis.Diagnostics.TestTarget/ExecutionSamplingWorkload.cs`
- Modify: `tests/DotnetAnalysis.Diagnostics.TestTarget/Program.cs`
- Create: `tests/DotnetAnalysis.Diagnostics.IntegrationTests/ExecutionSamplingCompatibilityProbe.cs`
- Create: `tests/DotnetAnalysis.Diagnostics.IntegrationTests/ExecutionSamplingCompatibilityProbeTests.cs`
- Modify: `tests/DotnetAnalysis.Diagnostics.IntegrationTests/IntegrationTestHost.cs`

**Interfaces:**
- Consumes: `IntegrationTestHost.StartTargetAsync(string, int?)`、`DiagnosticsClient.StartEventPipeSession`、`TraceLog.CreateFromEventPipeSession`。
- Produces:

  ```csharp
  internal sealed record IntegrationTargetOptions(
      int? InitialObjectCount = null,
      bool EnableExecutionWorkload = false,
      bool CopyWithoutPdb = false);

  internal sealed record ExecutionSamplingProbeResult(
      long ReceivedSampleCount,
      long EventsLost,
      IReadOnlyList<string> ManagedMethodNames);

  internal static class ExecutionSamplingCompatibilityProbe
  {
      public static Task<ExecutionSamplingProbeResult> CollectAsync(
          int processId,
          TimeSpan duration,
          CancellationToken cancellationToken);
  }
  ```

  以及环境变量 `DOTNET_ANALYSIS_TEST_EXECUTION_WORKLOAD` 和 `TestResults/ExecutionSampling-<timestamp>/compatibility-probe.json`。

- [ ] **Step 1: 写出受控工作负载的失败集成测试。**

  新测试必须对每个已安装的 `net8.0`、`net9.0`、`net10.0` 目标启动 `EnableExecutionWorkload = true`，并在 15 秒内断言至少接到一条 Sample Profiler 事件、至少一条事件有托管调用栈、栈中出现 `ExecutionSamplingWorkload`，且 `EventsLost == 0`。

  ```csharp
  [TestMethod]
  [TestCategory("WindowsDiagnosticsIntegration")]
  public async Task SampleProfiler_OnSupportedTarget_ProducesManagedStackAndNoLostEvents()
  {
      await using var target = await IntegrationTestHost.StartTargetAsync(
          "net10.0",
          new IntegrationTargetOptions(enableExecutionWorkload: true));

      var result = await ExecutionSamplingCompatibilityProbe.CollectAsync(
          target.ProcessId,
          TimeSpan.FromSeconds(10),
          CancellationToken.None);

      Assert.IsGreaterThan(0L, result.ReceivedSampleCount);
      Assert.IsTrue(result.ManagedMethodNames.Any(name => name.Contains("ExecutionSamplingWorkload", StringComparison.Ordinal)));
      Assert.AreEqual(0L, result.EventsLost);
  }
  ```

- [ ] **Step 2: 运行探针测试并确认当前没有实现。**

  Run:

  ```powershell
  dotnet test .\tests\DotnetAnalysis.Diagnostics.IntegrationTests\DotnetAnalysis.Diagnostics.IntegrationTests.csproj --configuration Debug --filter "FullyQualifiedName~ExecutionSamplingCompatibilityProbe"
  ```

  Expected: 编译失败，提示 `IntegrationTargetOptions` 和 `ExecutionSamplingCompatibilityProbe` 不存在。

- [ ] **Step 3: 实现可重复、可定位且不会被内联的受控负载。**

  `ExecutionSamplingWorkload.Start()` 创建 8 个长运行任务；每个任务循环执行 200 条固定调用路径。路径入口和叶子方法均加 `[MethodImpl(MethodImplOptions.NoInlining)]`，方法命名为 `Path000` 到 `Path199`，每条路径调用共同的非内联 `Consume(int)`，以 `Volatile.Write` 发布累计完成量。`Start()` 返回 `CancellationTokenSource` 所拥有的任务，`StopAsync()` 取消并等待全部 8 个任务完成。

  `Program.cs` 在输出 `READY` 前读取 `DOTNET_ANALYSIS_TEST_EXECUTION_WORKLOAD`；值为 `true` 时启动负载，收到 `EXIT` 后先停止负载再退出。`IntegrationTargetOptions` 在原有对象数参数之外传递该环境变量，`IntegrationTestHost.DisposeAsync()` 仍只发送 `EXIT`，不改变现有目标的退出协议。

- [ ] **Step 4: 以独立探针读取 Sample Profiler。**

  探针只位于测试项目，不依赖将要新增的公开契约。它以如下 providers 启动一个独立 EventPipe session：

  ```csharp
  var providers = new[]
  {
      new EventPipeProvider(
          ClrTraceEventParser.ProviderName,
          EventLevel.Informational,
          (long)ClrTraceEventParser.Keywords.Default),
      new EventPipeProvider(
          SampleProfilerTraceEventParser.ProviderName,
          EventLevel.Informational)
  };
  ```

  使用 `TraceLog.CreateFromEventPipeSession(session, TraceLog.EventPipeRundownConfiguration.Enable(client))`，再创建 `new SampleProfilerTraceEventParser(source)` 并订阅其强类型 `ThreadSample` 事件。`ClrThreadSampleTraceData` 继承 `TraceEvent`，调用 `data.CallStack()`，自当前帧沿 `Caller` 收集 `CodeAddress.FullMethodName`；停止 session 后记录 `source.EventsLost`。不得依赖 `source.Dynamic.All` 接收该 provider，因为 TraceEvent 已为它注册专用解析器。探针写入 JSON：运行时 TFM、PID、样本数、丢失数、前 20 个方法名、是否命中目标方法和异常。

- [ ] **Step 5: 验证探针并审查证据。**

  Run:

  ```powershell
  dotnet test .\tests\DotnetAnalysis.Diagnostics.IntegrationTests\DotnetAnalysis.Diagnostics.IntegrationTests.csproj --configuration Debug --filter "FullyQualifiedName~ExecutionSamplingCompatibilityProbe"
  ```

  Expected: 每个本机可运行的 `net8.0`、`net9.0`、`net10.0` 用例通过，并在 `TestResults/ExecutionSampling-<timestamp>/compatibility-probe.json` 留下原始事件结果；若任何目标不能产出真实托管栈，停止后续实现并将 JSON 作为设计阻塞证据。

- [ ] **Step 6: Commit。**

  ```powershell
  git add tests/DotnetAnalysis.Diagnostics.TestTarget tests/DotnetAnalysis.Diagnostics.IntegrationTests
  git commit -m "测试：验证 EventPipe 执行采样兼容性"
  ```

### Task 2: 建立 Core 执行分析不可变模型

**Files:**
- Create: `src/DotnetAnalysis.Core/Diagnostics/ExecutionTimeRange.cs`
- Create: `src/DotnetAnalysis.Core/Diagnostics/SourceLocation.cs`
- Create: `src/DotnetAnalysis.Core/Diagnostics/ExecutionFrame.cs`
- Create: `src/DotnetAnalysis.Core/Diagnostics/ExecutionHotspot.cs`
- Create: `src/DotnetAnalysis.Core/Diagnostics/ExecutionCallTreeNode.cs`
- Create: `src/DotnetAnalysis.Core/Diagnostics/ExecutionProfile.cs`
- Modify: `tests/DotnetAnalysis.Tests/Core/Diagnostics/DiagnosticsValueTests.cs`

**Interfaces:**
- Produces:

  ```csharp
  public sealed record ExecutionTimeRange(DateTimeOffset StartAtUtc, DateTimeOffset EndAtUtc);
  public sealed record SourceLocation(string FilePath, int LineNumber, int? ColumnNumber);
  public sealed record ExecutionFrame(string MethodName, string? ModuleName, SourceLocation? SourceLocation);
  public sealed record ExecutionHotspot(ExecutionFrame Frame, long InclusiveSampleCount, long ExclusiveSampleCount);
  public sealed record ExecutionCallTreeNode(
      ExecutionFrame Frame,
      long InclusiveSampleCount,
      long ExclusiveSampleCount,
      IReadOnlyList<ExecutionCallTreeNode> Children);
  public sealed record ExecutionProfile(
      ExecutionTimeRange TimeRange,
      long ReceivedSampleCount,
      long LostEventCount,
      IReadOnlyList<ExecutionHotspot> Hotspots,
      IReadOnlyList<ExecutionCallTreeNode> CallTreeRoots);
  ```

- [ ] **Step 1: 写 Core 值对象失败测试。**

  覆盖：非 UTC 时间转换为 UTC；`EndAtUtc <= StartAtUtc` 拒绝；空方法名、空文件路径、非正行列号和负样本数拒绝；每个输入集合复制为数组；热点和树节点计数允许零；`ExecutionProfile` 不因空样本而抛异常。

  ```csharp
  [TestMethod]
  public void ExecutionTimeRange_WhenEndIsNotAfterStart_Throws()
  {
      var instant = DateTimeOffset.UnixEpoch;
      Assert.ThrowsException<ArgumentOutOfRangeException>(
          () => new ExecutionTimeRange(instant, instant));
  }
  ```

- [ ] **Step 2: 运行失败测试。**

  Run:

  ```powershell
  dotnet test .\tests\DotnetAnalysis.Tests\DotnetAnalysis.Tests.csproj --configuration Debug --filter "FullyQualifiedName~DiagnosticsValueTests"
  ```

  Expected: 编译失败，提示 Core 执行分析模型不存在。

- [ ] **Step 3: 最小实现并补齐 XML 文档。**

  所有构造器以 `ToUniversalTime()` 保存 UTC 时间；`ExecutionTimeRange` 严格要求 `StartAtUtc < EndAtUtc`。`SourceLocation` 将 `FilePath` 规范化为 `Path.GetFullPath`，行号必须大于零，列号为 `null` 或大于零。所有列表均通过 `ToArray()` 防御性复制；计数均为非负；`ExecutionProfile` 保留空热点和空根节点作为合法“区间内没有托管样本”结果。

- [ ] **Step 4: 运行值对象与架构测试。**

  Run:

  ```powershell
  dotnet test .\tests\DotnetAnalysis.Tests\DotnetAnalysis.Tests.csproj --configuration Debug --filter "FullyQualifiedName~DiagnosticsValueTests|FullyQualifiedName~DiagnosticsBoundaryTests"
  ```

  Expected: PASS。

- [ ] **Step 5: Commit。**

  ```powershell
  git add src/DotnetAnalysis.Core tests/DotnetAnalysis.Tests/Core/Diagnostics
  git commit -m "核心：定义执行采样查询模型"
  ```

### Task 3: 扩展应用层查询契约和稳定错误语义

**Files:**
- Modify: `src/DotnetAnalysis.Application/Contracts/Diagnostics/IProcessDiagnosticsSession.cs`
- Modify: `src/DotnetAnalysis.Application/Contracts/Diagnostics/DiagnosticsErrorCode.cs`
- Modify: `tests/DotnetAnalysis.Tests/Architecture/DiagnosticsBoundaryTests.cs`
- Modify: `tests/DotnetAnalysis.Tests/Diagnostics/ProcessDiagnosticsSessionTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `ExecutionTimeRange`、`ExecutionProfile`。
- Produces:

  ```csharp
  Task<ExecutionProfile> GetExecutionProfileAsync(
      ExecutionTimeRange timeRange,
      CancellationToken cancellationToken);
  ```

  以及 `ExecutionProfilingUnavailable`、`ExecutionProfileRangeUnavailable`、`ExecutionProfileStorageFailed` 三个 `DiagnosticsErrorCode` 枚举值。

- [ ] **Step 1: 写契约和边界失败测试。**

  断言反射可找到精确签名；三种新错误码能被 `DiagnosticsException` 保留；Application 项目不引用 `Microsoft.Diagnostics.NETCore.Client`、`Microsoft.Diagnostics.Tracing` 或 `Microsoft.Diagnostics.Symbols`。

- [ ] **Step 2: 运行失败测试。**

  Run:

  ```powershell
  dotnet test .\tests\DotnetAnalysis.Tests\DotnetAnalysis.Tests.csproj --configuration Debug --filter "FullyQualifiedName~DiagnosticsBoundaryTests|FullyQualifiedName~DiagnosticsException_PreservesEveryStableErrorCode"
  ```

  Expected: FAIL，因为接口方法和错误码不存在。

- [ ] **Step 3: 添加公开契约。**

  在 `CaptureSnapshotAsync` 后增加带中文 `summary`、`param`、`returns` 与 `exception` 的 `GetExecutionProfileAsync`；只引用 `DotnetAnalysis.Core.Diagnostics`。在错误枚举末尾加入三项及中文语义，不修改已有数值顺序或既有异常语义。

- [ ] **Step 4: 运行契约、架构和现有会话测试。**

  Run:

  ```powershell
  dotnet test .\tests\DotnetAnalysis.Tests\DotnetAnalysis.Tests.csproj --configuration Debug --filter "FullyQualifiedName~DiagnosticsBoundaryTests|FullyQualifiedName~ProcessDiagnosticsSessionTests"
  ```

  Expected: PASS；现有快照和内存时间线接口仍保持兼容。

- [ ] **Step 5: Commit。**

  ```powershell
  git add src/DotnetAnalysis.Application tests/DotnetAnalysis.Tests
  git commit -m "应用：增加执行采样查询契约"
  ```

### Task 4: 实现会话私有追加式执行采样存储

**Files:**
- Create: `src/DotnetAnalysis.Diagnostics/Windows/ExecutionCaptureStore.cs`
- Create: `src/DotnetAnalysis.Diagnostics/Windows/ExecutionCaptureStorageLayout.cs`
- Create: `tests/DotnetAnalysis.Tests/Diagnostics/ExecutionCaptureStoreTests.cs`

**Interfaces:**
- Produces内部类型：

  ```csharp
  internal sealed record ExecutionSampleRecord(
      DateTimeOffset ObservedAtUtc, int ThreadId, int StackId);

  internal sealed record ExecutionFrameDescriptor(
      string MethodName,
      string? ModuleName,
      string? ModulePath,
      string SymbolKey);

  internal sealed record ExecutionCaptureReadBoundary(
      DateTimeOffset StartedAtUtc,
      DateTimeOffset WrittenThroughUtc,
      long LastCompletedRecord);

  internal sealed class ExecutionCaptureStore : IAsyncDisposable
  {
      ValueTask<int> GetOrAddFrameAsync(ExecutionFrameDescriptor frame, CancellationToken cancellationToken);
      ValueTask<int> GetOrAddStackAsync(int parentStackId, int frameId, CancellationToken cancellationToken);
      ValueTask AppendAsync(ExecutionSampleRecord sample, CancellationToken cancellationToken);
      ExecutionCaptureReadBoundary CaptureReadBoundary();
      IAsyncEnumerable<ExecutionSampleRecord> ReadAsync(
          ExecutionTimeRange range,
          ExecutionCaptureReadBoundary boundary,
          CancellationToken cancellationToken);
  }
  ```

- [ ] **Step 1: 写存储失败测试。**

  覆盖帧/栈去重、跨段时间范围读取、半开边界、读边界不会看见并发追加的半条记录、一个取消读取不会取消另一个读取、读者未结束时 `DisposeAsync` 等待读者、最终目录被删除、模拟 I/O 失败转换为 `ExecutionProfileStorageFailed`。

  ```csharp
  [TestMethod]
  public async Task ReadAsync_UsesCapturedBoundaryAndExcludesConcurrentAppend()
  {
      await using var store = CreateTemporaryStore();
      await store.AppendAsync(Sample("00:00:01"), CancellationToken.None);
      var boundary = store.CaptureReadBoundary();
      await store.AppendAsync(Sample("00:00:02"), CancellationToken.None);

      var records = await store.ReadAsync(Range("00:00:00", "00:00:03"), boundary, CancellationToken.None).ToListAsync();

      Assert.HasCount(1, records);
  }
  ```

- [ ] **Step 2: 运行失败测试。**

  Run:

  ```powershell
  dotnet test .\tests\DotnetAnalysis.Tests\DotnetAnalysis.Tests.csproj --configuration Debug --filter "FullyQualifiedName~ExecutionCaptureStoreTests"
  ```

  Expected: 编译失败，提示执行采样存储类型不存在。

- [ ] **Step 3: 实现分段二进制格式和生命周期。**

  `ExecutionCaptureStorageLayout` 在 `%LOCALAPPDATA%\DotnetAnalysis\ExecutionSessions\<session-id>` 创建私有目录，不穿过任何 Application 契约。`ExecutionCaptureStore` 保持单写入者：帧表按 `(模块路径、模块名、方法名、运行时符号键)` 去重，栈表按 `(parentStackId, frameId)` 去重；样本段以 4 MiB 为上限写入 `timestamp delta + stack id + thread id` 的 7-bit 变长整数。每个封存段保存起止 UTC、样本数与长度，读取仅打开和范围相交的段。

  写入与读边界使用同一锁保护“已完成记录长度”；写入不得先更新索引再写记录。`DisposeAsync` 先拒绝新读取、等待已登记读者、关闭流，再仅删除该会话目录；任何 `IOException`、`UnauthorizedAccessException` 或格式损坏都包装为 `DiagnosticsException(ExecutionProfileStorageFailed, ..., innerException)`。

- [ ] **Step 4: 运行存储测试和完整构建。**

  Run:

  ```powershell
  dotnet test .\tests\DotnetAnalysis.Tests\DotnetAnalysis.Tests.csproj --configuration Debug --filter "FullyQualifiedName~ExecutionCaptureStoreTests"
  dotnet build .\DotnetAnalysis.sln --configuration Debug
  ```

  Expected: PASS，且 0 warnings / 0 errors。

- [ ] **Step 5: Commit。**

  ```powershell
  git add src/DotnetAnalysis.Diagnostics/Windows tests/DotnetAnalysis.Tests/Diagnostics
  git commit -m "诊断：持久化会话执行样本"
  ```

### Task 5: 实现时间范围聚合、热点排序和调用树

**Files:**
- Create: `src/DotnetAnalysis.Diagnostics/Windows/ExecutionProfileBuilder.cs`
- Create: `tests/DotnetAnalysis.Tests/Diagnostics/ExecutionProfileBuilderTests.cs`

**Interfaces:**
- Consumes: `ExecutionCaptureStore` 的帧、栈和样本读取；Task 2 Core 模型。
- Produces:

  ```csharp
  internal sealed class ExecutionProfileBuilder
  {
      public Task<ExecutionProfile> BuildAsync(
          ExecutionTimeRange range,
          ExecutionCaptureReadBoundary boundary,
          long lostEventCount,
          CancellationToken cancellationToken);
  }
  ```

- [ ] **Step 1: 写聚合失败测试。**

  用固定帧 `Root -> Caller -> Leaf` 与 `Root -> OtherLeaf` 写入样本，断言：每条样本使路径所有节点的包含计数加一，只有叶节点独占计数加一；同一帧在不同调用路径保留为不同树节点；热点按包含、独占、方法名、模块名排序；空区间有零样本/空集合；累计 `LostEventCount` 原样返回；取消抛出 `OperationCanceledException` 且不会破坏后续查询。

- [ ] **Step 2: 运行失败测试。**

  Run:

  ```powershell
  dotnet test .\tests\DotnetAnalysis.Tests\DotnetAnalysis.Tests.csproj --configuration Debug --filter "FullyQualifiedName~ExecutionProfileBuilderTests"
  ```

  Expected: 编译失败，因为构建器不存在。

- [ ] **Step 3: 实现流式聚合。**

  对每次查询先固定 `ExecutionCaptureReadBoundary`，再按范围流式读取记录。将每个 `StackId` 还原为根到当前执行方法的帧 ID 链，在查询局部字典中构建树；不得把全会话样本、全会话对象或 EventPipe 事件装入托管列表。每样本递增 `ReceivedSampleCount`、沿路径递增包含计数、对最后一帧递增独占计数，并分别聚合帧级热点。输出前把内部可变节点转换为防御性复制的 `ExecutionCallTreeNode`，热点使用稳定比较器排序。

- [ ] **Step 4: 运行聚合和存储测试。**

  Run:

  ```powershell
  dotnet test .\tests\DotnetAnalysis.Tests\DotnetAnalysis.Tests.csproj --configuration Debug --filter "FullyQualifiedName~ExecutionProfileBuilderTests|FullyQualifiedName~ExecutionCaptureStoreTests"
  ```

  Expected: PASS。

- [ ] **Step 5: Commit。**

  ```powershell
  git add src/DotnetAnalysis.Diagnostics/Windows/ExecutionProfileBuilder.cs tests/DotnetAnalysis.Tests/Diagnostics/ExecutionProfileBuilderTests.cs
  git commit -m "诊断：聚合执行调用树和热点"
  ```

### Task 6: 实现 EventPipe Sample Profiler 采集器和本地符号解析

**Files:**
- Create: `src/DotnetAnalysis.Diagnostics/Windows/EventPipeExecutionSampler.cs`
- Create: `src/DotnetAnalysis.Diagnostics/Windows/ExecutionSymbolResolver.cs`
- Create: `src/DotnetAnalysis.Diagnostics/Windows/ExecutionSamplingSession.cs`
- Create: `tests/DotnetAnalysis.Tests/Diagnostics/ExecutionSymbolResolverTests.cs`
- Create: `tests/DotnetAnalysis.Tests/Diagnostics/ExecutionSamplingSessionTests.cs`

**Interfaces:**
- Produces内部接口：

  ```csharp
  internal interface IExecutionSamplingSession : IAsyncDisposable
  {
      Task StartAsync(TargetProcess target, CancellationToken cancellationToken);
      Task<ExecutionProfile> GetExecutionProfileAsync(
          ExecutionTimeRange timeRange,
          CancellationToken cancellationToken);
  }
  ```

- [ ] **Step 1: 写采样和符号降级失败测试。**

  覆盖：启动时 `DiagnosticsClientException`、`IOException`、`UnauthorizedAccessException` 均映射 `ExecutionProfilingUnavailable`；写存储失败映射 `ExecutionProfileStorageFailed`；丢失事件累加；`SourceLocation` 单飞解析；PDB 缺失、PDB 不匹配、源码文件缺失和动态程序集均返回 `null` 而非失败；取消一个查询不停止采集。

- [ ] **Step 2: 运行失败测试。**

  Run:

  ```powershell
  dotnet test .\tests\DotnetAnalysis.Tests\DotnetAnalysis.Tests.csproj --configuration Debug --filter "FullyQualifiedName~ExecutionSamplingSessionTests|FullyQualifiedName~ExecutionSymbolResolverTests"
  ```

  Expected: 编译失败，因为执行采样会话和符号解析器不存在。

- [ ] **Step 3: 实现采样器。**

  沿用 `EventPipeAllocationSampler` 的单 session、后台 `Process()`、安全 `Stop()` 和有界等待模式，但 provider 改为 Task 1 已验证的 `SampleProfilerTraceEventParser.ProviderName`，同时订阅 `ClrTraceEventParser.ProviderName` 的 `Keywords.Default` 并 `requestRundown: true`。每个 Sample Profiler 栈按“根 -> 当前执行帧”规范化，跳过没有任何托管方法名的栈；消费者线程仅调用帧/栈去重和 `ExecutionCaptureStore.AppendAsync`，以 `Interlocked` 累加成功样本和 `TraceLog.EventsLost`。

  `ExecutionSamplingSession.StartAsync` 对瞬时 `DiagnosticsClientException` 或 `IOException` 只在 1 秒后重试一次；第二次失败记录不可用状态。已不可用会话的查询抛 `DiagnosticsException(ExecutionProfilingUnavailable, ...)`，不影响内存采样、分配采样或快照捕获。

- [ ] **Step 4: 实现只读本地符号解析。**

  帧表保存内部 `ExecutionFrameDescriptor`：方法名、模块名、模块完整路径、运行时 `TraceCodeAddress` 符号句柄及帧 ID；SDK 类型不得离开 Diagnostics。`ExecutionSymbolResolver` 以 `ConcurrentDictionary<int, Task<SourceLocation?>>` 对帧 ID 单飞，使用仅包含模块所在目录的 `SymbolReader` 和 `TraceCodeAddress.GetSourceLine` 读取本地匹配 PDB。仅当 PDB 身份匹配、`SourceFile.BuildTimeFilePath` 指向实际存在文件且行号大于零时，创建 Core `SourceLocation`；不设置 HTTP symbol path、不调用 `GetSourceFile(true)`、不下载 Source Link/源码。解析异常转换为该帧的 `null`，记录 Debug/Warning 日志但不影响调用树。

- [ ] **Step 5: 实现会话查询与关闭语义。**

  查询前校验范围完全落在 `[采样开始时间, 当前已写入水位)`；不在范围内或开始后已结束/清理时抛 `ExecutionProfileRangeUnavailable`。查询注册为在途读取，固定 read boundary，调用 `ExecutionProfileBuilder`，并在输出模型转换时调用符号解析器。`DisposeAsync` 先停止 EventPipe 输入，再最多等待 5 秒排空处理器，拒绝新查询、等待在途查询、释放符号资源并删除会话目录。单个 `CancellationToken` 只能取消自身读取、聚合或等待，绝不停止共享采样。

- [ ] **Step 6: 运行单元测试。**

  Run:

  ```powershell
  dotnet test .\tests\DotnetAnalysis.Tests\DotnetAnalysis.Tests.csproj --configuration Debug --filter "FullyQualifiedName~ExecutionSamplingSessionTests|FullyQualifiedName~ExecutionSymbolResolverTests|FullyQualifiedName~ExecutionCaptureStoreTests|FullyQualifiedName~ExecutionProfileBuilderTests"
  ```

  Expected: PASS。

- [ ] **Step 7: Commit。**

  ```powershell
  git add src/DotnetAnalysis.Diagnostics/Windows tests/DotnetAnalysis.Tests/Diagnostics
  git commit -m "诊断：采集执行栈并解析本地源码"
  ```

### Task 7: 接入附着会话并保障并列生命周期

**Files:**
- Modify: `src/DotnetAnalysis.Diagnostics/Windows/WindowsProcessDiagnostics.cs`
- Modify: `src/DotnetAnalysis.Diagnostics/Windows/ProcessDiagnosticsSession.cs`
- Modify: `tests/DotnetAnalysis.Tests/Diagnostics/ProcessDiagnosticsSessionTests.cs`
- Modify: `tests/DotnetAnalysis.Tests/Diagnostics/RuntimeCapabilitiesResolverTests.cs`

**Interfaces:**
- Consumes: `IExecutionSamplingSession`、现有 `AllocationSamplingSession`、`ProcessMemorySampler`、`IMemorySnapshotCapture`。
- Produces: `IProcessDiagnosticsSession.GetExecutionProfileAsync` 的实际实现。

- [ ] **Step 1: 写会话接入失败测试。**

  断言附着期间启动执行采样；采样启动失败时会话仍为 `Monitoring`、内存时间线可读且 `GetExecutionProfileAsync` 返回 `ExecutionProfilingUnavailable`；结束会话取消在途执行查询并在采样资源释放后转为 `Ended`；捕获快照与执行查询可并发；进程退出后查询返回 `ExecutionProfileRangeUnavailable`。

- [ ] **Step 2: 运行失败测试。**

  Run:

  ```powershell
  dotnet test .\tests\DotnetAnalysis.Tests\DotnetAnalysis.Tests.csproj --configuration Debug --filter "FullyQualifiedName~ProcessDiagnosticsSessionTests"
  ```

  Expected: FAIL，因为 `ProcessDiagnosticsSession` 尚未拥有执行采样会话。

- [ ] **Step 3: 最小化接入。**

  `WindowsProcessDiagnostics.AttachCoreAsync` 在分配采样启动后创建并启动 `ExecutionSamplingSession`；捕获到稳定启动错误时保留一个不可用的执行会话对象，不终止附着。`ProcessDiagnosticsSession` 构造函数新增内部 `IExecutionSamplingSession` 参数和字段，公开方法仅转发查询且不发布额外事件。结束顺序固定为：取消会话 -> 等待活动 `.gcdump` -> 释放执行采样 -> 释放分配采样 -> 释放内存采样 -> 状态迁移与既有事件。不要新增事件总线、服务定位器或 Diagnostics SDK 类型到 Application。

- [ ] **Step 4: 运行会话和架构回归。**

  Run:

  ```powershell
  dotnet test .\tests\DotnetAnalysis.Tests\DotnetAnalysis.Tests.csproj --configuration Debug --filter "FullyQualifiedName~ProcessDiagnosticsSessionTests|FullyQualifiedName~DiagnosticsBoundaryTests|FullyQualifiedName~RuntimeCapabilitiesResolverTests"
  dotnet build .\DotnetAnalysis.sln --configuration Debug
  ```

  Expected: PASS，0 warnings / 0 errors。

- [ ] **Step 5: Commit。**

  ```powershell
  git add src/DotnetAnalysis.Diagnostics/Windows tests/DotnetAnalysis.Tests
  git commit -m "诊断：接入附着会话执行分析"
  ```

### Task 8: 完成 .NET 8/9/10 Windows 集成与并发快照验证

**Files:**
- Create: `tests/DotnetAnalysis.Diagnostics.IntegrationTests/ExecutionSamplingIntegrationTests.cs`
- Modify: `tests/DotnetAnalysis.Diagnostics.IntegrationTests/IntegrationTestHost.cs`

**Interfaces:**
- Consumes: 已实现的 `IProcessDiagnosticsSession.GetExecutionProfileAsync`、`IntegrationTargetOptions`。
- Produces: `WindowsDiagnosticsIntegration` 分类下的执行采样回归测试。

- [ ] **Step 1: 写集成断言。**

  对每个支持的 TFM 启动 `EnableExecutionWorkload = true` 目标，附着并等待至少 5 秒后查询一个完全覆盖已写入范围的 `ExecutionTimeRange`。断言样本数大于零、热点按计数降序、树中存在 `ExecutionSamplingWorkload`、目标 Debug PDB 返回实际存在的 `ExecutionSamplingWorkload.cs` 和正行号。再启动一个从复制输出目录运行但排除目标 `.pdb` 的目标，断言相同热点/树仍存在且全部帧允许 `SourceLocation == null`。

- [ ] **Step 2: 添加并发、边界和快照测试。**

  对同一会话并发启动 8 个随机合法范围查询，其中两个传入 100 ms 后取消的 token；断言 6 个成功查询计数自洽、两个仅抛 `OperationCanceledException`、采样仍在增长。执行三次顺序 `.gcdump` 捕获，每次捕获前后读取执行概要并断言样本时间连续、会话仍为 `Monitoring`。分别断言范围超出开始、水位之后和结束清理后得到 `ExecutionProfileRangeUnavailable`。

- [ ] **Step 3: 运行集成测试并确认先失败。**

  Run:

  ```powershell
  dotnet test .\tests\DotnetAnalysis.Diagnostics.IntegrationTests\DotnetAnalysis.Diagnostics.IntegrationTests.csproj --configuration Debug --filter "TestCategory=WindowsDiagnosticsIntegration"
  ```

  Expected: 新增断言在实现前失败。

- [ ] **Step 4: 仅为测试宿主补充必要能力。**

  `IntegrationTestHost` 支持 `(enableExecutionWorkload, copyWithoutPdb)` 两个显式选项；无 PDB 方式复制测试目标输出到临时目录、排除 `DotnetAnalysis.Diagnostics.TestTarget.pdb` 后启动，Dispose 时删除该目录。不得弱化现有 `READY`/`EXIT` 协议，不得用该受控目标替代真实 MQTTnet 验收。

- [ ] **Step 5: 运行集成回归。**

  Run:

  ```powershell
  dotnet build .\DotnetAnalysis.sln --configuration Debug
  dotnet test .\tests\DotnetAnalysis.Diagnostics.IntegrationTests\DotnetAnalysis.Diagnostics.IntegrationTests.csproj --configuration Debug --no-build --filter "TestCategory=WindowsDiagnosticsIntegration"
  ```

  Expected: PASS；所有可用运行时均产生真实托管调用树，三次快照不停止执行采样。

- [ ] **Step 6: Commit。**

  ```powershell
  git add tests/DotnetAnalysis.Diagnostics.IntegrationTests tests/DotnetAnalysis.Diagnostics.TestTarget
  git commit -m "测试：覆盖执行采样附着与快照并发"
  ```

### Task 9: 添加性能基准、压力浸泡和原始 JSON 证据

**Files:**
- Create: `tests/DotnetAnalysis.Diagnostics.IntegrationTests/ExecutionSamplingPerformanceTests.cs`
- Create: `tests/DotnetAnalysis.Diagnostics.IntegrationTests/ExecutionSamplingEvidenceWriter.cs`

**Interfaces:**
- Consumes: 受控 8 worker / 200 path 工作负载、完整 `ExecutionProfile` 查询。
- Produces: `TestResults/ExecutionSampling-<yyyyMMdd-HHmmss>/benchmark.json` 与 `stress.json`。

- [ ] **Step 1: 写环境门控和阈值失败测试。**

  两个测试仅在环境变量为 `true` 时执行：`DOTNET_ANALYSIS_RUN_EXECUTION_PROFILE_BENCHMARK` 执行 60 分钟基准，`DOTNET_ANALYSIS_RUN_EXECUTION_PROFILE_STRESS` 执行 2 小时浸泡。未设置时调用 `Assert.Inconclusive` 并说明这是显式门禁。证据 writer 必须拒绝只给摘要、没有原始数组的结果。

- [ ] **Step 2: 实现 60 分钟基准。**

  连续三轮 120 秒未附着基线和三轮已附着采样，比较中位完成量，目标下降不得超过 5%。在采样会话预热 30 秒后记录 60 分钟：诊断宿主 `TotalProcessorTime` 增量/墙钟时间不超过 0.05 核、相对采样启动基线的私有内存峰值不超过 128 MiB、执行会话私有目录不超过 128 MiB。完整范围连续查询 10 次，P95 不超过 2 秒，每次 `GC.GetTotalAllocatedBytes(true)` 增量不超过 64 MiB。所有受控运行 `LostEventCount == 0`。

- [ ] **Step 3: 实现 2 小时压力浸泡。**

  保持 8 CPU worker、至少 200 条路径，每分钟随机合法时间范围查询一次；每 10 分钟做 8 并发查询，其中 2 个取消；每 10 分钟执行一次三次 `.gcdump` 捕获序列。结束时断言采样没有中断、无丢失事件、调用树/热点样本计数一致、会话私有目录仅在 `EndAsync` 后删除、句柄数和私有内存没有单调泄漏。

- [ ] **Step 4: 写完整证据。**

  `ExecutionSamplingEvidenceWriter` 每次运行新建带 UTC 时间戳目录，JSON 固定包含：Git HEAD、SDK/运行时、Windows 版本、逻辑处理器数、目标路径/TFM/PID/启动时间、采样频率、原始样本和丢失数、目标吞吐原始数组、诊断 CPU/内存原始数组、存储大小、10 次查询耗时与分配数组、并发/取消结果、快照结果、异常和阈值判定。失败时也必须在 finally 写 JSON 后再让测试失败。

- [ ] **Step 5: 先运行编译和默认测试。**

  Run:

  ```powershell
  dotnet build .\DotnetAnalysis.sln --configuration Debug
  dotnet test .\DotnetAnalysis.sln --configuration Debug --no-build
  ```

  Expected: PASS；长时门禁默认不运行。

- [ ] **Step 6: 运行显式门禁并保存结果。**

  Run:

  ```powershell
  $env:DOTNET_ANALYSIS_RUN_EXECUTION_PROFILE_BENCHMARK = 'true'
  dotnet test .\tests\DotnetAnalysis.Diagnostics.IntegrationTests\DotnetAnalysis.Diagnostics.IntegrationTests.csproj --configuration Debug --no-build --filter "FullyQualifiedName~ExecutionSamplingBenchmark"

  $env:DOTNET_ANALYSIS_RUN_EXECUTION_PROFILE_STRESS = 'true'
  dotnet test .\tests\DotnetAnalysis.Diagnostics.IntegrationTests\DotnetAnalysis.Diagnostics.IntegrationTests.csproj --configuration Debug --no-build --filter "FullyQualifiedName~ExecutionSamplingStress"
  ```

  Expected: 两项通过并保留完整 JSON；任一硬门槛失败即停止交付并分析证据，不放宽阈值。

- [ ] **Step 7: Commit。**

  ```powershell
  git add tests/DotnetAnalysis.Diagnostics.IntegrationTests
  git commit -m "测试：加入执行采样性能与压力门禁"
  ```

### Task 10: 用 MQTTnet.TestApp 做真实应用附着验收

**Files:**
- Create: `tests/DotnetAnalysis.Diagnostics.IntegrationTests/ExecutionSamplingRealApplicationAcceptanceTests.cs`
- Modify: `tests/DotnetAnalysis.Diagnostics.IntegrationTests/ExecutionSamplingEvidenceWriter.cs`

**Interfaces:**
- Consumes: 环境变量 `DOTNET_ANALYSIS_RUN_REAL_APPLICATION_ACCEPTANCE=true` 与 `DOTNET_ANALYSIS_REAL_APPLICATION_PID`。
- Produces: `TestResults/ExecutionSampling-<timestamp>/mqttnet-real-application-acceptance.json`。

- [ ] **Step 1: 写真实目标身份检查失败测试。**

  验收测试未设置执行变量时 `Assert.Inconclusive`；设置后仅接受存在的 PID、目标主模块文件名为 `MQTTnet.TestApp.exe`、目标为 x64 `.NET 8` CoreCLR、启动时间与 `GetProcessesAsync()` 返回的 `TargetProcess.StartedAtUtc` 相同。其他进程、测试宿主、TestTarget 或 PID 复用均立即失败，不允许回退到任何受控目标。

- [ ] **Step 2: 实现 10 分钟真实附着流程。**

  测试不启动 MQTTnet。执行者必须先在独立正常控制台运行：

  ```powershell
  dotnet run --project 'D:\AIProject\MQTTnet\Source\MQTTnet.TestApp\MQTTnet.TestApp.csproj' --configuration Debug
  ```

  菜单出现后人工按 `b`，保持 QoS 1 循环。测试附着该既有 PID 并连续采样不少于 10 分钟，在第 2、5、8 分钟固定边界查询早/中/晚三个不重叠范围；每个范围必须有样本和调用树。范围中至少一帧的模块为 `MQTTnet.TestApp` 或 `MQTTnet`，并具有现存源码文件、正行号和对应本地 PDB。第 5 分钟执行一次 `.gcdump`，捕获前后分别查询执行概要并断言采样连续、会话仍为 `Monitoring`。

- [ ] **Step 3: 写不可替代的证据。**

  JSON 除 Task 9 字段外还包含：应用项目路径、EXE SHA-256、每个候选 PDB 路径及 SHA-256、PID、启动时间、附着/结束时间、三个范围及完整 `ExecutionProfile` 原始树、调用树中命中的源码位置、`.gcdump` 结果、累计丢失数、诊断 CPU/内存与任何异常。无论通过或失败均写出该文件；只有通过才允许声明功能完成。

- [ ] **Step 4: 运行真实应用验收。**

  在 MQTTnet 已按上述方式启动且仅有一个 `MQTTnet.TestApp` 进程时运行：

  ```powershell
  $env:DOTNET_ANALYSIS_RUN_REAL_APPLICATION_ACCEPTANCE = 'true'
  $mqttnetTestApp = Get-Process -Name 'MQTTnet.TestApp' -ErrorAction Stop
  if (@($mqttnetTestApp).Count -ne 1) { throw '真实应用验收要求恰好一个正在运行的 MQTTnet.TestApp 进程。' }
  $env:DOTNET_ANALYSIS_REAL_APPLICATION_PID = $mqttnetTestApp.Id
  dotnet test .\tests\DotnetAnalysis.Diagnostics.IntegrationTests\DotnetAnalysis.Diagnostics.IntegrationTests.csproj --configuration Debug --no-build --filter "FullyQualifiedName~ExecutionSamplingRealApplicationAcceptance"
  ```

  Expected: PASS，输出真实应用 JSON；这一步不能由其他测试的绿灯替代。

- [ ] **Step 5: Commit。**

  ```powershell
  git add tests/DotnetAnalysis.Diagnostics.IntegrationTests
  git commit -m "验收：附着 MQTTnet 执行采样"
  ```

### Task 11: 完整验证、性能复核和双重 QA

**Files:**
- Modify: 仅修复本任务前十项发现的缺陷；不得引入 Desktop/UI 变更。

**Interfaces:**
- Consumes: 前十项的源码、测试与 `TestResults/ExecutionSampling-*` 原始证据。
- Produces: 通过验证的工作树、提交历史和诚实的测试报告。

- [ ] **Step 1: 第一轮需求 QA。**

  按规格逐项核对：三层边界、全会话保存、范围拒绝而非裁剪、无 PDB 降级、会话结束删除、独立执行/分配数据、稳定错误码、并发/取消、三次快照、全部硬门槛和 MQTTnet 真实验收。任何缺项先以失败测试复现，再实施最小修复。

- [ ] **Step 2: 运行常规自动验证。**

  Run:

  ```powershell
  dotnet build .\DotnetAnalysis.sln --configuration Debug
  dotnet test .\DotnetAnalysis.sln --configuration Debug --no-build
  dotnet test .\tests\DotnetAnalysis.Diagnostics.IntegrationTests\DotnetAnalysis.Diagnostics.IntegrationTests.csproj --configuration Debug --no-build --filter "TestCategory=WindowsDiagnosticsIntegration"
  $env:DOTNET_ANALYSIS_RUN_LARGE_SNAPSHOT_BENCHMARK = 'true'
  dotnet test .\tests\DotnetAnalysis.Diagnostics.IntegrationTests\DotnetAnalysis.Diagnostics.IntegrationTests.csproj --configuration Debug --no-build --filter "FullyQualifiedName~LargeSnapshotBenchmark_RecordsCaptureAndCachedQueryMeasurements"
  ```

  Expected: 全部 PASS；保留新产生的百万对象和执行采样证据。

- [ ] **Step 3: 运行执行采样长时和真实应用门禁。**

  依次运行 Task 9 的 60 分钟基准、2 小时压力命令和 Task 10 的 MQTTnet 10 分钟验收命令；检查每个 JSON 均有原始数组、非空样本、真实 PID/二进制/PDB 身份和阈值判定。

- [ ] **Step 4: 第二轮独立实现 QA。**

  独立检查所有新增公开 API 的中文 XML、无 SDK 泄漏到 Core/Application/Desktop、无 `TODO`/占位、`git diff --check`、调用树独占/包含计数、会话目录清理、取消不停止采样、PDB 缺失不伪造位置和真实 MQTTnet 不是 TestTarget。若发现问题，最多进行三轮“失败测试 -> 修复 -> 重跑受影响门禁 -> 再审”。

- [ ] **Step 5: 最终状态核验与提交。**

  Run:

  ```powershell
  git diff --check
  git status --short
  git log --oneline -12
  ```

  Expected: 无格式错误；除明确需要保留的 `TestResults` 原始证据外工作树干净。提交任何最终修复：

  ```powershell
  git add src/DotnetAnalysis.Core src/DotnetAnalysis.Application src/DotnetAnalysis.Diagnostics tests/DotnetAnalysis.Tests tests/DotnetAnalysis.Diagnostics.IntegrationTests tests/DotnetAnalysis.Diagnostics.TestTarget
  git commit -m "验证：完成执行采样诊断层"
  ```

## 规格覆盖自检

- EventPipe Sample Profiler、真实托管栈和 .NET 8/9/10 兼容性：Task 1、6、8。
- Core/Application 输入输出、稳定错误、分层封装：Task 2、3、7。
- 全会话压缩保存、范围读取、会话结束清理：Task 4、6、7。
- 调用树、热点、包含/独占计数、无样本语义：Task 5。
- 本地 PDB/源码定位与缺失降级：Task 6、8、10。
- 分配/内存/快照并列运行、取消和并发：Task 7、8。
- CPU、内存、存储、查询时间和压力硬门槛：Task 9、11。
- MQTTnet.TestApp 十分钟真实应用、三段查询和 `.gcdump` 证据：Task 10、11。

占位符扫描、类型一致性和范围复核已完成：本计划没有 Desktop/UI 实现任务、没有网络符号/源码下载、没有把受控 TestTarget 当作真实验收替代品。
