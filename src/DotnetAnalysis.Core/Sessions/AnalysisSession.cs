namespace DotnetAnalysis.Core.Sessions;

public sealed class AnalysisSession
{
    private AnalysisSession(SessionId id)
    {
        Id = id;
        State = AnalysisSessionState.Created;
    }

    public SessionId Id { get; }

    public AnalysisSessionState State { get; private set; }

    public static AnalysisSession Create(SessionId id, DateTimeOffset createdAt)
    {
        return new AnalysisSession(id);
    }

    public bool TryMoveTo(AnalysisSessionState nextState)
    {
        if (!AnalysisSessionTransitionRules.CanMove(State, nextState))
        {
            return false;
        }

        State = nextState;
        return true;
    }
}
