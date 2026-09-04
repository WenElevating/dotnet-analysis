namespace DotnetAnalysis.Application.Events;

/// <summary>
/// 配置单个事件订阅的有界队列容量。
/// </summary>
public sealed record EventSubscriptionOptions(
    /// <summary>
    /// 订阅队列最大容量；超过容量时由事件策略决定处理方式。
    /// </summary>
    int QueueCapacity = 64);
