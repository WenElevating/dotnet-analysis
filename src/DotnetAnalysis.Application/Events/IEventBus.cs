using DotnetAnalysis.Core.Events;

namespace DotnetAnalysis.Application.Events;

public interface IEventBus : IAsyncDisposable
{
    ValueTask PublishAsync<TEvent>(TEvent applicationEvent, CancellationToken cancellationToken)
        where TEvent : IApplicationEvent;

    IDisposable Subscribe<TEvent>(
        Func<TEvent, CancellationToken, ValueTask> handler,
        EventSubscriptionOptions? options = null)
        where TEvent : IApplicationEvent;
}
