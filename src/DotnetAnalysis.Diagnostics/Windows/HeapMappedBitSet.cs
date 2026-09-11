using System.IO.MemoryMappedFiles;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 使用一次性文件和单个受限可写映射窗口保存大规模成员位，避免工件验证按对象数分配托管数组。
/// </summary>
internal sealed class HeapMappedBitSet : IDisposable
{
    private readonly HeapMappedFileWindow? _window;
    private readonly int _bitCount;

    /// <summary>
    /// 创建并清零固定长度位图文件；文件及其父目录的删除由调用方负责。
    /// </summary>
    /// <param name="path">必须尚不存在的一次性位图文件。</param>
    /// <param name="bitCount">可寻址位数量。</param>
    /// <returns>支持原子语义单线程设置和查询的文件位图。</returns>
    public static HeapMappedBitSet Create(string path, int bitCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(bitCount);
        var byteCount = checked(((long)bitCount + 7) / 8);
        return HeapTemporaryArtifactCleanup.CreateFileAndOpen(
            path,
            byteCount,
            () => new HeapMappedBitSet(path, bitCount, byteCount));
    }

    /// <summary>
    /// 查询指定位是否已设置。
    /// </summary>
    /// <param name="index">零基位索引。</param>
    /// <returns>此前已设置时返回 <see langword="true"/>。</returns>
    public bool Contains(int index)
    {
        ValidateIndex(index);
        var mask = (byte)(1 << (index & 7));
        return (_window!.ReadByte(index >> 3) & mask) != 0;
    }

    /// <summary>
    /// 设置指定位并报告它是否为首次出现，供 order 唯一性验证在一次访问中完成查询和写入。
    /// </summary>
    /// <param name="index">零基位索引。</param>
    /// <returns>位原先为零并已成功设置时返回 <see langword="true"/>；重复值返回 <see langword="false"/>。</returns>
    public bool TrySet(int index)
    {
        ValidateIndex(index);
        var byteOffset = index >> 3;
        var mask = (byte)(1 << (index & 7));
        var value = _window!.ReadByte(byteOffset);
        if ((value & mask) != 0)
        {
            return false;
        }

        _window.WriteByte(byteOffset, (byte)(value | mask));
        return true;
    }

    /// <summary>
    /// 释放活动位图视图及其共享映射租约；位图文件仍由父级验证目录持有。
    /// </summary>
    public void Dispose() => _window?.Dispose();

    /// <summary>
    /// 打开已按精确字节数创建的位图映射；零位集合不创建 Windows 不支持的空映射。
    /// </summary>
    /// <param name="path">已创建的位图文件。</param>
    /// <param name="bitCount">可寻址位数量。</param>
    /// <param name="byteCount">位图文件的精确长度。</param>
    private HeapMappedBitSet(string path, int bitCount, long byteCount)
    {
        _bitCount = bitCount;
        _window = byteCount == 0
            ? null
            : new HeapMappedFileWindow(
                path,
                MemoryMappedFileAccess.ReadWrite,
                HeapIndexResourcePolicy.DefaultMappedReadWindowBytes);
    }

    /// <summary>
    /// 验证位索引属于当前固定范围，防止映射偏移溢出或访问相邻文件区域。
    /// </summary>
    /// <param name="index">待访问的零基位索引。</param>
    private void ValidateIndex(int index)
    {
        if ((uint)index >= (uint)_bitCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Bit index must be inside the mapped bit set range.");
        }
    }
}
