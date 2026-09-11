using DotnetAnalysis.Application.Contracts.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 统一管理诊断进程内堆索引映射视图的字节租约，使只读图、可写工作区和验证位图共享同一上限。
/// </summary>
internal static class HeapMappedWindowBudget
{
    private static readonly object s_sync = new();
    private static long s_leasedBytes;

    /// <summary>
    /// 当前进程所有堆索引映射视图共同遵守的最大租约字节数。
    /// </summary>
    public static long MaximumLeasedBytes => HeapIndexResourcePolicy.MaximumMappedWindowBytes;

    /// <summary>
    /// 为一个即将创建的映射视图取得进程级字节租约；调用方必须在视图关闭后用相同字节数调用 <see cref="Release(long)"/>。
    /// </summary>
    /// <param name="bytes">实际视图长度，必须为正数且不超过进程映射上限。</param>
    /// <exception cref="DiagnosticsException">并发映射已占满进程预算时引发。</exception>
    public static void Acquire(long bytes)
    {
        lock (s_sync)
        {
            if (bytes <= 0 || s_leasedBytes > MaximumLeasedBytes - bytes)
            {
                throw new DiagnosticsException(
                    DiagnosticsErrorCode.SnapshotQueryLimitReached,
                    "并发堆索引读取已达到 64 MiB 映射窗口预算。");
            }

            s_leasedBytes += bytes;
        }
    }

    /// <summary>
    /// 归还一个已成功取得的映射视图字节租约；视图所有者负责确保每次成功取得只归还一次。
    /// </summary>
    /// <param name="bytes">当前视图实际持有的映射字节数。</param>
    public static void Release(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        lock (s_sync)
        {
            s_leasedBytes -= bytes;
        }
    }
}
