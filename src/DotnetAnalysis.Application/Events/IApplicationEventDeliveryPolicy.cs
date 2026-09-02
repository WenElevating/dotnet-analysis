namespace DotnetAnalysis.Application.Events;

public interface IApplicationEventDeliveryPolicy
{
    ApplicationEventDeliveryMode DeliveryMode { get; }

    string DeliveryKey { get; }
}
