using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

internal static class ExecutionSamplingGate
{
    public static bool IsEnabled(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}

internal sealed record ExecutionSamplingThresholdDecision(
    bool Passed,
    string Comparison,
    double Actual,
    double Limit,
    string Unit);

internal static class ExecutionSamplingThresholds
{
    public static double GetTargetThroughputDropPercent(double baselineMedian, double attachedMedian) =>
        baselineMedian <= 0
            ? 100
            : (baselineMedian - attachedMedian) * 100d / baselineMedian;

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

    public static ExecutionSamplingEvidenceWriter Create(string runKind, DateTimeOffset startedAtUtc) =>
        Create(
            Path.Combine(FindRepositoryRoot(), "TestResults"),
            runKind,
            startedAtUtc);

    internal static ExecutionSamplingEvidenceWriter Create(
        string artifactRoot,
        string runKind,
        DateTimeOffset startedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(runKind);

        var normalizedKind = runKind.Trim().ToLowerInvariant();
        if (normalizedKind is not ("benchmark" or "stress"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(runKind),
                runKind,
                "Execution sampling evidence kind must be benchmark or stress.");
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
        AddIfEmpty(evidence.TargetThroughput, nameof(evidence.TargetThroughput), emptyRawMeasurements);
        AddIfEmpty(evidence.DiagnosticResources, nameof(evidence.DiagnosticResources), emptyRawMeasurements);
        AddIfEmpty(evidence.StorageSizeBytes, nameof(evidence.StorageSizeBytes), emptyRawMeasurements);
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
        AddIfEmpty(evidence.ConcurrentQueries, nameof(evidence.ConcurrentQueries), emptyRawMeasurements);
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

    public List<string> Notes { get; } = [];

    public Dictionary<string, ExecutionSamplingThresholdDecision> Thresholds { get; } =
        new(StringComparer.Ordinal);

    public static ExecutionSamplingRunEvidence Create(string runKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runKind);
        return new ExecutionSamplingRunEvidence(runKind.Trim().ToLowerInvariant())
        {
            StartedAtUtc = DateTimeOffset.UtcNow
        };
    }
}

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

internal sealed record ExecutionSamplingTargetEvidence(
    string ExecutablePath,
    string WorkloadAssemblyPath,
    string TargetFramework,
    int ProcessId,
    DateTimeOffset StartTimeUtc);

internal sealed record ExecutionSamplingConfigurationEvidence(
    int WorkerCount,
    int PathCount,
    string SamplingFrequency,
    double WarmupSeconds,
    double MeasurementSeconds,
    double ThroughputRoundSeconds,
    double ResourceIntervalSeconds,
    int RandomSeed);

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

internal sealed record ExecutionSamplingConcurrentQueryBatch(
    int Sequence,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int EnteredQueryCount,
    IReadOnlyList<ExecutionSamplingConcurrentQueryOutcome> Outcomes);

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

internal sealed record ExecutionSamplingExceptionEvidence(
    string Phase,
    string Type,
    string Message,
    string Detail,
    DateTimeOffset ObservedAtUtc)
{
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
