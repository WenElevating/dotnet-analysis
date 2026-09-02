using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;
using Microsoft.Extensions.Logging;

namespace DotnetAnalysis.Application.Snapshots;

public sealed class MemorySnapshotOperation
{
    private const string AnalyzeStage = "Analyze";
    private const string RetryAnalysisStage = "RetryAnalysis";

    private static readonly Action<ILogger, MemorySnapshotId, string, DiagnosticsErrorCode, Exception?> s_stageFailed =
        LoggerMessage.Define<MemorySnapshotId, string, DiagnosticsErrorCode>(
            LogLevel.Error,
            new EventId(1, "MemorySnapshotStageFailed"),
            "Memory snapshot {SnapshotId} failed during {Stage} with {ErrorCode}.");

    private static readonly Action<ILogger, string, Exception?> s_eventPublishFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2, "MemorySnapshotEventPublishFailed"),
            "Could not publish snapshot analysis event {EventType}.");

    private readonly object _syncRoot = new();
    private readonly IMemorySnapshotAnalysisService _analysisService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MemorySnapshotOperation> _logger;
    private readonly IEventBus? _eventBus;
    private readonly ProcessDiagnosticsSessionId? _sessionId;

    public MemorySnapshotOperation(
        MemorySnapshot snapshot,
        IMemorySnapshotAnalysisService analysisService,
        TimeProvider timeProvider,
        ILogger<MemorySnapshotOperation> logger)
        : this(snapshot, analysisService, null, null, timeProvider, logger)
    {
    }

    public MemorySnapshotOperation(
        MemorySnapshot snapshot,
        IMemorySnapshotAnalysisService analysisService,
        IEventBus? eventBus,
        ProcessDiagnosticsSessionId? sessionId,
        TimeProvider timeProvider,
        ILogger<MemorySnapshotOperation> logger)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _analysisService = analysisService ?? throw new ArgumentNullException(nameof(analysisService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _eventBus = eventBus;
        _sessionId = sessionId;
        State = snapshot.State;
    }

    public MemorySnapshot Snapshot { get; private set; }

    public MemorySnapshotState State { get; private set; }

    public Task<MemorySnapshotAnalysis> AnalyzeAsync(CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            if (State is not MemorySnapshotState.Analyzing)
            {
                throw new InvalidOperationException($"Snapshot {Snapshot.Id} cannot be analyzed from state {State}.");
            }
        }

        return AnalyzeCoreAsync(AnalyzeStage, cancellationToken);
    }

    public Task<MemorySnapshotAnalysis> RetryAnalysisAsync(CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            if (State is not MemorySnapshotState.Failed)
            {
                throw new InvalidOperationException($"Snapshot {Snapshot.Id} cannot retry analysis from state {State}.");
            }

            SetSnapshotState(MemorySnapshotState.Analyzing);
        }

        return AnalyzeCoreAsync(RetryAnalysisStage, cancellationToken);
    }

    public Task<IReadOnlyList<MemoryObjectInfo>> GetObjectsAsync(
        TypeIdentity type,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _analysisService.GetObjectsAsync(Snapshot, type, cancellationToken);
    }

    public Task<MemoryReferencePath?> GetReferencePathAsync(
        ulong objectAddress,
        CancellationToken cancellationToken)
    {
        return _analysisService.GetReferencePathAsync(Snapshot, objectAddress, cancellationToken);
    }

    private async Task<MemorySnapshotAnalysis> AnalyzeCoreAsync(
        string stage,
        CancellationToken cancellationToken)
    {
        _ = PublishEventAsync(new MemorySnapshotAnalysisStarted(
            _sessionId ?? default,
            Snapshot.Id,
            _timeProvider.GetUtcNow(),
            nameof(MemorySnapshotOperation))).AsTask();

        try
        {
            var analysis = await _analysisService.AnalyzeAsync(Snapshot, cancellationToken).ConfigureAwait(false);
            var readySnapshot = WithState(analysis.Snapshot, MemorySnapshotState.Ready);
            var readyAnalysis = ReferenceEquals(readySnapshot, analysis.Snapshot)
                ? analysis
                : new MemorySnapshotAnalysis(readySnapshot, analysis.Types, analysis.AllocationProfile);

            lock (_syncRoot)
            {
                Snapshot = readySnapshot;
                State = MemorySnapshotState.Ready;
            }

            _ = _timeProvider.GetUtcNow();
            _ = PublishEventAsync(new MemorySnapshotAnalysisCompleted(
                _sessionId ?? default,
                readySnapshot.Id,
                _timeProvider.GetUtcNow(),
                nameof(MemorySnapshotOperation))).AsTask();
            return readyAnalysis;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            var diagnosticsException = new DiagnosticsException(
                DiagnosticsErrorCode.CaptureCancelled,
                "Memory snapshot analysis was cancelled.",
                exception);
            MarkFailed(stage, diagnosticsException, exception, MemorySnapshotState.Canceled);
            _ = PublishEventAsync(new MemorySnapshotAnalysisFailed(
                _sessionId ?? default,
                Snapshot.Id,
                diagnosticsException.ErrorCode,
                diagnosticsException.Message,
                _timeProvider.GetUtcNow(),
                nameof(MemorySnapshotOperation))).AsTask();
            throw diagnosticsException;
        }
        catch (DiagnosticsException exception)
        {
            MarkFailed(stage, exception, exception, MemorySnapshotState.Failed);
            _ = PublishEventAsync(new MemorySnapshotAnalysisFailed(
                _sessionId ?? default,
                Snapshot.Id,
                exception.ErrorCode,
                "Memory snapshot analysis failed.",
                _timeProvider.GetUtcNow(),
                nameof(MemorySnapshotOperation))).AsTask();
            throw;
        }
        catch (Exception exception)
        {
            var diagnosticsException = new DiagnosticsException(
                DiagnosticsErrorCode.CaptureFailed,
                "Memory snapshot analysis failed.",
                exception);
            MarkFailed(stage, diagnosticsException, exception, MemorySnapshotState.Failed);
            _ = PublishEventAsync(new MemorySnapshotAnalysisFailed(
                _sessionId ?? default,
                Snapshot.Id,
                diagnosticsException.ErrorCode,
                diagnosticsException.Message,
                _timeProvider.GetUtcNow(),
                nameof(MemorySnapshotOperation))).AsTask();
            throw diagnosticsException;
        }
    }

    private void MarkFailed(
        string stage,
        DiagnosticsException diagnosticsException,
        Exception logException,
        MemorySnapshotState state)
    {
        s_stageFailed(_logger, Snapshot.Id, stage, diagnosticsException.ErrorCode, logException);
        lock (_syncRoot)
        {
            SetSnapshotState(state);
        }
    }

    private void SetSnapshotState(MemorySnapshotState state)
    {
        Snapshot = WithState(Snapshot, state);
        State = state;
    }

    private static MemorySnapshot WithState(MemorySnapshot snapshot, MemorySnapshotState state)
    {
        return snapshot.State == state
            ? snapshot
            : snapshot.MoveTo(state);
    }

    private async ValueTask PublishEventAsync(IApplicationEvent applicationEvent)
    {
        if (_eventBus is null || _sessionId is null)
        {
            return;
        }

        try
        {
            await _eventBus.PublishAsync(applicationEvent, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            s_eventPublishFailed(_logger, applicationEvent.GetType().Name, exception);
        }
    }
}
