using DotnetAnalysis.Orchestration.Models;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Orchestration;

/// <summary>
/// 以有界内存保存活动诊断会话的进程内存样本，并记录缺失证据。
/// </summary>
public sealed class MemoryTimeline
{
    private readonly object _gate = new();
    private readonly Queue<MemoryUsageSample> _samples;
    private readonly int _capacity;
    private bool _droppedSamples;
    private bool _hasUnavailableSample;
    private bool _hasSessionEndedSample;

    /// <summary>创建有界内存时间线。</summary>
    /// <param name="capacity">最多保留的样本数。</param>
    public MemoryTimeline(int capacity = 2048)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Timeline capacity must be positive.");
        }

        _capacity = capacity;
        _samples = new Queue<MemoryUsageSample>(capacity);
    }

    /// <summary>当前保留的样本数。</summary>
    public int Count { get { lock (_gate) return _samples.Count; } }

    /// <summary>有界时间线允许保留的最大样本数。</summary>
    public int Capacity => _capacity;

    /// <summary>当前时间线的质量摘要。</summary>
    public DiagnosticQualitySummary Quality
    {
        get
        {
            lock (_gate)
            {
                if (_samples.Count == 0 && !_hasUnavailableSample && !_hasSessionEndedSample)
                {
                    return new DiagnosticQualitySummary(DiagnosticQuality.Unavailable, "尚未收到内存样本。");
                }

                if (_hasUnavailableSample || _hasSessionEndedSample || _droppedSamples)
                {
                    var missing = new List<string>();
                    if (_hasUnavailableSample) missing.Add("部分采样不可用");
                    if (_hasSessionEndedSample) missing.Add("目标会话已结束");
                    if (_droppedSamples) missing.Add("时间线达到容量上限，较早样本已淘汰");
                    return new DiagnosticQualitySummary(DiagnosticQuality.Partial, "时间线包含缺失或淘汰样本。", missing);
                }

                return new DiagnosticQualitySummary(DiagnosticQuality.Complete, "当前保留样本均可用。");
            }
        }
    }

    /// <summary>当前保留样本的防御性快照。</summary>
    public IReadOnlyList<MemoryUsageSample> Snapshot()
    {
        lock (_gate) return _samples.ToArray();
    }

    /// <summary>时间线中最早样本的观察时间。</summary>
    public DateTimeOffset? FirstObservedAtUtc
    {
        get { lock (_gate) return _samples.Count == 0 ? null : _samples.Peek().ObservedAtUtc; }
    }

    /// <summary>时间线中最晚样本的观察时间。</summary>
    public DateTimeOffset? LastObservedAtUtc
    {
        get { lock (_gate) return _samples.Count == 0 ? null : _samples.Last().ObservedAtUtc; }
    }

    /// <summary>追加一个样本；超过容量时淘汰最早样本。</summary>
    /// <param name="sample">要追加的样本。</param>
    public void Append(MemoryUsageSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (_gate)
        {
            if (sample.State is MemoryUsageSampleState.Unavailable) _hasUnavailableSample = true;
            if (sample.State is MemoryUsageSampleState.SessionEnded) _hasSessionEndedSample = true;
            if (_samples.Count == _capacity)
            {
                _samples.Dequeue();
                _droppedSamples = true;
            }
            _samples.Enqueue(sample);
        }
    }

    /// <summary>判断指定执行采样区间是否完全落在当前时间线可观察范围内。</summary>
    /// <param name="timeRange">需要查询的时间区间。</param>
    /// <returns>区间可被当前会话查询时返回 <see langword="true"/>。</returns>
    public bool Contains(ExecutionTimeRange timeRange)
    {
        ArgumentNullException.ThrowIfNull(timeRange);
        lock (_gate)
        {
            return _samples.Count > 0
                && timeRange.StartAtUtc >= _samples.Peek().ObservedAtUtc
                && timeRange.EndAtUtc <= _samples.Last().ObservedAtUtc;
        }
    }
}
