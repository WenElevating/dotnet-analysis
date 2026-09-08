using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
/// <summary>
/// 对由外部环境显式提供的 MQTTnet 真实应用执行附着验收，验证采样证据而不自行启动或修改目标进程。
/// </summary>
public sealed class ExecutionSamplingRealApplicationAcceptanceTests
{
    private const string AcceptanceGate = "DOTNET_ANALYSIS_RUN_REAL_APPLICATION_ACCEPTANCE";
    private const string TargetProcessIdVariable = "DOTNET_ANALYSIS_REAL_APPLICATION_PID";
    private const string ProjectPath = @"D:\AIProject\MQTTnet\Source\MQTTnet.TestApp\MQTTnet.TestApp.csproj";
    private static readonly TimeSpan s_queryAvailabilityTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_queryRetryDelay = TimeSpan.FromMilliseconds(250);
    private readonly TestContext _testContext;

    public ExecutionSamplingRealApplicationAcceptanceTests(TestContext testContext) => _testContext = testContext;

    [TestMethod]
    [TestCategory("ExecutionSamplingRealApplicationAcceptance")]
    [DoNotParallelize]
    [Timeout(780_000, CooperativeCancellation = true)]
    /// <summary>
    /// 在人工门禁、目标 PID 和目标二进制验证全部通过后，附着既有 MQTTnet 测试应用并写出可追溯执行证据。
    /// </summary>
    public async Task ExecutionSamplingRealApplicationAcceptance_AttachesOnlyToExistingMqttnetTestAppAndPreservesExecutionEvidence()
    {
        EnsureAcceptanceGateEnabled();

        var cancellationToken = _testContext.CancellationToken;
        var writer = ExecutionSamplingEvidenceWriter.Create(
            "mqttnet-real-application-acceptance",
            DateTimeOffset.UtcNow);
        var evidence = ExecutionSamplingRunEvidence.Create("mqttnet-real-application-acceptance");
        var snapshotRoot = Path.Combine(
            Path.GetTempPath(),
            "DotnetAnalysis.Diagnostics.IntegrationTests",
            "MqttnetRealApplicationAcceptance",
            Guid.NewGuid().ToString("N"));
        IProcessDiagnosticsSession? session = null;
        DateTimeOffset? attachedAtUtc = null;
        string? executablePath = null;
        IReadOnlyList<ExecutionSamplingPdbEvidence> pdbs = [];
        var primaryFailureObserved = false;

        try
        {
            evidence.Environment = await CreateEnvironmentEvidenceAsync(cancellationToken);
            var expectedProcessId = ReadTargetProcessId();
            Assert.HasCount(1, Process.GetProcessesByName("MQTTnet.TestApp"));
            using var targetProcess = GetRequiredTargetProcess(expectedProcessId);
            executablePath = ValidateTargetProcess(targetProcess);
            var targetStartedAtUtc = new DateTimeOffset(targetProcess.StartTime.ToUniversalTime());
            var workloadAssemblyPath = Path.Combine(
                Path.GetDirectoryName(executablePath)
                    ?? throw new AssertFailedException("MQTTnet.TestApp executable must have a directory."),
                "MQTTnet.TestApp.dll");
            Assert.IsTrue(File.Exists(workloadAssemblyPath), "MQTTnet.TestApp.dll must be next to the app host.");

            var snapshotLayout = new SnapshotStorageLayout(snapshotRoot);
            var diagnostics = new WindowsProcessDiagnostics(
                new InProcessEventBus(NullLogger<InProcessEventBus>.Instance),
                TimeProvider.System,
                snapshotLayout: snapshotLayout);
            var enumeratedTarget = (await diagnostics.GetProcessesAsync(cancellationToken))
                .SingleOrDefault(candidate => candidate.ProcessId == expectedProcessId)
                ?? throw new AssertFailedException("The configured MQTTnet.TestApp PID was not returned by GetProcessesAsync().");
            Assert.AreEqual(targetStartedAtUtc, enumeratedTarget.StartedAtUtc, "PID identity must include the exact process start time.");
            Assert.AreEqual(executablePath, enumeratedTarget.ExecutablePath, ignoreCase: true);

            evidence.Target = new ExecutionSamplingTargetEvidence(
                executablePath,
                workloadAssemblyPath,
                "net8.0",
                enumeratedTarget.ProcessId,
                enumeratedTarget.StartedAtUtc);
            evidence.Configuration = new ExecutionSamplingConfigurationEvidence(
                0,
                0,
                "runtime Sample Profiler default; existing MQTTnet QoS 1 workload; no interval override configured",
                0,
                600,
                0,
                30,
                0);
            pdbs = await CollectPdbEvidenceAsync(executablePath, cancellationToken);

            session = await diagnostics.AttachAsync(enumeratedTarget, cancellationToken);
            Assert.IsInstanceOfType<ProcessDiagnosticsSession>(session);
            attachedAtUtc = DateTimeOffset.UtcNow;
            using var diagnosticsProcess = Process.GetCurrentProcess();
            RecordResource(evidence, diagnosticsProcess, "attached", attachedAtUtc.Value);

            await DelayUntilAsync(attachedAtUtc.Value.AddMinutes(2), cancellationToken);
            var early = await QueryAvailableProfileAsync(
                session,
                new ExecutionTimeRange(attachedAtUtc.Value.AddSeconds(5), attachedAtUtc.Value.AddMinutes(1).AddSeconds(50)),
                cancellationToken);
            RecordProfile(evidence, "early", early);
            var earlyProfile = early.Profile;

            await DelayUntilAsync(attachedAtUtc.Value.AddMinutes(5), cancellationToken);
            var middle = await QueryAvailableProfileAsync(
                session,
                new ExecutionTimeRange(attachedAtUtc.Value.AddMinutes(2).AddSeconds(5), attachedAtUtc.Value.AddMinutes(4).AddSeconds(50)),
                cancellationToken);
            RecordProfile(evidence, "middle", middle);
            var middleProfile = middle.Profile;

            var beforeSnapshotQuery = await QueryAvailableProfileAsync(
                session,
                new ExecutionTimeRange(attachedAtUtc.Value.AddMinutes(4).AddSeconds(51), DateTimeOffset.UtcNow.AddSeconds(-2)),
                cancellationToken);
            RecordProfile(evidence, "before-gcdump", beforeSnapshotQuery);
            var beforeSnapshot = beforeSnapshotQuery.Profile;
            var snapshotStartedAtUtc = DateTimeOffset.UtcNow;
            var snapshot = await session.CaptureSnapshotAsync(cancellationToken);
            var snapshotCompletedAtUtc = DateTimeOffset.UtcNow;
            Assert.AreEqual(ProcessDiagnosticsSessionState.Monitoring, session.State);
            var dumpPath = snapshotLayout.GetFinalDumpPath(snapshot.Id);
            Assert.IsTrue(File.Exists(dumpPath), "The acceptance snapshot must be persisted as a gcdump.");
            var dumpSize = new FileInfo(dumpPath).Length;
            Assert.IsGreaterThan(dumpSize, 0L);
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            var afterSnapshotQuery = await QueryAvailableProfileAsync(
                session,
                new ExecutionTimeRange(snapshotCompletedAtUtc.AddMilliseconds(1), DateTimeOffset.UtcNow.AddSeconds(-2)),
                cancellationToken);
            RecordProfile(evidence, "after-gcdump", afterSnapshotQuery);
            var afterSnapshot = afterSnapshotQuery.Profile;
            evidence.Snapshots.Add(new ExecutionSamplingSnapshotMeasurement(
                1,
                snapshotStartedAtUtc,
                snapshotCompletedAtUtc,
                (snapshotCompletedAtUtc - snapshotStartedAtUtc).TotalMilliseconds,
                snapshot.Id.ToString(),
                snapshot.State.ToString(),
                dumpSize,
                0,
                diagnosticsProcess.PrivateMemorySize64,
                diagnosticsProcess.PrivateMemorySize64,
                beforeSnapshot.ReceivedSampleCount,
                afterSnapshot.ReceivedSampleCount,
                afterSnapshot.LostEventCount,
                afterSnapshot.ReceivedSampleCount > 0,
                null));
            Assert.IsGreaterThan(afterSnapshot.ReceivedSampleCount, 0L, "Sampling must continue after gcdump capture.");

            await DelayUntilAsync(attachedAtUtc.Value.AddMinutes(8), cancellationToken);
            var late = await QueryAvailableProfileAsync(
                session,
                new ExecutionTimeRange(attachedAtUtc.Value.AddMinutes(5).AddSeconds(5), attachedAtUtc.Value.AddMinutes(7).AddSeconds(50)),
                cancellationToken);
            RecordProfile(evidence, "late", late);
            var lateProfile = late.Profile;
            AssertNonOverlapping(earlyProfile, middleProfile, lateProfile);

            var sourceHits = CreateSourceHits([earlyProfile, middleProfile, lateProfile], pdbs);
            Assert.IsNotEmpty(sourceHits, "At least one MQTTnet.TestApp or MQTTnet frame must resolve to an existing local source location and matching local PDB.");
            evidence.SourceHits.AddRange(sourceHits);
            RecordResource(evidence, diagnosticsProcess, "completed", DateTimeOffset.UtcNow);

            await session.EndAsync(cancellationToken);
            Assert.AreEqual(ProcessDiagnosticsSessionState.Ended, session.State);
            evidence.RealApplication = new ExecutionSamplingRealApplicationEvidence(
                ProjectPath,
                await ComputeSha256Async(executablePath, cancellationToken),
                pdbs,
                attachedAtUtc.Value,
                DateTimeOffset.UtcNow);
            AddAcceptanceThresholds(evidence, earlyProfile, middleProfile, lateProfile, afterSnapshot, sourceHits);
        }
        catch (Exception exception)
        {
            primaryFailureObserved = true;
            evidence.Exceptions.Add(ExecutionSamplingExceptionEvidence.From("mqttnet-real-application-acceptance", exception));
            throw;
        }
        finally
        {
            Exception? cleanupFailure = null;
            if (session is not null)
            {
                try
                {
                    await session.DisposeAsync();
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                    evidence.Exceptions.Add(ExecutionSamplingExceptionEvidence.From("session-dispose", exception));
                }
            }

            evidence.CompletedAtUtc = DateTimeOffset.UtcNow;
            try
            {
                await writer.WriteAsync(evidence, CancellationToken.None);
            }
            finally
            {
                DeleteDirectoryIfPresent(snapshotRoot);
            }

            if (cleanupFailure is not null && !primaryFailureObserved)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }
        }
    }

    /// <summary>
    /// 强制要求人工显式启用验收门禁，避免常规集成测试依赖外部 PID。
    /// </summary>
    private static void EnsureAcceptanceGateEnabled()
    {
        if (!ExecutionSamplingGate.IsEnabled(Environment.GetEnvironmentVariable(AcceptanceGate)))
        {
            Assert.Inconclusive($"Set {AcceptanceGate}=true after manually starting MQTTnet.TestApp and pressing b to run this real-application acceptance test.");
        }
    }

    /// <summary>
    /// 从环境变量读取并验证人工提供的目标进程标识。
    /// </summary>
    private static int ReadTargetProcessId()
    {
        var value = Environment.GetEnvironmentVariable(TargetProcessIdVariable);
        if (!int.TryParse(value, out var processId) || processId <= 0)
        {
            throw new AssertFailedException($"{TargetProcessIdVariable} must contain one positive MQTTnet.TestApp PID.");
        }

        return processId;
    }

    /// <summary>
    /// 获取仍在运行的指定进程；目标已退出时给出验收前置条件错误。
    /// </summary>
    private static Process GetRequiredTargetProcess(int processId)
    {
        try
        {
            return Process.GetProcessById(processId);
        }
        catch (ArgumentException exception)
        {
            throw new AssertFailedException($"Configured PID {processId} is not running: {exception.Message}");
        }
    }

    /// <summary>
    /// 验证目标映像路径、名称和 AMD64 格式，防止验收误附着到无关进程。
    /// </summary>
    private static string ValidateTargetProcess(Process process)
    {
        Assert.AreEqual("MQTTnet.TestApp", process.ProcessName, ignoreCase: true);
        var executablePath = process.MainModule?.FileName
            ?? throw new AssertFailedException("MQTTnet.TestApp main module path must be readable.");
        Assert.AreEqual("MQTTnet.TestApp.exe", Path.GetFileName(executablePath), ignoreCase: true);
        Assert.IsTrue(IsAmd64PeImage(executablePath), "MQTTnet.TestApp must be an x64 app host.");
        var coreLib = process.Modules.Cast<ProcessModule>().FirstOrDefault(module =>
            string.Equals(module.ModuleName, "System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(coreLib, "MQTTnet.TestApp must be a CoreCLR process.");
        var runtimeVersion = FileVersionInfo.GetVersionInfo(coreLib.FileName).FileVersion;
        Assert.IsTrue(Version.TryParse(runtimeVersion, out var runtime) && runtime.Major == 8, "MQTTnet.TestApp must run on .NET 8 CoreCLR.");
        return executablePath;
    }

    /// <summary>
    /// 读取 PE 头机器类型，确认目标是当前诊断实现支持的 AMD64 进程。
    /// </summary>
    private static bool IsAmd64PeImage(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        Span<byte> dosHeader = stackalloc byte[64];
        stream.ReadExactly(dosHeader);
        var peOffset = BitConverter.ToInt32(dosHeader[60..]);
        stream.Position = peOffset;
        Span<byte> peHeader = stackalloc byte[6];
        stream.ReadExactly(peHeader);
        return peHeader[..4].SequenceEqual("PE\0\0"u8)
            && BitConverter.ToUInt16(peHeader[4..]) == 0x8664;
    }

    /// <summary>
    /// 在可取消条件下等待到指定 UTC 时间，用于固定采样和查询节奏。
    /// </summary>
    private static async Task DelayUntilAsync(DateTimeOffset targetAtUtc, CancellationToken cancellationToken)
    {
        var remaining = targetAtUtc - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }
    }

    /// <summary>
    /// 轮询当前可查询时间范围，直到目标已有足够采样数据或调用方取消。
    /// </summary>
    private static async Task<MeasuredExecutionProfile> QueryAvailableProfileAsync(
        IProcessDiagnosticsSession session,
        ExecutionTimeRange range,
        CancellationToken cancellationToken)
    {
        using var diagnosticsProcess = Process.GetCurrentProcess();
        diagnosticsProcess.Refresh();
        var privateMemoryBefore = diagnosticsProcess.PrivateMemorySize64;
        var processorBefore = diagnosticsProcess.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var startedAtUtc = DateTimeOffset.UtcNow;
        var deadline = DateTimeOffset.UtcNow + s_queryAvailabilityTimeout;
        while (true)
        {
            try
            {
                var profile = await session.GetExecutionProfileAsync(range, cancellationToken);
                AssertProfile(profile);
                diagnosticsProcess.Refresh();
                return new MeasuredExecutionProfile(
                    profile,
                    startedAtUtc,
                    DateTimeOffset.UtcNow,
                    GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
                    (diagnosticsProcess.TotalProcessorTime - processorBefore).TotalMilliseconds,
                    privateMemoryBefore,
                    diagnosticsProcess.PrivateMemorySize64);
            }
            catch (DiagnosticsException exception) when (
                exception.ErrorCode == DiagnosticsErrorCode.ExecutionProfileRangeUnavailable
                && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(s_queryRetryDelay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// 将阶段性执行分析结果和持续时间写入验收证据。
    /// </summary>
    private static void RecordProfile(
        ExecutionSamplingRunEvidence evidence,
        string phase,
        MeasuredExecutionProfile measuredProfile)
    {
        var profile = measuredProfile.Profile;
        evidence.Profiles.Add(new ExecutionSamplingProfileEvidence(phase, profile));
        evidence.ReceivedSampleCounts.Add(profile.ReceivedSampleCount);
        evidence.LostEventCounts.Add(profile.LostEventCount);
        evidence.Queries.Add(new ExecutionSamplingQueryMeasurement(
            phase,
            evidence.Queries.Count + 1,
            measuredProfile.StartedAtUtc,
            measuredProfile.CompletedAtUtc,
            profile.TimeRange.StartAtUtc,
            profile.TimeRange.EndAtUtc,
            (measuredProfile.CompletedAtUtc - measuredProfile.StartedAtUtc).TotalMilliseconds,
            measuredProfile.AllocatedBytes,
            measuredProfile.DiagnosticProcessorMilliseconds,
            measuredProfile.PrivateMemoryBeforeBytes,
            measuredProfile.PrivateMemoryAfterBytes,
            profile.ReceivedSampleCount,
            profile.LostEventCount,
            profile.Hotspots.Sum(static hotspot => hotspot.ExclusiveSampleCount),
            profile.CallTreeRoots.Sum(static node => node.InclusiveSampleCount),
            IsConsistent(profile)));
    }

    /// <summary>
    /// 记录当前诊断进程与目标进程的资源观测，供人工判断附着影响。
    /// </summary>
    private static void RecordResource(
        ExecutionSamplingRunEvidence evidence,
        Process diagnosticsProcess,
        string phase,
        DateTimeOffset observedAtUtc)
    {
        diagnosticsProcess.Refresh();
        evidence.DiagnosticResources.Add(new ExecutionSamplingResourceMeasurement(
            phase,
            observedAtUtc,
            0,
            diagnosticsProcess.TotalProcessorTime.TotalMilliseconds,
            0,
            diagnosticsProcess.PrivateMemorySize64,
            diagnosticsProcess.HandleCount,
            0,
            0));
    }

    /// <summary>
    /// 断言执行分析包含有效样本、调用树和一致的计数关系。
    /// </summary>
    private static void AssertProfile(ExecutionProfile profile)
    {
        Assert.IsGreaterThan(profile.ReceivedSampleCount, 0L);
        Assert.IsNotEmpty(profile.Hotspots);
        Assert.IsNotEmpty(profile.CallTreeRoots);
        Assert.IsTrue(IsConsistent(profile), "Execution profile hotspot and call-tree counts must equal received samples.");
    }

    /// <summary>
    /// 检查热点与调用树的样本计数是否可由接收样本总数解释。
    /// </summary>
    private static bool IsConsistent(ExecutionProfile profile) =>
        profile.Hotspots.Sum(static hotspot => hotspot.ExclusiveSampleCount) == profile.ReceivedSampleCount
        && profile.CallTreeRoots.Sum(static node => node.InclusiveSampleCount) == profile.ReceivedSampleCount;

    /// <summary>
    /// 验证不同验收阶段查询的时间范围不重叠，避免重复样本掩盖缓存或边界问题。
    /// </summary>
    private static void AssertNonOverlapping(params ExecutionProfile[] profiles)
    {
        for (var index = 1; index < profiles.Length; index++)
        {
            Assert.IsGreaterThan(
                profiles[index].TimeRange.StartAtUtc,
                profiles[index - 1].TimeRange.EndAtUtc,
                "The fixed real-application query ranges must not overlap.");
        }
    }

    /// <summary>
    /// 从热点与调用树收集可解析源码帧，并去重为验收报告中的源码命中列表。
    /// </summary>
    private static ExecutionSamplingSourceHitEvidence[] CreateSourceHits(
        IEnumerable<ExecutionProfile> profiles,
        IReadOnlyList<ExecutionSamplingPdbEvidence> pdbs)
    {
        return profiles
            .SelectMany(EnumerateFrames)
            .Where(static frame =>
                string.Equals(Path.GetFileNameWithoutExtension(frame.ModuleName), "MQTTnet.TestApp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(frame.ModuleName), "MQTTnet", StringComparison.OrdinalIgnoreCase))
            .Select(frame => CreateSourceHit(frame, pdbs))
            .Where(static hit => hit is not null)
            .Cast<ExecutionSamplingSourceHitEvidence>()
            .DistinctBy(static hit => (hit.ModuleName, hit.MethodName, hit.SourceLocation.FilePath, hit.SourceLocation.LineNumber))
            .ToArray();
    }

    /// <summary>
    /// 将带有效源码位置的执行帧转换为证据条目；缺少位置时不制造虚假命中。
    /// </summary>
    private static ExecutionSamplingSourceHitEvidence? CreateSourceHit(
        ExecutionFrame frame,
        IReadOnlyList<ExecutionSamplingPdbEvidence> pdbs)
    {
        if (frame.ModuleName is null || frame.SourceLocation is null || !File.Exists(frame.SourceLocation.FilePath))
        {
            return null;
        }

        var pdb = pdbs.FirstOrDefault(candidate =>
            string.Equals(
                Path.GetFileNameWithoutExtension(candidate.Path),
                Path.GetFileNameWithoutExtension(frame.ModuleName),
                StringComparison.OrdinalIgnoreCase));
        return pdb is null
            ? null
            : new ExecutionSamplingSourceHitEvidence(
                frame.ModuleName,
                frame.MethodName,
                frame.SourceLocation,
                pdb.Path,
                pdb.Sha256);
    }

    /// <summary>
    /// 枚举热点和递归调用树中的所有帧，以覆盖两种结果视图中的源码归因。
    /// </summary>
    private static IEnumerable<ExecutionFrame> EnumerateFrames(ExecutionProfile profile)
    {
        foreach (var hotspot in profile.Hotspots)
        {
            yield return hotspot.Frame;
        }

        var pending = new Stack<ExecutionCallTreeNode>(profile.CallTreeRoots.Reverse());
        while (pending.TryPop(out var node))
        {
            yield return node.Frame;
            for (var index = node.Children.Count - 1; index >= 0; index--)
            {
                pending.Push(node.Children[index]);
            }
        }
    }

    /// <summary>
    /// 根据真实应用验收的最小样本、源码命中和资源约束生成可报告的阈值结论。
    /// </summary>
    private static void AddAcceptanceThresholds(
        ExecutionSamplingRunEvidence evidence,
        ExecutionProfile early,
        ExecutionProfile middle,
        ExecutionProfile late,
        ExecutionProfile afterSnapshot,
        ExecutionSamplingSourceHitEvidence[] sourceHits)
    {
        evidence.Thresholds.Add("threeNonOverlappingRanges", new ExecutionSamplingThresholdDecision(
            true, "==", 3, 3, "profiles"));
        evidence.Thresholds.Add("mqttnetLocalSourceLocation", new ExecutionSamplingThresholdDecision(
            sourceHits.Length > 0, ">", sourceHits.Length, 0, "source hits"));
        evidence.Thresholds.Add("gcdumpSamplingContinuity", new ExecutionSamplingThresholdDecision(
            afterSnapshot.ReceivedSampleCount > 0, ">", afterSnapshot.ReceivedSampleCount, 0, "samples"));
        evidence.Thresholds.Add("profileCountsConsistent", new ExecutionSamplingThresholdDecision(
            IsConsistent(early) && IsConsistent(middle) && IsConsistent(late), "==", 1, 1, "boolean"));
    }

    /// <summary>
    /// 异步计算所有命中 PDB 的 SHA-256，记录符号文件与源码结果的可追溯关联。
    /// </summary>
    private static async Task<IReadOnlyList<ExecutionSamplingPdbEvidence>> CollectPdbEvidenceAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(executablePath)
            ?? throw new AssertFailedException("MQTTnet.TestApp executable must have a directory.");
        var paths = Directory.EnumerateFiles(directory, "*.pdb", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.IsNotEmpty(paths, "MQTTnet.TestApp Debug output must contain local PDB files.");
        var evidence = new List<ExecutionSamplingPdbEvidence>(paths.Length);
        foreach (var path in paths)
        {
            evidence.Add(new ExecutionSamplingPdbEvidence(path, await ComputeSha256Async(path, cancellationToken)));
        }

        return evidence;
    }

    /// <summary>
    /// 以异步顺序读取计算文件 SHA-256，避免在大 PDB 上阻塞测试线程。
    /// </summary>
    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// 收集验收主机、目标和执行采样配置的环境证据。
    /// </summary>
    private static async Task<ExecutionSamplingEnvironmentEvidence> CreateEnvironmentEvidenceAsync(
        CancellationToken cancellationToken)
    {
        var repositoryRoot = FindRepositoryRoot();
        var gitHead = await RunForOutputAsync("git", ["rev-parse", "HEAD"], repositoryRoot, cancellationToken);
        var gitStatus = await RunForOutputAsync("git", ["status", "--porcelain"], repositoryRoot, cancellationToken);
        var sdkVersion = await RunForOutputAsync("dotnet", ["--version"], repositoryRoot, cancellationToken);
        return new ExecutionSamplingEnvironmentEvidence(
            gitHead.Trim(),
            !string.IsNullOrWhiteSpace(gitStatus),
            sdkVersion.Trim(),
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown",
            Environment.ProcessorCount);
    }

    /// <summary>
    /// 自测试程序集目录向上查找仓库标识文件，用于稳定定位验收输出。
    /// </summary>
    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DotnetAnalysis.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the DotnetAnalysis repository root.");
    }

    /// <summary>
    /// 运行只读环境探测子进程并返回其标准输出；非零退出码属于验收基础环境失败。
    /// </summary>
    private static async Task<string> RunForOutputAsync(
        string fileName,
        IReadOnlyCollection<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} exited with {process.ExitCode}: {error}");
        }

        return output;
    }

    /// <summary>
    /// 在测试清理时删除本测试创建的临时目录；目录已不存在时保持幂等。
    /// </summary>
    private static void DeleteDirectoryIfPresent(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>
    /// 将一次阶段性执行分析结果与其查询耗时绑定，供证据和阈值检查共用。
    /// </summary>
    private sealed record MeasuredExecutionProfile(
        ExecutionProfile Profile,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        long AllocatedBytes,
        double DiagnosticProcessorMilliseconds,
        long PrivateMemoryBeforeBytes,
        long PrivateMemoryAfterBytes);
}
