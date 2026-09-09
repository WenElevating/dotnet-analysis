using DotnetAnalysis.Application.Contracts.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 定位随 Diagnostics 输出目录部署的 Windows x64 保留 Profiler 原生组件。
/// </summary>
/// <remarks>
/// 组件由 Diagnostics 项目构建目标复制到应用基目录的 <c>native</c> 子目录；不回退到源代码或临时构建目录，
/// 以免正式捕获依赖开发工作区状态。
/// </remarks>
internal static class ProfilerNativeArtifactLocator
{
    /// <summary>
    /// 返回已部署 Controller 和 Profiler DLL 的绝对路径。
    /// </summary>
    /// <returns>可供受控附加调用使用的原生文件路径。</returns>
    /// <exception cref="DiagnosticsException">任一原生组件未随应用部署时引发。</exception>
    public static ProfilerNativeArtifacts Resolve()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "native");
        var controllerPath = Path.Combine(directory, "DotnetAnalysis.RetentionProfilerController.dll");
        var profilerPath = Path.Combine(directory, "DotnetAnalysis.RetentionProfiler.dll");
        if (!File.Exists(controllerPath) || !File.Exists(profilerPath))
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.ProfilerAttachUnavailable,
                "保留分析原生 Profiler 组件未随 Diagnostics 应用输出部署。");
        }

        return new ProfilerNativeArtifacts(controllerPath, profilerPath);
    }
}

/// <summary>
/// 表示一对已验证存在的 Controller 和 CLR Profiler 原生 DLL 路径。
/// </summary>
/// <param name="controllerPath">Controller DLL 的绝对路径。</param>
/// <param name="profilerPath">Profiler DLL 的绝对路径。</param>
internal sealed record ProfilerNativeArtifacts(string ControllerPath, string ProfilerPath);
