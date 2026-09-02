using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Core.Sessions;

namespace DotnetAnalysis.Tests.Core;

[TestClass]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names describe the state transition under test.")]
public sealed class AnalysisSessionTests
{
    [TestMethod]
    public void Created_CanMoveToPreflighting()
    {
        var session = AnalysisSession.Create(SessionId.New(), DateTimeOffset.UtcNow);

        Assert.IsTrue(session.TryMoveTo(AnalysisSessionState.Preflighting));
        Assert.AreEqual(AnalysisSessionState.Preflighting, session.State);
    }

    [TestMethod]
    public void Created_CannotMoveDirectlyToCompleted()
    {
        var session = AnalysisSession.Create(SessionId.New(), DateTimeOffset.UtcNow);

        Assert.IsFalse(session.TryMoveTo(AnalysisSessionState.Completed));
        Assert.AreEqual(AnalysisSessionState.Created, session.State);
    }

    [TestMethod]
    public void ActiveState_CanMoveToCancelingThenCanceled()
    {
        var session = AnalysisSession.Create(SessionId.New(), DateTimeOffset.UtcNow);
        Assert.IsTrue(session.TryMoveTo(AnalysisSessionState.Preflighting));

        Assert.IsTrue(session.TryMoveTo(AnalysisSessionState.Canceling));
        Assert.IsTrue(session.TryMoveTo(AnalysisSessionState.Canceled));
    }

    [TestMethod]
    public void TerminalStates_RejectEveryNextState()
    {
        foreach (var terminalState in new[]
                 {
                     AnalysisSessionState.Completed,
                     AnalysisSessionState.Canceled,
                     AnalysisSessionState.Failed
                 })
        {
            var session = CreateSessionInState(terminalState);

            foreach (var nextState in Enum.GetValues<AnalysisSessionState>())
            {
                Assert.IsFalse(
                    session.TryMoveTo(nextState),
                    $"{terminalState} unexpectedly transitioned to {nextState}.");
                Assert.AreEqual(terminalState, session.State);
            }
        }
    }

    [TestMethod]
    public void Canceling_CannotMoveToCompleted()
    {
        var session = AnalysisSession.Create(SessionId.New(), DateTimeOffset.UtcNow);
        Assert.IsTrue(session.TryMoveTo(AnalysisSessionState.Preflighting));
        Assert.IsTrue(session.TryMoveTo(AnalysisSessionState.Canceling));

        Assert.IsFalse(session.TryMoveTo(AnalysisSessionState.Completed));
        Assert.AreEqual(AnalysisSessionState.Canceling, session.State);
    }

    [TestMethod]
    public void ActiveStates_CanMoveToCancelingOrFailed()
    {
        var activeStates = new[]
        {
            AnalysisSessionState.Created,
            AnalysisSessionState.Preflighting,
            AnalysisSessionState.CapturingAllocations,
            AnalysisSessionState.FinishingTrace,
            AnalysisSessionState.CapturingHeapSnapshot,
            AnalysisSessionState.Analyzing
        };

        foreach (var activeState in activeStates)
        {
            Assert.IsTrue(AnalysisSessionTransitionRules.CanMove(activeState, AnalysisSessionState.Canceling));
            Assert.IsTrue(AnalysisSessionTransitionRules.CanMove(activeState, AnalysisSessionState.Failed));
        }
    }

    private static AnalysisSession CreateSessionInState(AnalysisSessionState state)
    {
        var session = AnalysisSession.Create(SessionId.New(), DateTimeOffset.UtcNow);

        switch (state)
        {
            case AnalysisSessionState.Completed:
                MoveToCompleted(session);
                break;
            case AnalysisSessionState.Canceled:
                Assert.IsTrue(session.TryMoveTo(AnalysisSessionState.Canceling));
                Assert.IsTrue(session.TryMoveTo(AnalysisSessionState.Canceled));
                break;
            case AnalysisSessionState.Failed:
                Assert.IsTrue(session.TryMoveTo(AnalysisSessionState.Failed));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }

        return session;
    }

    private static void MoveToCompleted(AnalysisSession session)
    {
        Assert.IsTrue(session.TryMoveTo(AnalysisSessionState.Preflighting));
        Assert.IsTrue(session.TryMoveTo(AnalysisSessionState.CapturingAllocations));
        Assert.IsTrue(session.TryMoveTo(AnalysisSessionState.FinishingTrace));
        Assert.IsTrue(session.TryMoveTo(AnalysisSessionState.CapturingHeapSnapshot));
        Assert.IsTrue(session.TryMoveTo(AnalysisSessionState.Analyzing));
        Assert.IsTrue(session.TryMoveTo(AnalysisSessionState.Completed));
    }
}
