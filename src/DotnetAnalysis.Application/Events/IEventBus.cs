using DotnetAnalysis.Core.Events;

namespace DotnetAnalysis.Application.Events;

/// <summary>
/// 提供应用层事件发布、订阅和异步关闭能力。
/// </summary>
public interface IEventBus : IAsyncDisposable
{
    /// <summary>
    /// 将事件发布到当前匹配的订阅者。
    /// </summary>
    ValueTask PublishAsync<TEvent>(TEvent applicationEvent, CancellationToken cancellationToken)
        where TEvent : IApplicationEvent;

    /// <summary>
    /// 注册事件处理器并返回可撤销的订阅句柄。
    /// </summary>
    IDisposable Subscribe<TEvent>(
        Func<TEvent, CancellationToken, ValueTask> handler,
        EventSubscriptionOptions? options = null)
        where TEvent : IApplicationEvent;
}
