using System.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 抽象系统进程枚举来源，便于替换为测试数据。
/// </summary>
public interface IProcessCatalog
{
    /// <summary>
    /// 枚举当前系统可见的进程对象。
    /// </summary>
    IEnumerable<Process> GetProcesses();
}

/// <summary>
/// 使用 .NET 系统 API 枚举当前进程。
/// </summary>
public sealed class SystemProcessCatalog : IProcessCatalog
{
    /// <summary>
    /// 通过系统 API 获取进程列表。
    /// </summary>
    public IEnumerable<Process> GetProcesses() => Process.GetProcesses();
}

/// <summary>
/// 把系统进程对象转换为稳定的目标进程身份模型。
/// </summary>
public sealed class ProcessEnumerator
{
    private readonly IProcessCatalog _catalog;

    /// <summary>
    /// 创建进程枚举器，可注入测试用进程目录。
    /// </summary>
    public ProcessEnumerator(IProcessCatalog? catalog = null)
    {
        _catalog = catalog ?? new SystemProcessCatalog();
    }

    /// <summary>
    /// 读取进程启动时间、名称和可选可执行文件路径。
    /// </summary>
    /// <returns>无法访问或已退出的进程会被跳过。</returns>
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
