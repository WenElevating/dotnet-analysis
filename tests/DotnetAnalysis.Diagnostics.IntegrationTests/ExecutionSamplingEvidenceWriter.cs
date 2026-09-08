using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

/// <summary>
/// 统一解析显式环境变量门禁，防止长时性能与真实目标测试意外进入常规测试运行。
/// </summary>
internal static class ExecutionSamplingGate
{
    /// <summary>
    /// 仅接受不区分大小写的 <c>true</c>，避免任意非空环境变量误启用高成本测试。
    /// </summary>
    public static bool IsEnabled(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 记录一次性能门禁判定、实际测量值与适用阈值，供证据文件和失败消息复用。
/// </summary>
internal sealed record ExecutionSamplingThresholdDecision(
    bool Passed,
    string Comparison,
    double Actual,
    double Limit,
    string Unit);

/// <summary>
/// 集中定义执行采样性能证据的可重复阈值计算和完整性校验。
/// </summary>
internal static class ExecutionSamplingThresholds
{
    /// <summary>
    /// 计算附着采样相对基线的吞吐下降百分比，并对零基线返回确定失败值。
    /// </summary>
    public static double GetTargetThroughputDropPercent(double baselineMedian, double attachedMedian) =>
        baselineMedian <= 0
            ? 100
            : (baselineMedian - attachedMedian) * 100d / baselineMedian;

    /// <summary>
    /// 判断快照是否同时完成分析且保留了可读取的转储文件。
    /// </summary>
    public static bool IsUsableSnapshot(ExecutionSamplingSnapshotMeasurement snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Exception is null
            && snapshot.SamplingContinued
            && snapshot.LostEventCount == 0
            && !string.IsNullOrWhiteSpace(snapshot.SnapshotId)
            && string.Equals(
                snapshot.State,
                MemorySnapshotState.Analyzing.ToString(),
                StringComparison.Ordinal)
            && snapshot.FileSizeBytes > 0;
    }

    /// <summary>
    /// 汇总所有未通过的阈值判定，避免性能门禁只报告首个症状。
    /// </summary>
    public static string[] GetFailures(
        IReadOnlyDictionary<string, ExecutionSamplingThresholdDecision> thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);

        return thresholds
            .Where(static threshold => !threshold.Value.Passed)
            .Select(static threshold => threshold.Key)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
    }
}

/// <summary>
/// 将执行采样性能、压力或真实应用验收的原始测量与环境上下文写为可追溯 JSON 证据。
/// </summary>
internal sealed class ExecutionSamplingEvidenceWriter
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private ExecutionSamplingEvidenceWriter(string artifactDirectory, string evidencePath)
    {
        ArtifactDirectory = artifactDirectory;
        EvidencePath = evidencePath;
    }

    public string ArtifactDirectory { get; }

    public string EvidencePath { get; }

    /// <summary>
    /// 使用仓库默认证据目录创建写入器，并以调用方提供的运行类型和开始时间命名输出。
    /// </summary>
    public static ExecutionSamplingEvidenceWriter Create(string runKind, DateTimeOffset startedAtUtc) =>
        Create(
            Path.Combine(FindRepositoryRoot(), "TestResults"),
            runKind,
            startedAtUtc);

    /// <summary>
    /// 为测试注入临时输出目录，隔离证据路径定位逻辑。
    /// </summary>
    internal static ExecutionSamplingEvidenceWriter Create(
        string artifactRoot,
        string runKind,
        DateTimeOffset startedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(runKind);

        var normalizedKind = runKind.Trim().ToLowerInvariant();
        if (normalizedKind is not ("benchmark" or "stress" or "mqttnet-real-application-acceptance"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(runKind),
                runKind,
                "Execution sampling evidence kind must be benchmark, stress, or mqttnet-real-application-acceptance.");
        }

        var timestamp = startedAtUtc
            .ToUniversalTime()
            .ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        var artifactDirectory = Path.Combine(
            Path.GetFullPath(artifactRoot),
            $"ExecutionSampling-{timestamp}");
        var evidencePath = Path.Combine(artifactDirectory, $"{normalizedKind}.json");
        return new ExecutionSamplingEvidenceWriter(artifactDirectory, evidencePath);
    }

    /// <summary>
    /// 先校验证据完整性，再异步写入格式化 JSON，确保成功运行不会产生无法复核的摘要。
    /// </summary>
    public async Task WriteAsync(
        ExecutionSamplingRunEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        Validate(evidence);

        Directory.CreateDirectory(ArtifactDirectory);
        var temporaryPath = Path.Combine(
            ArtifactDirectory,
            $".{Path.GetFileName(EvidencePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(evidence, s_jsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, EvidencePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>
    /// 强制成功、失败、压力和真实应用证据各自所需的原始数组与可追溯上下文。
    /// </summary>
    private static void Validate(ExecutionSamplingRunEvidence evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.RunKind);
        if (evidence.Exceptions.Count > 0)
        {
            return;
        }

        var missingContext = new List<string>();
        AddIfNull(evidence.Environment, nameof(evidence.Environment), missingContext);
        AddIfNull(evidence.Target, nameof(evidence.Target), missingContext);
        AddIfNull(evidence.Configuration, nameof(evidence.Configuration), missingContext);
        AddIfNull(evidence.CompletedAtUtc, nameof(evidence.CompletedAtUtc), missingContext);
        if (missingContext.Count > 0)
        {
            throw new ArgumentException(
                $"Successful execution sampling evidence must contain traceability context: {string.Join(", ", missingContext)}.",
                nameof(evidence));
        }

        var emptyRawMeasurements = new List<string>();
        AddIfEmpty(evidence.ReceivedSampleCounts, nameof(evidence.ReceivedSampleCounts), emptyRawMeasurements);
        AddIfEmpty(evidence.LostEventCounts, nameof(evidence.LostEventCounts), emptyRawMeasurements);
        AddIfEmpty(evidence.DiagnosticResources, nameof(evidence.DiagnosticResources), emptyRawMeasurements);
        if (!string.Equals(evidence.RunKind, "mqttnet-real-application-acceptance", StringComparison.Ordinal))
        {
            AddIfEmpty(evidence.TargetThroughput, nameof(evidence.TargetThroughput), emptyRawMeasurements);
            AddIfEmpty(evidence.StorageSizeBytes, nameof(evidence.StorageSizeBytes), emptyRawMeasurements);
        }
        if (string.Equals(evidence.RunKind, "stress", StringComparison.Ordinal))
        {
            AddIfEmpty(
                evidence.MinuteCadenceLagSeconds,
                nameof(evidence.MinuteCadenceLagSeconds),
                emptyRawMeasurements);
            AddIfEmpty(
                evidence.TenMinuteCheckpointLagSeconds,
                nameof(evidence.TenMinuteCheckpointLagSeconds),
                emptyRawMeasurements);
        }

        AddIfEmpty(evidence.Queries, nameof(evidence.Queries), emptyRawMeasurements);
        if (!string.Equals(evidence.RunKind, "mqttnet-real-application-acceptance", StringComparison.Ordinal))
        {
            AddIfEmpty(evidence.ConcurrentQueries, nameof(evidence.ConcurrentQueries), emptyRawMeasurements);
        }
        AddIfEmpty(evidence.Snapshots, nameof(evidence.Snapshots), emptyRawMeasurements);
        if (emptyRawMeasurements.Count > 0)
        {
            throw new ArgumentException(
                $"Successful execution sampling evidence must contain raw measurement arrays: {string.Join(", ", emptyRawMeasurements)}.",
                nameof(evidence));
        }

        if (evidence.Thresholds.Count == 0)
        {
            throw new ArgumentException(
                "Successful execution sampling evidence must contain threshold decisions.",
                nameof(evidence));
        }

        if (string.Equals(evidence.RunKind, "mqttnet-real-application-acceptance", StringComparison.Ordinal))
        {
            if (evidence.RealApplication is null)
            {
                throw new ArgumentException(
                    "Successful real application acceptance evidence must contain application identity.",
                    nameof(evidence));
            }

            AddIfEmpty(evidence.Profiles, nameof(evidence.Profiles), emptyRawMeasurements);
            AddIfEmpty(evidence.SourceHits, nameof(evidence.SourceHits), emptyRawMeasurements);
            if (emptyRawMeasurements.Count > 0)
            {
                throw new ArgumentException(
                    $"Successful real application acceptance evidence must contain raw acceptance arrays: {string.Join(", ", emptyRawMeasurements)}.",
                    nameof(evidence));
            }
        }
    }

    private static void AddIfNull<T>(T? value, string name, List<string> missingContext)
    {
        if (value is null)
        {
            missingContext.Add(name);
        }
    }

    private static void AddIfEmpty<T>(
        IReadOnlyCollection<T> values,
        string name,
        List<string> emptyRawMeasurements)
    {
        if (values.Count == 0)
        {
            emptyRawMeasurements.Add(name);
        }
    }

    /// <summary>
    /// 自测试输出目录向上定位仓库根目录，保证证据落点不依赖当前工作目录。
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var candidate = new DirectoryInfo(AppContext.BaseDirectory);
        while (candidate is not null)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "DotnetAnalysis.sln")))
            {
                return candidate.FullName;
            }

            candidate = candidate.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the DotnetAnalysis repository root.");
    }
}

/// <summary>
/// 汇集单次执行采样验证的环境、配置、原始测量、判定结果和异常上下文。
/// </summary>
internal sealed class ExecutionSamplingRunEvidence
{
    private ExecutionSamplingRunEvidence(string runKind)
    {
        RunKind = runKind;
    }

    public string SchemaVersion { get; } = "1.0";

    public string RunKind { get; }

    public DateTimeOffset StartedAtUtc { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public ExecutionSamplingEnvironmentEvidence? Environment { get; set; }

    public ExecutionSamplingTargetEvidence? Target { get; set; }

    public ExecutionSamplingConfigurationEvidence? Configuration { get; set; }

    public List<long> ReceivedSampleCounts { get; } = [];

    public List<long> LostEventCounts { get; } = [];

    public List<ExecutionSamplingThroughputMeasurement> TargetThroughput { get; } = [];

    public List<ExecutionSamplingResourceMeasurement> DiagnosticResources { get; } = [];

    public List<long> StorageSizeBytes { get; } = [];

    public List<double> MinuteCadenceLagSeconds { get; } = [];

    public List<double> TenMinuteCheckpointLagSeconds { get; } = [];

    public List<ExecutionSamplingQueryMeasurement> Queries { get; } = [];

    public List<ExecutionSamplingConcurrentQueryBatch> ConcurrentQueries { get; } = [];

    public List<ExecutionSamplingSnapshotMeasurement> Snapshots { get; } = [];

    public List<ExecutionSamplingExceptionEvidence> Exceptions { get; } = [];

    public ExecutionSamplingRealApplicationEvidence? RealApplication { get; set; }

    public List<ExecutionSamplingProfileEvidence> Profiles { get; } = [];

    public List<ExecutionSamplingSourceHitEvidence> SourceHits { get; } = [];

    public List<string> Notes { get; } = [];

    public Dictionary<string, ExecutionSamplingThresholdDecision> Thresholds { get; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 创建带统一架构、运行时和采样器版本字段的初始证据对象。
    /// </summary>
    public static ExecutionSamplingRunEvidence Create(string runKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runKind);
        return new ExecutionSamplingRunEvidence(runKind.Trim().ToLowerInvariant())
        {
            StartedAtUtc = DateTimeOffset.UtcNow
        };
    }
}

/// <summary>
/// 保存运行验证时的操作系统、处理器和 .NET 环境信息。
/// </summary>
internal sealed record ExecutionSamplingEnvironmentEvidence(
    string GitHead,
    bool HasUncommittedChanges,
    string DotnetSdkVersion,
    string RuntimeVersion,
    string OperatingSystem,
    string OperatingSystemArchitecture,
    string ProcessArchitecture,
    string ProcessorName,
    int LogicalProcessorCount);

/// <summary>
/// 保存被附着目标的进程、运行时和工作负载标识。
/// </summary>
internal sealed record ExecutionSamplingTargetEvidence(
    string ExecutablePath,
    string WorkloadAssemblyPath,
    string TargetFramework,
    int ProcessId,
    DateTimeOffset StartTimeUtc);

/// <summary>
/// 保存采样、查询和工作负载配置，使性能证据可在相同条件下复现。
/// </summary>
internal sealed record ExecutionSamplingConfigurationEvidence(
    int WorkerCount,
    int PathCount,
    string SamplingFrequency,
    double WarmupSeconds,
    double MeasurementSeconds,
    double ThroughputRoundSeconds,
    double ResourceIntervalSeconds,
    int RandomSeed);

/// <summary>
/// 表示一个吞吐测量窗口内的完成操作数、持续时间和计算后的速率。
/// </summary>
internal sealed record ExecutionSamplingThroughputMeasurement(
    string Mode,
    int Sequence,
    int ProcessId,
    DateTimeOffset ProcessStartTimeUtc,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    double DurationSeconds,
    long StartingCompletedOperations,
    long EndingCompletedOperations,
    long CompletedOperations);

/// <summary>
/// 表示测量窗口内诊断端资源占用和目标进程工作集观察值。
/// </summary>
internal sealed record ExecutionSamplingResourceMeasurement(
    string Phase,
    DateTimeOffset ObservedAtUtc,
    double ElapsedSeconds,
    double TotalProcessorMilliseconds,
    double IntervalCoreUsage,
    long PrivateMemoryBytes,
    int HandleCount,
    long StorageBytes,
    long TargetCompletedOperations);

/// <summary>
/// 表示一次执行分析查询的范围、持续时间、缓存命中和结果计数。
/// </summary>
internal sealed record ExecutionSamplingQueryMeasurement(
    string Kind,
    int Sequence,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    DateTimeOffset RangeStartAtUtc,
    DateTimeOffset RangeEndAtUtc,
    double ElapsedMilliseconds,
    long AllocatedBytes,
    double DiagnosticProcessorMilliseconds,
    long PrivateMemoryBeforeBytes,
    long PrivateMemoryAfterBytes,
    long ReceivedSampleCount,
    long LostEventCount,
    long HotspotExclusiveSampleCount,
    long CallTreeRootInclusiveSampleCount,
    bool CountsConsistent);

/// <summary>
/// 保存并发查询批次中单个调用方的完成、取消或失败结果。
/// </summary>
internal sealed record ExecutionSamplingConcurrentQueryOutcome(
    int Sequence,
    DateTimeOffset RangeStartAtUtc,
    DateTimeOffset RangeEndAtUtc,
    bool CancellationRequested,
    string Outcome,
    long? ReceivedSampleCount,
    long? LostEventCount,
    bool? CountsConsistent,
    string? Exception);

/// <summary>
/// 汇总一批并发范围查询的排队、执行和最终一致性结果。
/// </summary>
internal sealed record ExecutionSamplingConcurrentQueryBatch(
    int Sequence,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int EnteredQueryCount,
    IReadOnlyList<ExecutionSamplingConcurrentQueryOutcome> Outcomes);

/// <summary>
/// 保存采样期间快照捕获、分析状态和转储有效性的测量结果。
/// </summary>
internal sealed record ExecutionSamplingSnapshotMeasurement(
    int Sequence,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    double ElapsedMilliseconds,
    string? SnapshotId,
    string? State,
    long? FileSizeBytes,
    double DiagnosticProcessorMilliseconds,
    long PrivateMemoryBeforeBytes,
    long PrivateMemoryAfterBytes,
    long BeforeReceivedSampleCount,
    long AfterReceivedSampleCount,
    long LostEventCount,
    bool SamplingContinued,
    string? Exception);

/// <summary>
/// 将验证阶段异常投影为可序列化的类型、消息和堆栈证据。
/// </summary>
internal sealed record ExecutionSamplingExceptionEvidence(
    string Phase,
    string Type,
    string Message,
    string Detail,
    DateTimeOffset ObservedAtUtc)
{
    /// <summary>
    /// 从阶段名称和异常创建不丢失内层异常信息的可序列化故障证据。
    /// </summary>
    public static ExecutionSamplingExceptionEvidence From(string phase, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentNullException.ThrowIfNull(exception);
        return new ExecutionSamplingExceptionEvidence(
            phase,
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message,
            exception.ToString(),
            DateTimeOffset.UtcNow);
    }
}

/// <summary>
/// 保存外部真实应用验收所需的 PID、二进制、PDB 和源码命中证据。
/// </summary>
internal sealed record ExecutionSamplingRealApplicationEvidence(
    string ProjectPath,
    string ExecutableSha256,
    IReadOnlyList<ExecutionSamplingPdbEvidence> CandidatePdbs,
    DateTimeOffset AttachedAtUtc,
    DateTimeOffset EndedAtUtc);

/// <summary>
/// 表示验收时发现的 PDB 路径及其 SHA-256 指纹。
/// </summary>
internal sealed record ExecutionSamplingPdbEvidence(string Path, string Sha256);

/// <summary>
/// 将执行分析结果关联到产生它的验收阶段。
/// </summary>
internal sealed record ExecutionSamplingProfileEvidence(string Phase, ExecutionProfile Profile);

/// <summary>
/// 保存从执行帧解析出的源码命中，便于验证本地符号归因确实可追溯。
/// </summary>
internal sealed record ExecutionSamplingSourceHitEvidence(
    string ModuleName,
    string MethodName,
    SourceLocation SourceLocation,
    string PdbPath,
    string PdbSha256);
