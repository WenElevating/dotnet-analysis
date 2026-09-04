namespace DotnetAnalysis.Application.Events;

/// <summary>
/// 由事件声明其队列投递策略和合并键的契约。
/// </summary>
public interface IApplicationEventDeliveryPolicy
{
    /// <summary>
    /// 事件应使用的投递模式。
    /// </summary>
    ApplicationEventDeliveryMode DeliveryMode { get; }

    /// <summary>
    /// LatestOnly 模式用于区分独立状态流的非空键。
    /// </summary>
    string DeliveryKey { get; }
}
