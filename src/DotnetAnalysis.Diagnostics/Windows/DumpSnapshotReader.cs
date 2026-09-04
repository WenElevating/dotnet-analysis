using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 识别 Windows 转储扩展名并明确报告当前适配器尚未实现。
/// </summary>
public sealed class DumpSnapshotReader : IMemorySnapshotReader
{
    /// <summary>
    /// 判断文件是否使用 Windows 转储扩展名。
    /// </summary>
    public bool CanRead(string filePath) => string.Equals(Path.GetExtension(filePath), ".dmp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 尝试读取按类型统计；当前转储适配器明确报告格式暂不支持。
    /// </summary>
    public Task<IReadOnlyList<MemoryTypeSummary>> ReadTypeSummariesAsync(string filePath, CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<MemoryTypeSummary>>(Unsupported());

    /// <summary>
    /// 尝试读取对象列表；当前转储适配器明确报告格式暂不支持。
    /// </summary>
    public Task<IReadOnlyList<MemoryObjectInfo>> ReadObjectsAsync(string filePath, TypeIdentity type, CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<MemoryObjectInfo>>(Unsupported());

    /// <summary>
    /// 尝试读取引用路径；当前转储适配器明确报告格式暂不支持。
    /// </summary>
    public Task<MemoryReferencePath?> ReadReferencePathAsync(string filePath, ulong objectAddress, CancellationToken cancellationToken) =>
        Task.FromException<MemoryReferencePath?>(Unsupported());

    /// <summary>
    /// 创建表示转储格式暂不支持的稳定异常。
    /// </summary>
    private static DiagnosticsException Unsupported() =>
        new(DiagnosticsErrorCode.SnapshotFormatNotSupported, "Dump snapshots are not supported yet.");
}
