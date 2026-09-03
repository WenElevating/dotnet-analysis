using System.Diagnostics;
using System.Runtime.InteropServices;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Tests.Diagnostics;

[TestClass]
public sealed class ProcessMemoryReaderTests
{
    [TestMethod]
    public void ReadPrivateWorkingSetBytesReturnsNativePrivateWorkingSet()
    {
        using var process = Process.GetCurrentProcess();
        var expected = GetPrivateWorkingSetSize(process.Handle);

        var actual = new ProcessMemoryReader().ReadPrivateWorkingSetBytes(process.Id);

        Assert.IsNotNull(actual);
        var difference = Math.Abs(
            Math.Round(expected / (double)Environment.SystemPageSize)
            - Math.Round(actual.Value / (double)Environment.SystemPageSize));

        Assert.IsLessThanOrEqualTo(
            256,
            difference,
            "The two native reads may move while the test process is running.");
    }

    private static long GetPrivateWorkingSetSize(IntPtr processHandle)
    {
        var counters = new ProcessMemoryCountersEx2
        {
            StructureLength = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>()
        };

        Assert.IsTrue(GetProcessMemoryInfo(
            processHandle,
            ref counters,
            counters.StructureLength));

        return checked((long)counters.PrivateWorkingSetSize);
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
