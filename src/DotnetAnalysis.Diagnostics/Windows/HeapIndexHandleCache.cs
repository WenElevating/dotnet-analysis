using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 为当前快照缓存轻量堆索引句柄，保持单飞构建、调用方独立取消和快照切换释放映射资源的语义。
/// </summary>
internal sealed class HeapIndexHandleCache : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _coldBuildGate = new(1, 1);
    private Entry? _current;
    private bool _disposed;

    /// <summary>
    /// 获取当前快照的路由索引句柄；调用方取消仅取消等待，不会中止共享构建。
    /// </summary>
    public async Task<HeapIndexHandle> GetAsync(
        MemorySnapshotId snapshotId,
        Func<Task<HeapIndexHandle>> loader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loader);
        Entry entry;
        Entry? superseded = null;
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_current is { } current && current.SnapshotId == snapshotId)
            {
                entry = current;
                entry.RegisterWaiter();
            }
            else
            {
                superseded = _current;
                entry = new Entry(snapshotId, QueueColdBuild(loader));
                entry.RegisterWaiter();
                _current = entry;
                _ = entry.Task.ContinueWith(
                    completed => ClearFailedEntry(entry, completed),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        superseded?.Evict();

        try
        {
            var owner = await entry.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return owner.AcquireLease();
        }
        finally
        {
            entry.ReleaseWaiter();
        }
    }

    /// <summary>
    /// 关闭当前完成句柄；未完成构建在完成后会自行释放。
    /// </summary>
    public void Dispose()
    {
        Entry? current;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            current = _current;
            _current = null;
        }

        current?.Evict();
    }

    /// <summary>
    /// 在同一快照构建失败或取消时清除入口，使后续请求能够重试。
    /// </summary>
    private void ClearFailedEntry(Entry entry, Task<HeapIndexHandle> completed)
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

        entry.Evict();
    }

    /// <summary>
    /// 串行化冷构建，防止快照切换时多个完整图同时抬高峰值内存。
    /// </summary>
    private Task<HeapIndexHandle> QueueColdBuild(Func<Task<HeapIndexHandle>> loader)
    {
        return LoadWithGateAsync(loader);
    }

    /// <summary>
    /// 串行执行一个冷构建；释放信号量后不保留前序任务引用，避免快照切换形成任务链。
    /// </summary>
    private async Task<HeapIndexHandle> LoadWithGateAsync(Func<Task<HeapIndexHandle>> loader)
    {
        await _coldBuildGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            return await loader().ConfigureAwait(false);
        }
        finally
        {
            _coldBuildGate.Release();
        }
    }

    /// <summary>
    /// 表示单个快照的共享构建任务、缓存所有权和尚未取得调用方租约的等待者。
    /// </summary>
    private sealed class Entry
    {
        private readonly object _syncRoot = new();
        private int _pendingWaiterCount;
        private bool _evicted;
        private bool _ownerReleased;

        /// <summary>
        /// 创建缓存拥有的单飞加载条目。
        /// </summary>
        public Entry(MemorySnapshotId snapshotId, Task<HeapIndexHandle> task)
        {
            SnapshotId = snapshotId;
            Task = task ?? throw new ArgumentNullException(nameof(task));
        }

        /// <summary>
        /// 条目对应的快照标识。
        /// </summary>
        public MemorySnapshotId SnapshotId { get; }

        /// <summary>
        /// 返回缓存初始所有权句柄的共享加载任务。
        /// </summary>
        public Task<HeapIndexHandle> Task { get; }

        /// <summary>
        /// 登记一个已选择该条目但尚未取得独立句柄租约的等待者。
        /// </summary>
        public void RegisterWaiter()
        {
            lock (_syncRoot)
            {
                if (_evicted)
                {
                    throw new InvalidOperationException("不能从已移除的堆索引缓存条目登记等待者。");
                }

                _pendingWaiterCount++;
            }
        }

        /// <summary>
        /// 标记一个等待者已取得租约或离开，并在必要时释放已移除条目的缓存所有权。
        /// </summary>
        public void ReleaseWaiter()
        {
            lock (_syncRoot)
            {
                _pendingWaiterCount--;
                if (_pendingWaiterCount < 0)
                {
                    throw new InvalidOperationException("堆索引缓存等待者计数已失衡。");
                }
            }

            TryReleaseOwner();
        }

        /// <summary>
        /// 移除缓存所有权；活动等待者会先取得自己的租约，未完成加载则在完成后复核释放条件。
        /// </summary>
        public void Evict()
        {
            lock (_syncRoot)
            {
                _evicted = true;
            }

            if (!Task.IsCompleted)
            {
                _ = Task.ContinueWith(
                    _ => TryReleaseOwner(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            TryReleaseOwner();
        }

        /// <summary>
        /// 在条目已移除、加载已完成且所有等待者离开后，恰好释放一次缓存拥有的句柄。
        /// </summary>
        private void TryReleaseOwner()
        {
            HeapIndexHandle? owner = null;
            lock (_syncRoot)
            {
                if (!_evicted || _ownerReleased || _pendingWaiterCount != 0 || !Task.IsCompleted)
                {
                    return;
                }

                _ownerReleased = true;
                if (Task.Status is TaskStatus.RanToCompletion)
                {
                    owner = Task.Result;
                }
            }

            owner?.Dispose();
        }
    }
}
