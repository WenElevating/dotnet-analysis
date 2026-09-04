using DotnetAnalysis.Application.Contracts.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 把底层异常转换为应用层稳定诊断错误码。
/// </summary>
public static class DiagnosticsExceptionFactory
{
    /// <summary>
    /// 将底层捕获异常规范化为稳定的“捕获失败”错误码。
    /// </summary>
    public static DiagnosticsException CaptureFailed(Exception exception) =>
        new(DiagnosticsErrorCode.CaptureFailed, "Diagnostics capture failed.", exception);

    /// <summary>
    /// 将底层进程访问异常规范化为稳定的“目标已退出”错误码。
    /// </summary>
    public static DiagnosticsException TargetExited(Exception exception) =>
        new(DiagnosticsErrorCode.TargetExited, "The target process exited.", exception);
}
