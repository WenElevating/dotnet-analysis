namespace DotnetAnalysis.Application.Events;

public sealed record EventSubscriptionOptions(
    int QueueCapacity = 64,
    bool CoalesceProgressEvents = true);
