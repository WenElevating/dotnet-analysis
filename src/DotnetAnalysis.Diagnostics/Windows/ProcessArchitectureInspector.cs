using System.Diagnostics;
using System.Runtime.InteropServices;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 检查目标进程映像架构的内部边界，避免把 Windows API 泄漏到上层契约。
/// </summary>
internal interface IProcessArchitectureInspector
{
    /// <summary>
    /// 判断目标进程是否为原生 AMD64 映像。
    /// </summary>
    bool IsAmd64(TargetProcess process);
}

/// <summary>
/// 使用 IsWow64Process2 检查目标进程及宿主原生机器架构。
/// </summary>
internal sealed class ProcessArchitectureInspector : IProcessArchitectureInspector
{
    private const ushort ImageFileMachineUnknown = 0;
    private const ushort ImageFileMachineAmd64 = 0x8664;

    /// <inheritdoc />
    public bool IsAmd64(TargetProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var target = Process.GetProcessById(process.ProcessId);
            if (!IsWow64Process2(target.Handle, out var processMachine, out var nativeMachine))
            {
                return false;
            }

            return nativeMachine == ImageFileMachineAmd64
                && processMachine == ImageFileMachineUnknown;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException
                or EntryPointNotFoundException
                or DllNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(
        IntPtr process,
        out ushort processMachine,
        out ushort nativeMachine);
}
