using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

internal sealed record ExecutionSamplingProbeResult(
    long ReceivedSampleCount,
    long EventsLost,
    IReadOnlyList<string> ManagedMethodNames,
    IReadOnlyList<string> RepresentativeManagedStack,
    bool UsedTraceEventExecutionSource);

internal static class ExecutionSamplingCompatibilityProbe
{
    private static readonly JsonSerializerOptions s_indentedJson = new() { WriteIndented = true };

    public static async Task<ExecutionSamplingProbeResult> CollectAsync(
        int processId,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var managedMethodNames = new HashSet<string>(StringComparer.Ordinal);
        string[] representativeManagedStack = [];
        var usedTraceEventExecutionSource = false;
        var runtimeTargetFramework = "unknown";
        var capturedAtUtc = DateTimeOffset.UtcNow;
        var storageRoot = Path.Combine(
            Path.GetTempPath(),
            "DotnetAnalysis.Diagnostics.IntegrationTests",
            Guid.NewGuid().ToString("N"));
        long receivedSampleCount = 0;
        long eventsLost = 0;
        Exception? probeException = null;

        try
        {
            runtimeTargetFramework = ResolveRuntimeTargetFramework(processId);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
            cancellationToken.ThrowIfCancellationRequested();

            var store = new ExecutionCaptureStore(new ExecutionCaptureStorageLayout(storageRoot));
            await using (store.ConfigureAwait(false))
            {
                var runtime = new ProductionAdapterObservingRuntime();
                var sampler = new EventPipeExecutionSampler(store, runtime, TimeSpan.FromSeconds(5));
                await using (sampler.ConfigureAwait(false))
                {
                    await sampler.StartAsync(CreateTargetProcess(processId), cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        await sampler.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    }

                    if (sampler.TerminalFailure is { } terminalFailure)
                    {
                        throw terminalFailure;
                    }

                    receivedSampleCount = sampler.SuccessfulSampleCount;
                    eventsLost = sampler.LostEventCount;
                    usedTraceEventExecutionSource = runtime.UsedTraceEventExecutionSource;

                    var boundary = store.CaptureReadBoundary();
                    var rangeEnd = boundary.WrittenThroughUtc == DateTimeOffset.MaxValue
                        ? boundary.WrittenThroughUtc
                        : boundary.WrittenThroughUtc.AddTicks(1);
                    var range = new ExecutionTimeRange(boundary.StartedAtUtc, rangeEnd);
                    var stacksById = new Dictionary<int, IReadOnlyList<ExecutionFrameReference>>();
                    await foreach (var sample in store.ReadAsync(range, boundary, cancellationToken)
                        .WithCancellation(cancellationToken)
                        .ConfigureAwait(false))
                    {
                        if (!stacksById.TryGetValue(sample.StackId, out var frames))
                        {
                            frames = await store.GetStackFramesAsync(sample.StackId, cancellationToken).ConfigureAwait(false);
                            stacksById.Add(sample.StackId, frames);
                        }

                        var methodNames = frames
                            .Select(static frame => frame.Descriptor.MethodName)
                            .ToArray();
                        foreach (var methodName in methodNames)
                        {
                            managedMethodNames.Add(methodName);
                        }

                        if (representativeManagedStack.Length == 0 && ContainsWorkloadRootAndPath(methodNames))
                        {
                            representativeManagedStack = methodNames;
                        }
                    }
                }
            }

            return new ExecutionSamplingProbeResult(
                receivedSampleCount,
                eventsLost,
                managedMethodNames.OrderBy(static name => name, StringComparer.Ordinal).ToArray(),
                representativeManagedStack,
                usedTraceEventExecutionSource);
        }
        catch (Exception exception)
        {
            probeException = exception;
            throw;
        }
        finally
        {
            try
            {
                try
                {
                    await WriteEvidenceAsync(
                        capturedAtUtc,
                        runtimeTargetFramework,
                        processId,
                        receivedSampleCount,
                        eventsLost,
                        managedMethodNames.OrderBy(static name => name, StringComparer.Ordinal).Take(20).ToArray(),
                        representativeManagedStack,
                        usedTraceEventExecutionSource,
                        probeException).ConfigureAwait(false);
                }
                catch when (probeException is not null)
                {
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(storageRoot))
                    {
                        Directory.Delete(storageRoot, recursive: true);
                    }
                }
                catch when (probeException is not null)
                {
                }
            }
        }
    }

    private static TargetProcess CreateTargetProcess(int processId)
    {
        using var process = Process.GetProcessById(processId);
        return new TargetProcess(
            process.Id,
            new DateTimeOffset(process.StartTime.ToUniversalTime()),
            process.ProcessName,
            process.MainModule?.FileName);
    }

    private static bool ContainsWorkloadRootAndPath(IReadOnlyList<string> methodNames) =>
        methodNames.Any(static name => name.Contains("ExecutionSamplingWorkload.RunWorker(", StringComparison.Ordinal))
        && methodNames.Any(static name => name.Contains("ExecutionSamplingWorkload.Path", StringComparison.Ordinal));

    private static string ResolveRuntimeTargetFramework(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var executablePath = process.MainModule?.FileName;
            var targetFramework = executablePath is null
                ? null
                : Path.GetFileName(Path.GetDirectoryName(executablePath));
            return targetFramework is not null && targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase)
                ? targetFramework
                : "unknown";
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "unknown";
        }
    }

    private static Task WriteEvidenceAsync(
        DateTimeOffset capturedAtUtc,
        string runtimeTargetFramework,
        int processId,
        long receivedSampleCount,
        long eventsLost,
        IReadOnlyList<string> managedMethodNames,
        IReadOnlyList<string> representativeManagedStack,
        bool usedTraceEventExecutionSource,
        Exception? exception)
    {
        var artifactDirectory = Path.Combine(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
            "TestResults",
            $"ExecutionSampling-{capturedAtUtc.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture)}");
        Directory.CreateDirectory(artifactDirectory);
        var artifact = new
        {
            capturedAtUtc,
            runtimeTargetFramework,
            pid = processId,
            receivedSampleCount,
            eventsLost,
            managedMethodNames = managedMethodNames.Take(20).ToArray(),
            representativeManagedStack,
            usedTraceEventExecutionSource,
            targetMethodObserved = managedMethodNames.Any(name => name.Contains("ExecutionSamplingWorkload", StringComparison.Ordinal)),
            exception = exception?.ToString()
        };
        return File.WriteAllTextAsync(
            Path.Combine(artifactDirectory, "compatibility-probe.json"),
            JsonSerializer.Serialize(artifact, s_indentedJson));
    }

    private sealed class ProductionAdapterObservingRuntime : IEventPipeExecutionRuntime
    {
        private readonly DiagnosticsClientExecutionSamplingRuntime _runtime = new();

        public bool UsedTraceEventExecutionSource { get; private set; }

        public IEventPipeExecutionSession StartSession(
            int processId,
            IReadOnlyCollection<Microsoft.Diagnostics.NETCore.Client.EventPipeProvider> providers,
            bool requestRundown,
            int circularBufferMegabytes) =>
            _runtime.StartSession(processId, providers, requestRundown, circularBufferMegabytes);

        public IEventPipeExecutionTraceSource CreateTraceSource(IEventPipeExecutionSession session)
        {
            var source = _runtime.CreateTraceSource(session);
            UsedTraceEventExecutionSource =
                source is DiagnosticsClientExecutionSamplingRuntime.TraceEventExecutionTraceSource;
            return source;
        }
    }
}
