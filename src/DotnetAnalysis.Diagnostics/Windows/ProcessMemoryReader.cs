using System.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

public interface IProcessMemoryReader
{
    long? ReadPrivateWorkingSetBytes(int processId);
}

public sealed class ProcessMemoryReader : IProcessMemoryReader
{
    public long? ReadPrivateWorkingSetBytes(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.PrivateMemorySize64 >= 0 ? process.PrivateMemorySize64 : null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
