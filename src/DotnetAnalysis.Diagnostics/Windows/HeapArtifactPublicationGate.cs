namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 为单个基础或派生堆工件目标提供进程级互斥，确保等待者只在前一发布结束后复核完成态。
/// </summary>
internal static class HeapArtifactPublicationGate
{
    private static readonly object s_gate = new();
    private static readonly Dictionary<string, GateEntry> s_entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 异步进入规范化目标路径的独占发布区；取消只移除当前等待者，不影响持有者。
    /// </summary>
    /// <param name="targetDirectory">.heapidx 或 .heapderived 最终目录。</param>
    /// <param name="cancellationToken">取消当前等待。</param>
    /// <returns>离开发布区时必须释放的租约。</returns>
    public static async Task<PublicationLease> EnterAsync(
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        GateEntry entry;
        lock (s_gate)
        {
            if (!s_entries.TryGetValue(key, out entry!))
            {
                entry = new GateEntry();
                s_entries.Add(key, entry);
            }

            entry.ReferenceCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new PublicationLease(key, entry);
        }
        catch
        {
            ReleaseReference(key, entry);
            throw;
        }
    }

    /// <summary>
    /// 释放持有权和字典引用；最后一个持有者或等待者离开时移除路径状态。
    /// </summary>
    private static void Release(string key, GateEntry entry)
    {
        entry.Semaphore.Release();
        ReleaseReference(key, entry);
    }

    /// <summary>
    /// 在静态锁内减少引用计数，避免移除仍有等待者使用的信号量。
    /// </summary>
    private static void ReleaseReference(string key, GateEntry entry)
    {
        lock (s_gate)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0)
            {
                _ = s_entries.Remove(key);
            }
        }
    }

    /// <summary>
    /// 保存一个目标路径的信号量和持有者/等待者总引用数。
    /// </summary>
    internal sealed class GateEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }
    }

    /// <summary>
    /// 表示已经进入单目标发布区的幂等释放句柄。
    /// </summary>
    internal sealed class PublicationLease : IDisposable
    {
        private readonly string _key;
        private GateEntry? _entry;

        /// <summary>
        /// 创建绑定到指定目标状态的发布租约。
        /// </summary>
        internal PublicationLease(string key, GateEntry entry)
        {
            _key = key;
            _entry = entry;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            var entry = Interlocked.Exchange(ref _entry, null);
            if (entry is not null)
            {
                Release(_key, entry);
            }
        }
    }
}
