using System.Runtime.InteropServices;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 在 Diagnostics 层动态加载原生 Controller，并调用 CLR AttachProfiler 导出入口。
/// </summary>
/// <remarks>
/// 原生附加调用会在 CLR 握手期间阻塞；本类将它调度到线程池，但单个调用方取消只停止等待，
/// 不会中断已经进入 CLR 的 AttachProfiler 调用。
/// </remarks>
internal sealed class ProfilerControllerClient
{
    /// <summary>
    /// 保留分析 Profiler 的稳定 COM 类标识，必须与原生协议头一致。
    /// </summary>
    internal static readonly Guid RetentionProfilerClsid = new("1D6F4A88-0F8B-4B93-A12C-1DCAB9696F13");

    private const string AttachExportName = "DotnetAnalysisAttachRetentionProfiler";
    private const int ENoInterface = unchecked((int)0x80004002);

    /// <summary>
    /// 调用 Controller 在目标 CLR 中附加保留分析 Profiler。
    /// </summary>
    /// <param name="processId">目标进程 PID。</param>
    /// <param name="handshakeTimeout">CLR 附加握手最长等待时间，计划值为 15 秒。</param>
    /// <param name="controllerPath">Controller DLL 的绝对路径。</param>
    /// <param name="profilerPath">Profiler DLL 的绝对路径。</param>
    /// <param name="attachData">传给 InitializeForAttach 的固定布局协议数据。</param>
    /// <param name="cancellationToken">取消当前等待的令牌，不强行中断 CLR 附加。</param>
    /// <returns>Controller 返回的原始 HRESULT，调用方应结合目标状态映射稳定诊断错误。</returns>
    /// <exception cref="DiagnosticsException">Controller DLL 或导出入口不可用时引发。</exception>
    public static async Task<int> AttachAsync(
        int processId,
        TimeSpan handshakeTimeout,
        string controllerPath,
        string profilerPath,
        RetentionProfilerAttachData attachData,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentException.ThrowIfNullOrWhiteSpace(controllerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(profilerPath);
        if (handshakeTimeout <= TimeSpan.Zero || handshakeTimeout.TotalMilliseconds > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(handshakeTimeout));
        }

        var attachTask = Task.Run(
            () => AttachCore(
                checked((uint)processId),
                checked((uint)handshakeTimeout.TotalMilliseconds),
                controllerPath,
                profilerPath,
                attachData),
            CancellationToken.None);
        var attachResult = await attachTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (attachResult != ENoInterface)
        {
            return attachResult;
        }

        // ICLRProfiling is a metahost service and may not expose the modern CoreCLR
        // attach endpoint.  .NET 8-10 uses Diagnostics IPC for the same controlled
        // profiler-attach operation, which is the runtime-supported fallback here.
        var diagnosticsAttachTask = Task.Run(
            () => AttachThroughDiagnosticsIpc(
                processId,
                handshakeTimeout,
                profilerPath,
                attachData),
            CancellationToken.None);
        return await diagnosticsAttachTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 在工作线程内执行不可取消的原生调用，并严格关闭动态库和结构封送缓冲区。
    /// </summary>
    private static int AttachCore(
        uint processId,
        uint timeoutMilliseconds,
        string controllerPath,
        string profilerPath,
        RetentionProfilerAttachData attachData)
    {
        if (!File.Exists(controllerPath) || !File.Exists(profilerPath))
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.ProfilerAttachUnavailable,
                "保留分析原生 Controller 或 Profiler DLL 未部署。");
        }

        IntPtr nativeLibrary = IntPtr.Zero;
        IntPtr nativeData = IntPtr.Zero;
        try
        {
            nativeLibrary = NativeLibrary.Load(controllerPath);
            var export = NativeLibrary.GetExport(nativeLibrary, AttachExportName);
            var attach = Marshal.GetDelegateForFunctionPointer<AttachProfilerDelegate>(export);
            nativeData = Marshal.AllocHGlobal(Marshal.SizeOf<RetentionProfilerAttachData>());
            Marshal.StructureToPtr(attachData, nativeData, fDeleteOld: false);
            var clsid = RetentionProfilerClsid;
            return attach(
                processId,
                timeoutMilliseconds,
                ref clsid,
                profilerPath,
                nativeData,
                checked((uint)Marshal.SizeOf<RetentionProfilerAttachData>()));
        }
        catch (DiagnosticsException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException
                or BadImageFormatException
                or EntryPointNotFoundException
                or SEHException)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.ProfilerAttachUnavailable,
                "无法加载保留分析原生 Controller 或其附加入口。",
                exception);
        }
        finally
        {
            if (nativeData != IntPtr.Zero)
            {
                Marshal.DestroyStructure<RetentionProfilerAttachData>(nativeData);
                Marshal.FreeHGlobal(nativeData);
            }
            if (nativeLibrary != IntPtr.Zero)
            {
                NativeLibrary.Free(nativeLibrary);
            }
        }
    }

    /// <summary>
    /// 通过 .NET 8-10 CoreCLR 的 Diagnostics IPC 端点附加 Profiler；该路径与运行时测试使用的客户端一致。
    /// </summary>
    private static int AttachThroughDiagnosticsIpc(
        int processId,
        TimeSpan handshakeTimeout,
        string profilerPath,
        RetentionProfilerAttachData attachData)
    {
        try
        {
            var clientData = SerializeAttachData(attachData);
            new DiagnosticsClient(processId).AttachProfiler(
                handshakeTimeout,
                RetentionProfilerClsid,
                profilerPath,
                clientData);
            return 0;
        }
        catch (Exception exception) when (
            exception is ServerErrorException
                or ServerNotAvailableException
                or UnauthorizedAccessException
                or IOException)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.ProfilerAttachUnavailable,
                "CoreCLR Diagnostics IPC 无法附加保留分析 Profiler。",
                exception);
        }
    }

    /// <summary>
    /// 将固定布局协议复制为 AttachProfiler 所需的托管字节数组。
    /// </summary>
    private static byte[] SerializeAttachData(RetentionProfilerAttachData attachData)
    {
        var size = Marshal.SizeOf<RetentionProfilerAttachData>();
        var data = new byte[size];
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(attachData, pointer, fDeleteOld: false);
            Marshal.Copy(pointer, data, 0, size);
            return data;
        }
        finally
        {
            Marshal.DestroyStructure<RetentionProfilerAttachData>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }

    /// <summary>
    /// 对应 Controller DLL 导出函数的 Windows x64 调用约定。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int AttachProfilerDelegate(
        uint processId,
        uint timeoutMilliseconds,
        ref Guid profilerClsid,
        [MarshalAs(UnmanagedType.LPWStr)] string profilerPath,
        IntPtr clientData,
        uint clientDataLength);
}
