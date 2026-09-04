using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 抽象读取 Windows 进程内存计数器的来源。
/// </summary>
public interface IProcessMemoryReader
{
    /// <summary>
    /// 读取进程的原生 Private Working Set 字节数。
    /// </summary>
    /// <returns>读取失败、无权限或进程不存在时返回 <see langword="null"/>。</returns>
    long? ReadPrivateWorkingSetBytes(int processId);
}

/// <summary>
/// 通过 psapi.dll 读取进程 Private Working Set。
/// </summary>
public sealed class ProcessMemoryReader : IProcessMemoryReader
{
    /// <summary>
    /// 检查进程是否仍在运行。
    /// </summary>
    internal static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 读取与任务管理器私有工作集口径一致的进程内存值。
    /// </summary>
    /// <param name="processId">目标进程 ID。</param>
    /// <returns>读取失败、无权限或进程不存在时返回 <see langword="null"/>。</returns>
    public long? ReadPrivateWorkingSetBytes(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var counters = new ProcessMemoryCountersEx2
            {
                StructureLength = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>()
            };

            return GetProcessMemoryInfo(
                    process.Handle,
                    ref counters,
                    counters.StructureLength)
                ? checked((long)counters.PrivateWorkingSetSize)
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 调用 Windows psapi 获取进程内存计数器。
    /// </summary>
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(
        IntPtr process,
        ref ProcessMemoryCountersEx2 counters,
        uint size);

    /// <summary>
    /// 与 Windows PROCESS_MEMORY_COUNTERS_EX2 对齐的托管结构。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2
    {
        public uint StructureLength;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }
}
