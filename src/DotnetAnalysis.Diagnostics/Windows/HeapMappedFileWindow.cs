using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 对一个固定长度文件提供单活动窗口的标量随机访问；窗口切换会同步归还并重新取得进程级映射租约。
/// </summary>
internal sealed unsafe class HeapMappedFileWindow : IDisposable
{
    private const long AllocationGranularityBytes = 64 * 1024;
    private readonly MemoryMappedFile? _mapping;
    private readonly bool _ownsMapping;
    private readonly MemoryMappedFileAccess _access;
    private readonly long _length;
    private readonly List<MappedViewEntry> _views;
    private readonly int _maximumCachedWindows;
    private long _accessSequence;
    private long _recordReadOperations;
    private long _recordWriteOperations;
    private long _scalarReadOperations;
    private long _scalarWriteOperations;
    private long _viewOpenOperations;
    private bool _disposed;

    /// <summary>
    /// 打开一个只读或可写文件映射；空只读文件允许存在，但任何访问仍会按越界处理。
    /// </summary>
    /// <param name="path">已存在且生命周期内长度不变的文件。</param>
    /// <param name="access">当前窗口允许的读取或读写权限。</param>
    /// <param name="maximumWindowBytes">单个活动视图上限，必须是 64 KiB 的整数倍。</param>
    /// <param name="maximumCachedWindows">当前实例最多保留的 LRU 窗口数；所有窗口仍共享进程级 64 MiB 配额。</param>
    public HeapMappedFileWindow(
        string path,
        MemoryMappedFileAccess access,
        long maximumWindowBytes,
        int maximumCachedWindows = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (access is not MemoryMappedFileAccess.Read and not MemoryMappedFileAccess.ReadWrite)
        {
            throw new ArgumentOutOfRangeException(nameof(access));
        }

        if (maximumWindowBytes < AllocationGranularityBytes
            || maximumWindowBytes > HeapIndexResourcePolicy.MaximumMappedWindowBytes
            || maximumWindowBytes % AllocationGranularityBytes != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumWindowBytes));
        }

        ValidateMaximumCachedWindows(maximumWindowBytes, maximumCachedWindows);

        var fullPath = Path.GetFullPath(path);
        _length = new FileInfo(fullPath).Length;
        if (_length == 0 && access == MemoryMappedFileAccess.ReadWrite)
        {
            throw new ArgumentException("可写映射文件必须具有正长度。", nameof(path));
        }

        _mapping = _length == 0
            ? null
            : MemoryMappedFile.CreateFromFile(fullPath, FileMode.Open, null, 0, access);
        _ownsMapping = true;
        _access = access;
        _maximumCachedWindows = maximumCachedWindows;
        _views = new List<MappedViewEntry>(maximumCachedWindows);
        MaximumMappedWindowBytes = maximumWindowBytes;
    }

    /// <summary>
    /// 在调用方拥有的共享文件映射上创建独立活动视图；释放当前实例不会释放底层映射。
    /// </summary>
    /// <param name="mapping">生命周期覆盖当前窗口的非空文件映射。</param>
    /// <param name="length">底层文件的固定正长度。</param>
    /// <param name="access">当前窗口允许的读取或读写权限。</param>
    /// <param name="maximumWindowBytes">单个活动视图上限，必须是 64 KiB 的整数倍。</param>
    /// <param name="maximumCachedWindows">当前实例最多保留的 LRU 窗口数；所有窗口仍共享进程级 64 MiB 配额。</param>
    public HeapMappedFileWindow(
        MemoryMappedFile mapping,
        long length,
        MemoryMappedFileAccess access,
        long maximumWindowBytes,
        int maximumCachedWindows = 1)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        if (access is not MemoryMappedFileAccess.Read and not MemoryMappedFileAccess.ReadWrite)
        {
            throw new ArgumentOutOfRangeException(nameof(access));
        }

        if (maximumWindowBytes < AllocationGranularityBytes
            || maximumWindowBytes > HeapIndexResourcePolicy.MaximumMappedWindowBytes
            || maximumWindowBytes % AllocationGranularityBytes != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumWindowBytes));
        }

        ValidateMaximumCachedWindows(maximumWindowBytes, maximumCachedWindows);

        _mapping = mapping;
        _length = length;
        _access = access;
        _maximumCachedWindows = maximumCachedWindows;
        _views = new List<MappedViewEntry>(maximumCachedWindows);
        MaximumMappedWindowBytes = maximumWindowBytes;
    }

    /// <summary>
    /// 单个活动映射窗口允许的最大大小。
    /// </summary>
    public long MaximumMappedWindowBytes { get; }

    /// <summary>
    /// 当前活动映射窗口的逻辑大小；尚未访问或已释放时为零。
    /// </summary>
    public long CurrentMappedWindowBytes
    {
        get
        {
            long largestWindowBytes = 0;
            foreach (var view in _views)
            {
                largestWindowBytes = Math.Max(largestWindowBytes, view.Length);
            }

            return largestWindowBytes;
        }
    }

    /// <summary>
    /// 当前实例保留的 LRU 映射视图数量。
    /// </summary>
    public int CachedWindowCount => _views.Count;

    /// <summary>
    /// 当前实例执行的固定记录逻辑读取次数；用于验证热路径不会退化为逐字段映射访问。
    /// </summary>
    public long RecordReadOperations => _recordReadOperations;

    /// <summary>
    /// 当前实例执行的固定记录逻辑写入次数；用于验证热路径不会退化为逐字段映射访问。
    /// </summary>
    public long RecordWriteOperations => _recordWriteOperations;

    /// <summary>
    /// 当前实例执行的标量逻辑读取次数；跨窗口记录的罕见逐字节拼接也计入该值。
    /// </summary>
    public long ScalarReadOperations => _scalarReadOperations;

    /// <summary>
    /// 当前实例执行的标量逻辑写入次数；跨窗口记录的罕见逐字节拆分也计入该值。
    /// </summary>
    public long ScalarWriteOperations => _scalarWriteOperations;

    /// <summary>
    /// 当前实例因访问未命中活动窗口而创建映射视图的次数。
    /// </summary>
    public long ViewOpenOperations => _viewOpenOperations;

    /// <summary>从指定文件偏移读取一个字节。</summary>
    public byte ReadByte(long offset)
    {
        _scalarReadOperations++;
        var view = GetViewForByte(offset);
        return *(view.Pointer + GetRelativeOffset(offset, view));
    }

    /// <summary>从指定文件偏移读取一个小端 32 位有符号整数。</summary>
    public int ReadInt32(long offset)
    {
        _scalarReadOperations++;
        if (FitsSingleWindow(offset, sizeof(int)))
        {
            var view = GetViewForByte(offset);
            return Unsafe.ReadUnaligned<int>(view.Pointer + GetRelativeOffset(offset, view));
        }

        Span<byte> bytes = stackalloc byte[sizeof(int)];
        ReadAcrossWindows(offset, bytes);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    /// <summary>从指定文件偏移读取一个小端 64 位有符号整数。</summary>
    public long ReadInt64(long offset)
    {
        _scalarReadOperations++;
        if (FitsSingleWindow(offset, sizeof(long)))
        {
            var view = GetViewForByte(offset);
            return Unsafe.ReadUnaligned<long>(view.Pointer + GetRelativeOffset(offset, view));
        }

        Span<byte> bytes = stackalloc byte[sizeof(long)];
        ReadAcrossWindows(offset, bytes);
        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }

    /// <summary>从指定文件偏移读取一个小端 64 位无符号整数。</summary>
    public ulong ReadUInt64(long offset)
    {
        _scalarReadOperations++;
        if (FitsSingleWindow(offset, sizeof(ulong)))
        {
            var view = GetViewForByte(offset);
            return Unsafe.ReadUnaligned<ulong>(view.Pointer + GetRelativeOffset(offset, view));
        }

        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        ReadAcrossWindows(offset, bytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    /// <summary>将一个字节写入指定文件偏移。</summary>
    public void WriteByte(long offset, byte value)
    {
        _scalarWriteOperations++;
        EnsureWritable();
        var view = GetViewForByte(offset);
        *(view.Pointer + GetRelativeOffset(offset, view)) = value;
    }

    /// <summary>将一个小端 32 位有符号整数写入指定文件偏移。</summary>
    public void WriteInt32(long offset, int value)
    {
        _scalarWriteOperations++;
        EnsureWritable();
        if (FitsSingleWindow(offset, sizeof(int)))
        {
            var view = GetViewForByte(offset);
            Unsafe.WriteUnaligned(view.Pointer + GetRelativeOffset(offset, view), value);
            return;
        }

        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        WriteAcrossWindows(offset, bytes);
    }

    /// <summary>将一个小端 64 位有符号整数写入指定文件偏移。</summary>
    public void WriteInt64(long offset, long value)
    {
        _scalarWriteOperations++;
        EnsureWritable();
        if (FitsSingleWindow(offset, sizeof(long)))
        {
            var view = GetViewForByte(offset);
            Unsafe.WriteUnaligned(view.Pointer + GetRelativeOffset(offset, view), value);
            return;
        }

        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        WriteAcrossWindows(offset, bytes);
    }

    /// <summary>
    /// 通过一次映射访问读取一个非托管固定宽度记录；仅在记录跨窗口边界时逐字节拼接。
    /// </summary>
    /// <typeparam name="T">不包含托管引用的固定布局记录类型。</typeparam>
    /// <param name="offset">记录在文件中的起始偏移。</param>
    /// <returns>完整反序列化的记录值。</returns>
    public T ReadRecord<T>(long offset)
        where T : unmanaged
    {
        _recordReadOperations++;
        var size = Unsafe.SizeOf<T>();
        if (FitsSingleWindow(offset, size))
        {
            var view = GetViewForByte(offset);
            return Unsafe.ReadUnaligned<T>(view.Pointer + GetRelativeOffset(offset, view));
        }

        T result = default;
        var values = MemoryMarshal.CreateSpan(ref result, 1);
        ReadAcrossWindows(offset, MemoryMarshal.AsBytes(values));
        return result;
    }

    /// <summary>
    /// 通过一次映射访问写入一个非托管固定宽度记录；仅在记录跨窗口边界时逐字节拆分。
    /// </summary>
    /// <typeparam name="T">不包含托管引用的固定布局记录类型。</typeparam>
    /// <param name="offset">记录在文件中的起始偏移。</param>
    /// <param name="value">要写入的完整记录值。</param>
    public void WriteRecord<T>(long offset, T value)
        where T : unmanaged
    {
        _recordWriteOperations++;
        EnsureWritable();
        var size = Unsafe.SizeOf<T>();
        if (FitsSingleWindow(offset, size))
        {
            var view = GetViewForByte(offset);
            Unsafe.WriteUnaligned(view.Pointer + GetRelativeOffset(offset, view), value);
            return;
        }

        var values = MemoryMarshal.CreateReadOnlySpan(ref value, 1);
        WriteAcrossWindows(offset, MemoryMarshal.AsBytes(values));
    }

    /// <summary>
    /// 释放活动视图、进程级配额与底层映射句柄；释放后不得继续访问。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseViews();
        if (_ownsMapping)
        {
            _mapping?.Dispose();
        }
    }

    /// <summary>
    /// 判断固定宽度值是否完全位于一个逻辑窗口内，并提前验证访问范围。
    /// </summary>
    /// <param name="offset">值的起始文件偏移。</param>
    /// <param name="requiredBytes">固定宽度值的字节数。</param>
    /// <returns>不需要跨窗口拼接时返回 <see langword="true"/>。</returns>
    private bool FitsSingleWindow(long offset, int requiredBytes)
    {
        ValidateRange(offset, requiredBytes);
        var start = offset / MaximumMappedWindowBytes * MaximumMappedWindowBytes;
        return offset <= start + Math.Min(MaximumMappedWindowBytes, _length - start) - requiredBytes;
    }

    /// <summary>
    /// 返回覆盖指定字节的最近使用视图；不命中时按固定粒度打开窗口，并在达到上限时淘汰最久未使用项。
    /// </summary>
    /// <param name="offset">必须落在文件内的字节偏移。</param>
    /// <returns>覆盖该字节且持有共享预算租约的映射视图。</returns>
    private MappedViewEntry GetViewForByte(long offset)
    {
        ValidateRange(offset, 1);
        foreach (var existing in _views)
        {
            if (offset >= existing.Start && offset < existing.Start + existing.Length)
            {
                existing.LastAccessSequence = ++_accessSequence;
                return existing;
            }
        }

        if (_views.Count == _maximumCachedWindows)
        {
            var leastRecentlyUsed = _views[0];
            for (var index = 1; index < _views.Count; index++)
            {
                if (_views[index].LastAccessSequence < leastRecentlyUsed.LastAccessSequence)
                {
                    leastRecentlyUsed = _views[index];
                }
            }

            CloseView(leastRecentlyUsed);
            _views.Remove(leastRecentlyUsed);
        }

        var viewStart = offset / MaximumMappedWindowBytes * MaximumMappedWindowBytes;
        var viewLength = Math.Min(MaximumMappedWindowBytes, _length - viewStart);
        MemoryMappedViewAccessor? accessor = null;
        var budgetAcquired = false;
        var pointerAcquired = false;
        try
        {
            HeapMappedWindowBudget.Acquire(viewLength);
            budgetAcquired = true;
            accessor = _mapping!.CreateViewAccessor(viewStart, viewLength, _access);
            _viewOpenOperations++;
            byte* pointer = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            pointerAcquired = true;
            var entry = new MappedViewEntry(
                accessor,
                pointer + checked((int)accessor.PointerOffset),
                viewStart,
                viewLength,
                ++_accessSequence);
            _views.Add(entry);
            return entry;
        }
        catch
        {
            if (pointerAcquired)
            {
                accessor!.SafeMemoryMappedViewHandle.ReleasePointer();
            }

            accessor?.Dispose();
            if (budgetAcquired)
            {
                HeapMappedWindowBudget.Release(viewLength);
            }

            throw;
        }
    }

    /// <summary>
    /// 将已验证命中当前窗口的绝对文件偏移转换为可用于指针运算的窗口内偏移。
    /// </summary>
    /// <param name="offset">已由当前视图覆盖的绝对文件偏移。</param>
    /// <param name="view">覆盖该偏移的活动 LRU 视图。</param>
    /// <returns>不超过单窗口上限的非负整数偏移。</returns>
    private static int GetRelativeOffset(long offset, MappedViewEntry view)
        => checked((int)(offset - view.Start));

    /// <summary>
    /// 逐字节读取罕见的跨窗口标量；每次切换仍保持至多一个活动视图。
    /// </summary>
    /// <param name="offset">标量起始文件偏移。</param>
    /// <param name="destination">接收完整小端标量的栈缓冲区。</param>
    private void ReadAcrossWindows(long offset, Span<byte> destination)
    {
        for (var index = 0; index < destination.Length; index++)
        {
            destination[index] = ReadByte(offset + index);
        }
    }

    /// <summary>
    /// 逐字节写入罕见的跨窗口标量；每次切换先释放旧视图及其共享租约。
    /// </summary>
    /// <param name="offset">标量起始文件偏移。</param>
    /// <param name="source">包含完整小端标量的栈缓冲区。</param>
    private void WriteAcrossWindows(long offset, ReadOnlySpan<byte> source)
    {
        for (var index = 0; index < source.Length; index++)
        {
            WriteByte(offset + index, source[index]);
        }
    }

    /// <summary>
    /// 验证访问范围和生命周期，确保错误不会延迟到平台映射 API。
    /// </summary>
    /// <param name="offset">访问起始文件偏移。</param>
    /// <param name="requiredBytes">本次访问需要的连续字节数。</param>
    private void ValidateRange(long offset, int requiredBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0 || requiredBytes < 0 || offset > _length - requiredBytes)
        {
            throw new EndOfStreamException("堆索引映射访问超出工件文件边界。");
        }
    }

    /// <summary>
    /// 验证当前映射具有写权限，避免依赖平台异常表达调用方契约错误。
    /// </summary>
    private void EnsureWritable()
    {
        if (_access != MemoryMappedFileAccess.ReadWrite)
        {
            throw new InvalidOperationException("当前堆索引映射窗口是只读的。");
        }
    }

    /// <summary>
    /// 关闭所有 LRU 视图并立即归还对应的进程级映射配额。
    /// </summary>
    private void CloseViews()
    {
        foreach (var view in _views)
        {
            CloseView(view);
        }

        _views.Clear();
    }

    /// <summary>
    /// 释放一个 LRU 视图的指针、访问器和共享字节租约。
    /// </summary>
    /// <param name="view">由当前实例拥有且只关闭一次的视图。</param>
    private static void CloseView(MappedViewEntry view)
    {
        try
        {
            view.Accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
        finally
        {
            try
            {
                view.Accessor.Dispose();
            }
            finally
            {
                HeapMappedWindowBudget.Release(view.Length);
            }
        }
    }

    /// <summary>
    /// 验证单个实例配置的窗口数量不会独占超过进程级映射预算。
    /// </summary>
    /// <param name="maximumWindowBytes">单个窗口最大字节数。</param>
    /// <param name="maximumCachedWindows">实例最多保留的窗口数量。</param>
    private static void ValidateMaximumCachedWindows(long maximumWindowBytes, int maximumCachedWindows)
    {
        if (maximumCachedWindows < 1
            || maximumCachedWindows > HeapIndexResourcePolicy.MaximumMappedWindowBytes / maximumWindowBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCachedWindows));
        }
    }

    /// <summary>
    /// 保存一个已取得指针租约的映射窗口及其 LRU 次序；所有者负责在淘汰或释放时归还资源。
    /// </summary>
    private sealed class MappedViewEntry
    {
        /// <summary>
        /// 创建一个活动映射视图条目。
        /// </summary>
        /// <param name="accessor">持有底层映射句柄的视图访问器。</param>
        /// <param name="pointer">已包含平台视图偏移的首字节指针。</param>
        /// <param name="start">窗口对应的绝对文件起始偏移。</param>
        /// <param name="length">窗口实际映射字节数。</param>
        /// <param name="lastAccessSequence">用于 LRU 比较的初始访问序号。</param>
        public MappedViewEntry(
            MemoryMappedViewAccessor accessor,
            byte* pointer,
            long start,
            long length,
            long lastAccessSequence)
        {
            Accessor = accessor;
            Pointer = pointer;
            Start = start;
            Length = length;
            LastAccessSequence = lastAccessSequence;
        }

        /// <summary>持有底层映射句柄的视图访问器。</summary>
        public MemoryMappedViewAccessor Accessor { get; }

        /// <summary>已包含平台视图偏移的首字节指针。</summary>
        public byte* Pointer { get; }

        /// <summary>窗口对应的绝对文件起始偏移。</summary>
        public long Start { get; }

        /// <summary>窗口实际映射并计入共享预算的字节数。</summary>
        public long Length { get; }

        /// <summary>用于淘汰最久未使用窗口的单调访问序号。</summary>
        public long LastAccessSequence { get; set; }
    }
}
