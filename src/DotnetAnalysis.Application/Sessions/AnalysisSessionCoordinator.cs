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

        await StartOperationAsync(entry, OperationKind.Start, () => StartCoreAsync(entry, cancellationToken)).ConfigureAwait(false);
        return session;
    }

    public Task FinishAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = GetSession(sessionId);

        lock (entry.SyncRoot)
        {
            if (entry.ActiveOperation is not null)
            {
                return entry.ActiveOperation;
            }

            if (entry.Session.State != AnalysisSessionState.CapturingAllocations)
            {
                throw new InvalidOperationException($"Session {sessionId} cannot be finished from state {entry.Session.State}.");
            }

            var finishCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            entry.FinishCancellation = finishCancellation;
            return StartOperationAsync(entry, OperationKind.Finish, async () =>
            {
                try
                {
                    await FinishCoreAsync(entry, finishCancellation.Token).ConfigureAwait(false);
                }
                finally
                {
                    lock (entry.SyncRoot)
                    {
                        if (ReferenceEquals(entry.FinishCancellation, finishCancellation))
                        {
                            entry.FinishCancellation = null;
                        }
                    }

                    finishCancellation.Dispose();
                }
            });
        }
    }

    public Task CancelAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = GetSession(sessionId);

        lock (entry.SyncRoot)
        {
            if (entry.ActiveOperation is not null)
            {
                if (entry.ActiveOperationKind == OperationKind.Finish)
                {
                    var finish = entry.ActiveOperation;
                    entry.FinishCancellation!.Cancel();
                    return StartOperationAsync(
                        entry,
                        OperationKind.Cancel,
                        () => CancelAfterFinishAsync(entry, finish));
                }

                return entry.ActiveOperation;
            }

            if (entry.Session.State is AnalysisSessionState.Completed
                or AnalysisSessionState.Canceled
                or AnalysisSessionState.Failed)
            {
                throw new InvalidOperationException($"Session {sessionId} cannot be canceled from state {entry.Session.State}.");
            }

            return StartOperationAsync(entry, OperationKind.Cancel, () => CancelCoreAsync(entry));
        }
    }

    private async Task StartCoreAsync(SessionEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            MoveTo(entry.Session, AnalysisSessionState.Preflighting);
            MoveTo(entry.Session, AnalysisSessionState.CapturingAllocations);
            await _captureBackend.StartAllocationTraceAsync(entry.Session, cancellationToken).ConfigureAwait(false);

            if (!await PublishActiveLifecycleEventAsync(
                    new CaptureStarted(entry.Session.Id, _timeProvider.GetUtcNow(), Source)).ConfigureAwait(false))
            {
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelCoreAsync(entry).ConfigureAwait(false);
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
            MoveTo(entry.Session, AnalysisSessionState.FinishingTrace);
            await _captureBackend.StopAllocationTraceAsync(entry.Session, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            MoveTo(entry.Session, AnalysisSessionState.CapturingHeapSnapshot);
            await _captureBackend.CaptureHeapSnapshotAsync(entry.Session, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            MoveTo(entry.Session, AnalysisSessionState.Analyzing);
            await _analysisService.AnalyzeAsync(entry.Session, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            MoveTo(entry.Session, AnalysisSessionState.Completed);
            await PublishTerminalLifecycleEventAsync(
                new AnalysisCompleted(entry.Session.Id, _timeProvider.GetUtcNow(), Source)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelCoreAsync(entry).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await FailSessionAsync(entry).ConfigureAwait(false);
        }
    }

    private async Task CancelAfterFinishAsync(SessionEntry entry, Task finish)
    {
        try
        {
            await finish.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Finish cancellation is converted to the durable canceled session state below.
        }

        await CancelCoreAsync(entry).ConfigureAwait(false);
    }

    private async Task CancelCoreAsync(SessionEntry entry)
    {
        if (entry.Session.State is AnalysisSessionState.Completed
            or AnalysisSessionState.Canceled
            or AnalysisSessionState.Failed)
        {
            return;
        }

        try
        {
            MoveTo(entry.Session, AnalysisSessionState.Canceling);
            await _captureBackend.CancelAsync(entry.Session, CancellationToken.None).ConfigureAwait(false);
            MoveTo(entry.Session, AnalysisSessionState.Canceled);
            await PublishTerminalLifecycleEventAsync(
                new AnalysisCanceled(entry.Session.Id, _timeProvider.GetUtcNow(), Source)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await FailSessionAsync(entry).ConfigureAwait(false);
        }
    }

    private static Task StartOperationAsync(SessionEntry entry, OperationKind operationKind, Func<Task> operation)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        entry.ActiveOperation = completion.Task;
        entry.ActiveOperationKind = operationKind;
        _ = ExecuteOperationAsync(entry, completion, operation);
        return completion.Task;
    }

    private static async Task ExecuteOperationAsync(
        SessionEntry entry,
        TaskCompletionSource completion,
        Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (OperationCanceledException cancellationException)
        {
            completion.TrySetCanceled(cancellationException.CancellationToken);
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
                }
            }
        }
    }

    private async Task<bool> PublishActiveLifecycleEventAsync<TEvent>(TEvent applicationEvent)
        where TEvent : IApplicationEvent
    {
        try
        {
            await _eventBus.PublishAsync(applicationEvent, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            // Queue admission is bounded: an active session becomes Failed instead of exposing a bus exception to the UI.
            var sessionId = applicationEvent.SessionId;
            if (sessionId is { } id && _sessions.TryGetValue(id, out var entry))
            {
                await FailSessionAsync(entry).ConfigureAwait(false);
            }

            return false;
        }
    }

    private async Task FailSessionAsync(SessionEntry entry)
    {
        if (entry.Session.State is AnalysisSessionState.Completed or AnalysisSessionState.Canceled or AnalysisSessionState.Failed)
        {
            return;
        }

        MoveTo(entry.Session, AnalysisSessionState.Failed);
        await PublishTerminalLifecycleEventAsync(
            new AnalysisFailed(
                entry.Session.Id,
                SessionFailureCode,
                SessionFailureMessage,
                _timeProvider.GetUtcNow(),
                Source)).ConfigureAwait(false);
    }

    private async Task PublishTerminalLifecycleEventAsync<TEvent>(TEvent applicationEvent)
        where TEvent : IApplicationEvent
    {
        try
        {
            await _eventBus.PublishAsync(applicationEvent, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Terminal state changes are durable even when a bounded event subscriber cannot admit the event.
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

        public CancellationTokenSource? FinishCancellation { get; set; }
    }

    private enum OperationKind
    {
        Start,
        Finish,
        Cancel
    }
}
