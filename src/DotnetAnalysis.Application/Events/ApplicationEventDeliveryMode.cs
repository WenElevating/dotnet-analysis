namespace DotnetAnalysis.Application.Events;

/// <summary>
/// 定义应用事件在订阅队列中的投递策略。
/// </summary>
public enum ApplicationEventDeliveryMode
{
    /// <summary>
    /// 按发布顺序逐条投递。
    /// </summary>
    Ordered,
    /// <summary>
    /// 同一投递键仅保留最新待处理事件。
    /// </summary>
    LatestOnly
}
