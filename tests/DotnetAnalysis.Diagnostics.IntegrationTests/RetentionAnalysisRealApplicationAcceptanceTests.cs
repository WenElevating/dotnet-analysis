using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

/// <summary>
/// 对外部显式提供的 MQTTnet.TestApp 执行保留分析真实应用验收；测试只附加既有目标，
/// 不负责启动、驱动或终止该业务应用。
/// </summary>
[TestClass]
[DoNotParallelize]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "测试名称描述真实应用验收行为。")]
public sealed class RetentionAnalysisRealApplicationAcceptanceTests
{
    private const string AcceptanceGate = "DOTNET_ANALYSIS_RUN_REAL_APPLICATION_RETENTION_ACCEPTANCE";
    private const string TargetProcessIdVariable = "DOTNET_ANALYSIS_REAL_APPLICATION_PID";

    /// <summary>
    /// 复用证据文件的格式化选项，避免每次验收创建新的序列化配置。
    /// </summary>
    private static readonly JsonSerializerOptions s_indentedJsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly TestContext _testContext;

    /// <summary>
    /// 初始化 MSTest 提供的取消令牌和验收上下文。
    /// </summary>
    /// <param name="testContext">当前测试运行上下文。</param>
    public RetentionAnalysisRealApplicationAcceptanceTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    /// <summary>
    /// 附加真实 MQTTnet.TestApp 并捕获保留函数分析快照，验证快照可解析且捕获不会结束目标进程。
    /// </summary>
    [TestMethod]
    [TestCategory("RetentionAnalysisRealApplicationAcceptance")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task RetentionAnalysisRealApplicationAcceptance_CapturesMqttnetTestAppAndKeepsItRunning()
    {
        EnsureAcceptanceGateEnabled();
        var cancellationToken = _testContext.CancellationToken;
        var expectedProcessId = ReadTargetProcessId();
        using var targetProcess = GetRequiredTargetProcess(expectedProcessId);
        ValidateMqttnetTestApp(targetProcess);
        var startedAtUtc = new DateTimeOffset(targetProcess.StartTime.ToUniversalTime());
        var snapshotRoot = Path.Combine(
            Path.GetTempPath(),
            "DotnetAnalysis.Diagnostics.IntegrationTests",
            "MqttnetRetentionAnalysisAcceptance",
            Guid.NewGuid().ToString("N"));
        var evidenceRoot = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.RetentionAnalysisRealApplicationEvidence");
        Directory.CreateDirectory(evidenceRoot);

        try
        {
            var layout = new SnapshotStorageLayout(snapshotRoot);
            var diagnostics = new WindowsProcessDiagnostics(
                new InProcessEventBus(NullLogger<InProcessEventBus>.Instance),
                TimeProvider.System,
                snapshotLayout: layout);
            var enumeratedTarget = (await diagnostics.GetProcessesAsync(cancellationToken).ConfigureAwait(false))
                .SingleOrDefault(candidate => candidate.ProcessId == expectedProcessId)
                ?? throw new AssertFailedException("The configured MQTTnet.TestApp PID was not returned by GetProcessesAsync().");
            Assert.AreEqual(startedAtUtc, enumeratedTarget.StartedAtUtc, "PID identity must include the exact process start time.");

            await using var session = await diagnostics.AttachAsync(enumeratedTarget, cancellationToken).ConfigureAwait(false);
            var captureStopwatch = Stopwatch.StartNew();
            var snapshot = await session.CaptureSnapshotAsync(
                MemorySnapshotCaptureMode.RetentionAnalysis,
                cancellationToken).ConfigureAwait(false);
            captureStopwatch.Stop();
            Assert.AreEqual(ProcessDiagnosticsSessionState.Monitoring, session.State);

            var snapshotPath = layout.GetFinalRetentionHeapPath(snapshot.Id);
            Assert.IsTrue(File.Exists(snapshotPath), "Real-application retention capture must persist a retentionheap snapshot.");
            var snapshotBytes = new FileInfo(snapshotPath).Length;
            Assert.IsGreaterThan(0L, snapshotBytes);
            var index = await RetentionHeapSnapshot.ReadIndexAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            var capturedObjectCount = index.TypeSummaries.Sum(summary => summary.ObjectCount);
            Assert.IsGreaterThan(0L, capturedObjectCount);
            var stackRootPath = FindStackRootRetentionPath(index, cancellationToken);
            Assert.IsNotNull(stackRootPath, "The real MQTTnet process did not yield an object retention path with verified stack-root function evidence.");
            Assert.AreEqual(MemoryRootKind.Stack, stackRootPath.Root.Kind);
            Assert.IsFalse(string.IsNullOrWhiteSpace(stackRootPath.Root.FunctionName));

            targetProcess.Refresh();
            Assert.IsFalse(targetProcess.HasExited, "MQTTnet.TestApp exited during RetentionAnalysis capture.");
            Assert.AreEqual(startedAtUtc, new DateTimeOffset(targetProcess.StartTime.ToUniversalTime()));
            await session.EndAsync(cancellationToken).ConfigureAwait(false);
            Assert.AreEqual(ProcessDiagnosticsSessionState.Ended, session.State);

            var evidence = new
            {
                targetProcessId = expectedProcessId,
                targetStartedAtUtc = startedAtUtc,
                targetExecutablePath = targetProcess.MainModule?.FileName,
                snapshotId = snapshot.Id.ToString(),
                captureMilliseconds = captureStopwatch.Elapsed.TotalMilliseconds,
                snapshotBytes,
                capturedObjectCount,
                stackRootFunctionName = stackRootPath.Root.FunctionName,
                stackRootModuleName = stackRootPath.Root.ModuleName,
                retentionPathObjectCount = stackRootPath.Objects.Count,
                retentionPathObjectAddresses = stackRootPath.Objects.Select(item => $"0x{item.Address:X}").ToArray(),
                targetWorkingSetBytes = targetProcess.WorkingSet64,
                capturedAtUtc = DateTimeOffset.UtcNow,
            };
            var evidencePath = Path.Combine(
                evidenceRoot,
                $"MqttnetRetentionAnalysis-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.json");
            await File.WriteAllTextAsync(
                evidencePath,
                JsonSerializer.Serialize(evidence, s_indentedJsonOptions),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(snapshotRoot))
            {
                Directory.Delete(snapshotRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// 强制要求人工或自动化显式启用真实应用验收，避免常规集成测试依赖外部 PID。
    /// </summary>
    private static void EnsureAcceptanceGateEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(AcceptanceGate), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive($"Set {AcceptanceGate}=true after starting MQTTnet.TestApp.");
        }
    }

    /// <summary>
    /// 读取并验证外部提供的 MQTTnet.TestApp 进程标识。
    /// </summary>
    /// <returns>有效的目标进程标识。</returns>
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
    /// 获取仍存活的外部目标进程；避免 PID 配置错误被解释成 Profiler 失败。
    /// </summary>
    /// <param name="processId">外部提供的进程标识。</param>
    /// <returns>仍运行的目标进程。</returns>
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
    /// 验证进程名称、映像路径和 CoreCLR 模块，防止验收误附着到无关的同 PID 历史对象。
    /// </summary>
    /// <param name="process">待验证的外部进程。</param>
    private static void ValidateMqttnetTestApp(Process process)
    {
        Assert.AreEqual("MQTTnet.TestApp", process.ProcessName, ignoreCase: true);
        var executablePath = process.MainModule?.FileName
            ?? throw new AssertFailedException("MQTTnet.TestApp main module path must be readable.");
        Assert.AreEqual("MQTTnet.TestApp.exe", Path.GetFileName(executablePath), ignoreCase: true);
        Assert.IsTrue(process.Modules.Cast<ProcessModule>().Any(module =>
            string.Equals(module.ModuleName, "System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase)),
            "MQTTnet.TestApp must be a CoreCLR process.");
    }

    /// <summary>
    /// 在有限候选对象集合中查找带 CLR 验证函数名的栈根保留路径；限制扫描量以保持真实应用验收可预测。
    /// </summary>
    /// <param name="index">已验证的保留快照索引。</param>
    /// <param name="cancellationToken">取消当前路径查询的令牌。</param>
    /// <returns>首条含函数证据的栈根路径；没有可验证证据时返回空。</returns>
    private static MemoryRetentionPath? FindStackRootRetentionPath(SnapshotIndex index, CancellationToken cancellationToken)
    {
        const int maximumObjectsToInspect = 4_096;
        const int maximumObjectsPerType = 128;
        var inspectedCount = 0;
        foreach (var type in index.TypeSummaries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageSize = (int)Math.Min(type.ObjectCount, maximumObjectsPerType);
            if (pageSize == 0)
            {
                continue;
            }

            var page = index.GetPage(type.Type, offset: 0, pageSize);
            foreach (var item in page.Objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (inspectedCount++ >= maximumObjectsToInspect)
                {
                    return null;
                }

                var result = index.GetRetentionPaths(item.Address, maxPathCount: 16, cancellationToken);
                var stackRootPath = result?.Paths.FirstOrDefault(path =>
                    path.Root.Kind is MemoryRootKind.Stack
                    && !string.IsNullOrWhiteSpace(path.Root.FunctionName));
                if (stackRootPath is not null)
                {
                    return stackRootPath;
                }
            }
        }

        return null;
    }
}
