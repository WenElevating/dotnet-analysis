using System.IO.MemoryMappedFiles;
namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 为大索引的随机固定宽度读取提供单窗口内存映射；切换位置时释放旧视图，
/// 因而调用方可通过每个文件的窗口上限约束总映射量。
/// </summary>
/// <remarks>
/// 本类型不缓存托管副本，也不创建整文件视图。默认 8 MiB 窗口与 Dominator 构建的五个并发读取器配合时，
/// 总映射窗口最多为 40 MiB，低于 Diagnostics 的 64 MiB 上限。
/// </remarks>
internal sealed class HeapMappedReadWindow : IDisposable
{
    private readonly HeapMappedFileWindow _window;

    /// <summary>
    /// 为一个只读二进制工件创建受限映射读取器。
    /// </summary>
    /// <param name="path">存在的、不会被当前查询写入的工件文件。</param>
    /// <param name="maximumWindowBytes">单次活动视图的最大字节数，必须是 64 KiB 的整数倍。</param>
    public HeapMappedReadWindow(string path, long maximumWindowBytes = HeapIndexResourcePolicy.DefaultMappedReadWindowBytes)
        => _window = new HeapMappedFileWindow(path, MemoryMappedFileAccess.Read, maximumWindowBytes);

    /// <summary>
    /// 单个活动映射窗口允许的最大大小。
    /// </summary>
    public long MaximumMappedWindowBytes => _window.MaximumMappedWindowBytes;

    /// <summary>
    /// 当前活动映射窗口的实际大小；尚未读取时为零。
    /// </summary>
    public long CurrentMappedWindowBytes => _window.CurrentMappedWindowBytes;

    /// <summary>
    /// 从指定偏移读取 32 位有符号整数。
    /// </summary>
    public int ReadInt32(long offset) => _window.ReadInt32(offset);

    /// <summary>
    /// 从指定偏移读取 64 位有符号整数。
    /// </summary>
    public long ReadInt64(long offset) => _window.ReadInt64(offset);

    /// <summary>
    /// 从指定偏移读取 64 位无符号整数。
    /// </summary>
    public ulong ReadUInt64(long offset) => _window.ReadUInt64(offset);

    /// <summary>
    /// 释放当前映射视图和底层文件映射；释放后不得继续读取。
    /// </summary>
    public void Dispose() => _window.Dispose();
}
