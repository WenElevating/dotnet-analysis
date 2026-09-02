namespace DotnetAnalysis.Application.Events;

public sealed class EventDeliveryException : InvalidOperationException
{
    public EventDeliveryException(Type eventType, string subscriptionIdentity)
        : base($"Could not admit {eventType.Name} to subscription {subscriptionIdentity}.")
    {
        EventType = eventType ?? throw new ArgumentNullException(nameof(eventType));
        SubscriptionIdentity = subscriptionIdentity ?? throw new ArgumentNullException(nameof(subscriptionIdentity));
    }

    public Type EventType { get; }

    public string SubscriptionIdentity { get; }
}
