using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DotnetAnalysis.Diagnostics.Windows;

public interface IProcessMemoryReader
{
    long? ReadPrivateWorkingSetBytes(int processId);
}

public sealed class ProcessMemoryReader : IProcessMemoryReader
{
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

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(
        IntPtr process,
        ref ProcessMemoryCountersEx2 counters,
        uint size);

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
