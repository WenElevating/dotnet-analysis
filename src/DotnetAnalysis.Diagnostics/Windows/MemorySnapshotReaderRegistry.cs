using DotnetAnalysis.Application.Contracts.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 根据文件格式选择已注册的快照读取器。
/// </summary>
public sealed class MemorySnapshotReaderRegistry
{
    private readonly IReadOnlyList<IMemorySnapshotReader> _readers;

    /// <summary>
    /// 使用所有已注册格式读取器创建快照读取器选择器。
    /// </summary>
    public MemorySnapshotReaderRegistry(IEnumerable<IMemorySnapshotReader> readers)
    {
        _readers = readers?.ToArray() ?? throw new ArgumentNullException(nameof(readers));
    }

    /// <summary>
    /// 根据存在的文件及其格式选择唯一支持它的读取器。
    /// </summary>
    /// <param name="filePath">待分析的本地快照路径。</param>
    /// <returns>可读取该文件的格式适配器。</returns>
    /// <exception cref="DiagnosticsException">文件缺失或格式无读取器支持时引发。</exception>
    public IMemorySnapshotReader Resolve(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.CaptureFailed, "Snapshot file does not exist.");
        }

        var reader = _readers.SingleOrDefault(candidate => candidate.CanRead(filePath));
        return reader ?? throw new DiagnosticsException(
            DiagnosticsErrorCode.SnapshotFormatNotSupported,
            "No reader supports the snapshot format.");
    }
}
