using System.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

public interface IProcessCatalog
{
    IEnumerable<Process> GetProcesses();
}

public sealed class SystemProcessCatalog : IProcessCatalog
{
    public IEnumerable<Process> GetProcesses() => Process.GetProcesses();
}

public sealed class ProcessEnumerator
{
    private readonly IProcessCatalog _catalog;

    public ProcessEnumerator(IProcessCatalog? catalog = null)
    {
        _catalog = catalog ?? new SystemProcessCatalog();
    }

    public IReadOnlyList<TargetProcess> Enumerate()
    {
        var result = new List<TargetProcess>();
        foreach (var process in _catalog.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var startedAtUtc = process.StartTime.ToUniversalTime();
                    string? executablePath = null;
                    try
                    {
                        executablePath = process.MainModule?.FileName;
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    result.Add(new TargetProcess(process.Id, startedAtUtc, process.ProcessName, executablePath));
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            }
        }

        return result;
    }
}
