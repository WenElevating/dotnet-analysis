using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 为当前快照提供不可取消的单飞索引解析，并在快照切换后释放旧缓存引用。
/// </summary>
internal sealed class SnapshotIndexCache
{
    private readonly object _syncRoot = new();
    private Task _coldBuildTail = Task.CompletedTask;
    private Entry? _current;

    /// <summary>
    /// 获取当前常驻索引所属的快照标识；没有已完成或正在解析的索引时返回空。
    /// </summary>
    internal MemorySnapshotId? CurrentSnapshotId
    {
        get
        {
            lock (_syncRoot)
            {
                return _current?.SnapshotId;
            }
        }
    }

    /// <summary>
    /// 获取当前快照索引；调用方取消只停止自身等待，不会取消共享解析。
    /// </summary>
    public async Task<SnapshotIndex> GetAsync(
        MemorySnapshotId snapshotId,
        Func<Task<SnapshotIndex>> loader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loader);
        Entry entry;
        lock (_syncRoot)
        {
            if (_current is { } current && current.SnapshotId == snapshotId)
            {
                entry = current;
            }
            else
            {
                entry = new Entry(snapshotId, QueueColdBuild(loader));
                _current = entry;
                _ = entry.Task.ContinueWith(
                    completed => ClearFailedEntry(entry, completed),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        return await entry.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ClearFailedEntry(Entry entry, Task<SnapshotIndex> completed)
    {
        if (completed.Status is not (TaskStatus.Faulted or TaskStatus.Canceled))
        {
            return;
        }

        lock (_syncRoot)
        {
            if (ReferenceEquals(_current, entry))
            {
                _current = null;
            }
        }
    }

    /// <summary>
    /// 串行化冷索引构建，防止快照切换时多个全量解析同时抬高峰值内存。
    /// </summary>
    private Task<SnapshotIndex> QueueColdBuild(Func<Task<SnapshotIndex>> loader)
    {
        var predecessor = _coldBuildTail;
        var queued = LoadAfterAsync(predecessor, loader);
        _coldBuildTail = queued;
        return queued;
    }

    private static async Task<SnapshotIndex> LoadAfterAsync(
        Task predecessor,
        Func<Task<SnapshotIndex>> loader)
    {
        try
        {
            await predecessor.ConfigureAwait(false);
        }
        catch
        {
            // A failed prior snapshot must not permanently block a later one.
        }

        return await loader().ConfigureAwait(false);
    }

    private sealed record Entry(MemorySnapshotId SnapshotId, Task<SnapshotIndex> Task);
}
