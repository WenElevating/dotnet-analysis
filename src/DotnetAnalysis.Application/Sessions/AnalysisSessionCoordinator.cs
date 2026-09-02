using System.Collections.Concurrent;
using DotnetAnalysis.Application.Contracts;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Core.Events;
using DotnetAnalysis.Core.Sessions;

namespace DotnetAnalysis.Application.Sessions;

public sealed class AnalysisSessionCoordinator
{
    private const string Source = nameof(AnalysisSessionCoordinator);
    private const string SessionFailureCode = "analysis_session_failed";
    private const string SessionFailureMessage = "The memory analysis session could not be completed.";

    private readonly ICaptureBackend _captureBackend;
    private readonly IAnalysisService _analysisService;
    private readonly IEventBus _eventBus;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<SessionId, SessionEntry> _sessions = new();

    public AnalysisSessionCoordinator(
        ICaptureBackend captureBackend,
        IAnalysisService analysisService,
        IEventBus eventBus,
        TimeProvider timeProvider)
    {
        _captureBackend = captureBackend ?? throw new ArgumentNullException(nameof(captureBackend));
        _analysisService = analysisService ?? throw new ArgumentNullException(nameof(analysisService));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<AnalysisSession> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var session = AnalysisSession.Create(SessionId.New(), _timeProvider.GetUtcNow());
        var entry = new SessionEntry(session);
        if (!_sessions.TryAdd(session.Id, entry))
        {
            throw new InvalidOperationException("Could not register the analysis session.");
        }

        Task start;
        lock (entry.SyncRoot)
        {
            var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            start = StartActiveOperationLocked(
                entry,
                OperationKind.Start,
                operationCancellation,
                token => StartCoreAsync(entry, token));
        }

        await start.ConfigureAwait(false);
        return session;
    }

    public Task FinishAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = GetSession(sessionId);

        lock (entry.SyncRoot)
        {
            if (entry.CancellationOperation is not null)
            {
                throw new InvalidOperationException($"Session {sessionId} cannot be finished from state {entry.Session.State}.");
            }

            if (entry.ActiveOperationKind == OperationKind.Finish)
            {
                return entry.ActiveOperation!;
            }

            if (entry.ActiveOperation is not null || entry.Session.State != AnalysisSessionState.CapturingAllocations)
            {
                throw new InvalidOperationException($"Session {sessionId} cannot be finished from state {entry.Session.State}.");
            }

            var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            return StartActiveOperationLocked(
                entry,
                OperationKind.Finish,
                operationCancellation,
                token => FinishCoreAsync(entry, token));
        }
    }

    public Task CancelAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = GetSession(sessionId);

        lock (entry.SyncRoot)
        {
            if (entry.CancellationOperation is not null)
            {
                return entry.CancellationOperation;
            }

            if (entry.Session.State is AnalysisSessionState.Completed
                or AnalysisSessionState.Canceled
                or AnalysisSessionState.Failed)
            {
                throw new InvalidOperationException($"Session {sessionId} cannot be canceled from state {entry.Session.State}.");
            }

            if (entry.ActiveOperation is not null)
            {
                var activeOperation = entry.ActiveOperation;
                var activeCancellation = entry.ActiveOperationCancellation
                    ?? throw new InvalidOperationException("The active operation has no cancellation token.");
                var cancellation = StartCancellationOperationLocked(entry, () => CancelAfterActiveAsync(entry, activeOperation));
                activeCancellation.Cancel();
                return cancellation;
            }

            return StartCancellationOperationLocked(entry, () => CancelAndCompleteAsync(entry, failSession: false));
        }
    }

    private async Task StartCoreAsync(SessionEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            var start = AdmitStartLocked(entry, cancellationToken);
            await start.ConfigureAwait(false);

            await PublishCaptureStartedAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CompleteCancellationFromActiveCoreAsync(entry).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await FailSessionAsync(entry).ConfigureAwait(false);
        }
    }

    private async Task FinishCoreAsync(SessionEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await AdmitStageLocked(
                entry,
                AnalysisSessionState.FinishingTrace,
                _captureBackend.StopAllocationTraceAsync,
                cancellationToken).ConfigureAwait(false);
            await AdmitStageLocked(
                entry,
                AnalysisSessionState.CapturingHeapSnapshot,
                _captureBackend.CaptureHeapSnapshotAsync,
                cancellationToken).ConfigureAwait(false);
            await AdmitStageLocked(
                entry,
                AnalysisSessionState.Analyzing,
                _analysisService.AnalyzeAsync,
                cancellationToken).ConfigureAwait(false);

            await CompleteSessionAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CompleteCancellationFromActiveCoreAsync(entry).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await FailSessionAsync(entry).ConfigureAwait(false);
        }
    }

    private Task AdmitStartLocked(SessionEntry entry, CancellationToken cancellationToken)
    {
        lock (entry.SyncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MoveTo(entry.Session, AnalysisSessionState.Preflighting);
            MoveTo(entry.Session, AnalysisSessionState.CapturingAllocations);
            return _captureBackend.StartAllocationTraceAsync(entry.Session, cancellationToken);
        }
    }

    private static Task AdmitStageLocked(
        SessionEntry entry,
        AnalysisSessionState nextState,
        Func<AnalysisSession, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        lock (entry.SyncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MoveTo(entry.Session, nextState);
            return operation(entry.Session, cancellationToken);
        }
    }

    private async Task PublishCaptureStartedAsync(SessionEntry entry, CancellationToken cancellationToken)
    {
        ValueTask publication;
        lock (entry.SyncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            publication = _eventBus.PublishAsync(
                new CaptureStarted(entry.Session.Id, _timeProvider.GetUtcNow(), Source),
                CancellationToken.None);
        }

        try
        {
            await publication.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A started trace must be cleaned up before bounded lifecycle publication failure becomes a session failure.
            await BeginFailureCleanupAsync(entry).ConfigureAwait(false);
        }
    }

    private async Task CompleteSessionAsync(SessionEntry entry, CancellationToken cancellationToken)
    {
        ValueTask publication;
        lock (entry.SyncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MoveTo(entry.Session, AnalysisSessionState.Completed);
            publication = _eventBus.PublishAsync(
                new AnalysisCompleted(entry.Session.Id, _timeProvider.GetUtcNow(), Source),
                CancellationToken.None);
        }

        await IgnoreTerminalPublicationFailureAsync(publication).ConfigureAwait(false);
    }

    private async Task CancelAfterActiveAsync(SessionEntry entry, Task activeOperation)
    {
        try
        {
            await activeOperation.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await CancelAndCompleteAsync(entry, failSession: false).ConfigureAwait(false);
    }

    private async Task CompleteCancellationFromActiveCoreAsync(SessionEntry entry)
    {
        Task cancellation;
        lock (entry.SyncRoot)
        {
            if (entry.CancellationOperation is not null)
            {
                return;
            }

            cancellation = StartCancellationOperationLocked(entry, () => CancelAndCompleteAsync(entry, failSession: false));
        }

        await cancellation.ConfigureAwait(false);
    }

    private async Task BeginFailureCleanupAsync(SessionEntry entry)
    {
        Task cleanup;
        lock (entry.SyncRoot)
        {
            if (entry.CancellationOperation is not null)
            {
                return;
            }

            cleanup = StartCancellationOperationLocked(entry, () => CancelAndCompleteAsync(entry, failSession: true));
        }

        await cleanup.ConfigureAwait(false);
    }

    private async Task CancelAndCompleteAsync(SessionEntry entry, bool failSession)
    {
        Exception? cancellationFailure = null;
        try
        {
            var cancel = AdmitCancelLocked(entry);
            await cancel.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cancellationFailure = exception;
        }

        if (failSession || cancellationFailure is not null)
        {
            await FailSessionAsync(entry).ConfigureAwait(false);
            return;
        }

        ValueTask publication;
        lock (entry.SyncRoot)
        {
            if (entry.Session.State is AnalysisSessionState.Completed
                or AnalysisSessionState.Canceled
                or AnalysisSessionState.Failed)
            {
                return;
            }

            MoveTo(entry.Session, AnalysisSessionState.Canceled);
            publication = _eventBus.PublishAsync(
                new AnalysisCanceled(entry.Session.Id, _timeProvider.GetUtcNow(), Source),
                CancellationToken.None);
        }

        await IgnoreTerminalPublicationFailureAsync(publication).ConfigureAwait(false);
    }

    private Task AdmitCancelLocked(SessionEntry entry)
    {
        lock (entry.SyncRoot)
        {
            if (entry.Session.State is AnalysisSessionState.Completed
                or AnalysisSessionState.Canceled)
            {
                return Task.CompletedTask;
            }

            if (entry.Session.State is not AnalysisSessionState.Canceling and not AnalysisSessionState.Failed)
            {
                MoveTo(entry.Session, AnalysisSessionState.Canceling);
            }

            return _captureBackend.CancelAsync(entry.Session, CancellationToken.None);
        }
    }

    private async Task FailSessionAsync(SessionEntry entry)
    {
        ValueTask publication;
        lock (entry.SyncRoot)
        {
            if (entry.Session.State is AnalysisSessionState.Completed
                or AnalysisSessionState.Canceled
                or AnalysisSessionState.Failed)
            {
                return;
            }

            MoveTo(entry.Session, AnalysisSessionState.Failed);
            publication = _eventBus.PublishAsync(
                new AnalysisFailed(
                    entry.Session.Id,
                    SessionFailureCode,
                    SessionFailureMessage,
                    _timeProvider.GetUtcNow(),
                    Source),
                CancellationToken.None);
        }

        await IgnoreTerminalPublicationFailureAsync(publication).ConfigureAwait(false);
    }

    private static async Task IgnoreTerminalPublicationFailureAsync(ValueTask publication)
    {
        try
        {
            await publication.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A terminal state is already committed; bounded subscriber admission cannot reopen the session.
        }
    }

    private static Task StartActiveOperationLocked(
        SessionEntry entry,
        OperationKind operationKind,
        CancellationTokenSource operationCancellation,
        Func<CancellationToken, Task> operation)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        entry.ActiveOperation = completion.Task;
        entry.ActiveOperationKind = operationKind;
        entry.ActiveOperationCancellation = operationCancellation;
        _ = ExecuteActiveOperationAsync(entry, completion, operationCancellation, operation);
        return completion.Task;
    }

    private static Task StartCancellationOperationLocked(SessionEntry entry, Func<Task> operation)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        entry.CancellationOperation = completion.Task;
        _ = ExecuteCancellationOperationAsync(entry, completion, operation);
        return completion.Task;
    }

    private static async Task ExecuteActiveOperationAsync(
        SessionEntry entry,
        TaskCompletionSource completion,
        CancellationTokenSource operationCancellation,
        Func<CancellationToken, Task> operation)
    {
        try
        {
            await operation(operationCancellation.Token).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            lock (entry.SyncRoot)
            {
                if (ReferenceEquals(entry.ActiveOperation, completion.Task))
                {
                    entry.ActiveOperation = null;
                    entry.ActiveOperationKind = null;
                    entry.ActiveOperationCancellation = null;
                }
            }

            operationCancellation.Dispose();
        }
    }

    private static async Task ExecuteCancellationOperationAsync(
        SessionEntry entry,
        TaskCompletionSource completion,
        Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            lock (entry.SyncRoot)
            {
                if (ReferenceEquals(entry.CancellationOperation, completion.Task))
                {
                    entry.CancellationOperation = null;
                }
            }
        }
    }

    private SessionEntry GetSession(SessionId sessionId)
    {
        return _sessions.TryGetValue(sessionId, out var entry)
            ? entry
            : throw new KeyNotFoundException($"Unknown analysis session {sessionId}.");
    }

    private static void MoveTo(AnalysisSession session, AnalysisSessionState state)
    {
        if (!session.TryMoveTo(state))
        {
            throw new InvalidOperationException($"Session {session.Id} cannot move from {session.State} to {state}.");
        }
    }

    private sealed class SessionEntry(AnalysisSession session)
    {
        public object SyncRoot { get; } = new();

        public AnalysisSession Session { get; } = session;

        public Task? ActiveOperation { get; set; }

        public OperationKind? ActiveOperationKind { get; set; }

        public CancellationTokenSource? ActiveOperationCancellation { get; set; }

        public Task? CancellationOperation { get; set; }
    }

    private enum OperationKind
    {
        Start,
        Finish
    }
}
