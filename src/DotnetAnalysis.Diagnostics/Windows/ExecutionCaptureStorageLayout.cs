using DotnetAnalysis.Application.Contracts.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 管理单个执行采样会话的私有本地存储目录和分段文件路径。
/// </summary>
internal sealed class ExecutionCaptureStorageLayout
{
    /// <summary>
    /// 创建执行采样会话的私有存储布局。
    /// </summary>
    /// <param name="rootDirectory">执行采样会话根目录；未指定时使用本地应用数据目录。</param>
    /// <param name="sessionId">会话标识；未指定时创建新的标识。</param>
    public ExecutionCaptureStorageLayout(string? rootDirectory = null, Guid? sessionId = null)
    {
        try
        {
            RootDirectory = Path.GetFullPath(rootDirectory ?? GetDefaultRootDirectory());
            SessionId = sessionId ?? Guid.NewGuid();
            SessionDirectory = Path.Combine(RootDirectory, SessionId.ToString("N"));
            Directory.CreateDirectory(SessionDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw CreateStorageException("Execution capture session directory could not be created.", exception);
        }
    }

    /// <summary>
    /// 执行采样会话的父目录。
    /// </summary>
    public string RootDirectory { get; }

    /// <summary>
    /// 当前执行采样会话标识。
    /// </summary>
    public Guid SessionId { get; }

    /// <summary>
    /// 当前会话专属目录。
    /// </summary>
    public string SessionDirectory { get; }

    /// <summary>
    /// 获取应用管理的执行采样会话默认根目录。
    /// </summary>
    public static string GetDefaultRootDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DotnetAnalysis",
        "ExecutionSessions");

    /// <summary>
    /// 获取指定序号样本段的文件路径。
    /// </summary>
    /// <param name="segmentNumber">从零开始的样本段序号。</param>
    /// <returns>当前会话目录内的样本段路径。</returns>
    public string GetSegmentPath(int segmentNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(segmentNumber);

        return Path.Combine(SessionDirectory, $"samples-{segmentNumber:D8}.bin");
    }

    /// <summary>
    /// 创建统一的执行采样存储失败异常。
    /// </summary>
    internal static DiagnosticsException CreateStorageException(string message, Exception innerException) =>
        new(DiagnosticsErrorCode.ExecutionProfileStorageFailed, message, innerException);
}
