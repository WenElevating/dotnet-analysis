namespace DotnetAnalysis.Application.Events;

/// <summary>
/// 表示事件无法进入订阅队列的投递异常。
/// </summary>
public sealed class EventDeliveryException : InvalidOperationException
{
    /// <summary>
    /// 创建投递失败异常。
    /// </summary>
    /// <param name="eventType">未能投递的事件类型。</param>
    /// <param name="subscriptionIdentity">失败订阅的稳定诊断标识。</param>
    public EventDeliveryException(Type eventType, string subscriptionIdentity)
        : base($"Could not admit {eventType.Name} to subscription {subscriptionIdentity}.")
    {
        EventType = eventType ?? throw new ArgumentNullException(nameof(eventType));
        SubscriptionIdentity = subscriptionIdentity ?? throw new ArgumentNullException(nameof(subscriptionIdentity));
    }

    /// <summary>
    /// 未能投递的事件类型。
    /// </summary>
    public Type EventType { get; }

    /// <summary>
    /// 失败订阅的身份字符串。
    /// </summary>
    public string SubscriptionIdentity { get; }
}
