using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

internal sealed record ExecutionSamplingProbeResult(
    long ReceivedSampleCount,
    long EventsLost,
    IReadOnlyList<string> ManagedMethodNames);

internal static class ExecutionSamplingCompatibilityProbe
{
    private static readonly JsonSerializerOptions s_indentedJson = new() { WriteIndented = true };

    public static async Task<ExecutionSamplingProbeResult> CollectAsync(
        int processId,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        var managedMethodNames = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var runtimeTargetFramework = ResolveRuntimeTargetFramework(processId);
        var capturedAtUtc = DateTimeOffset.UtcNow;
        long receivedSampleCount = 0;
        long eventsLost = 0;
        Exception? probeException = null;

        try
        {
            var client = new DiagnosticsClient(processId);
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
            using var session = client.StartEventPipeSession(providers, requestRundown: false);
            using var source = TraceLog.CreateFromEventPipeSession(
                session,
                TraceLog.EventPipeRundownConfiguration.Enable(client));
            source.Dynamic.All += data =>
            {
                if (!string.Equals(
                        data.ProviderName,
                        SampleProfilerTraceEventParser.ProviderName,
                        StringComparison.Ordinal))
                {
                    return;
                }

                Interlocked.Increment(ref receivedSampleCount);
                var stack = data.CallStack();
                if (stack is null)
                {
                    return;
                }
                for (TraceCallStack? frame = stack; frame is not null; frame = frame.Caller)
                {
                    var methodName = frame.CodeAddress.FullMethodName;
                    if (!string.IsNullOrWhiteSpace(methodName))
                    {
                        managedMethodNames.TryAdd(methodName, 0);
                    }
                }
            };

            var processing = Task.Run(source.Process, CancellationToken.None);
            try
            {
                await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                session.Stop();
                await processing.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                eventsLost = source.EventsLost;
            }

            return new ExecutionSamplingProbeResult(
                receivedSampleCount,
                eventsLost,
                managedMethodNames.Keys.OrderBy(static name => name, StringComparer.Ordinal).ToArray());
        }
        catch (Exception exception)
        {
            probeException = exception;
            throw;
        }
        finally
        {
            await WriteEvidenceAsync(
                capturedAtUtc,
                runtimeTargetFramework,
                processId,
                receivedSampleCount,
                eventsLost,
                managedMethodNames.Keys.OrderBy(static name => name, StringComparer.Ordinal).Take(20).ToArray(),
                probeException).ConfigureAwait(false);
        }
    }

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
            targetMethodObserved = managedMethodNames.Any(name => name.Contains("ExecutionSamplingWorkload", StringComparison.Ordinal)),
            exception = exception?.ToString()
        };
        return File.WriteAllTextAsync(
            Path.Combine(artifactDirectory, "compatibility-probe.json"),
            JsonSerializer.Serialize(artifact, s_indentedJson));
    }
}
