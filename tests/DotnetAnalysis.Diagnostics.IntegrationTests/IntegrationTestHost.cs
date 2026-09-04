using System.Collections.ObjectModel;
using System.Diagnostics;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

public sealed class IntegrationTestHost : IAsyncDisposable
{
    private static readonly Lazy<ReadOnlyCollection<int>> s_installedRuntimeMajorVersions = new(LoadInstalledRuntimeMajorVersions);
    private readonly Process _process;

    private IntegrationTestHost(Process process) => _process = process;

    public int ProcessId => _process.Id;

    public static ReadOnlyCollection<int> GetInstalledRuntimeMajorVersions() => s_installedRuntimeMajorVersions.Value;

    public static IEnumerable<string> GetSupportedTargetFrameworks()
    {
        yield return "net8.0";

        if (GetInstalledRuntimeMajorVersions().Contains(9))
        {
            yield return "net9.0";
        }

        yield return "net10.0";
    }

    public static string ResolveTargetExecutablePath(string targetFramework)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFramework);

        var projectDirectory = GetTargetProjectDirectory();
        var candidates = new[]
        {
            Path.Combine(projectDirectory, "bin", "x64", "Debug", targetFramework, "DotnetAnalysis.Diagnostics.TestTarget.exe"),
            Path.Combine(projectDirectory, "bin", "Debug", targetFramework, "DotnetAnalysis.Diagnostics.TestTarget.exe"),
            Path.Combine(projectDirectory, "bin", "x64", "Release", targetFramework, "DotnetAnalysis.Diagnostics.TestTarget.exe"),
            Path.Combine(projectDirectory, "bin", "Release", targetFramework, "DotnetAnalysis.Diagnostics.TestTarget.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Could not find the built target executable for {targetFramework}.",
            candidates[0]);
    }

    public static async Task<IntegrationTestHost> StartTargetAsync(string targetFramework)
    {
        return await StartTargetAsync(targetFramework, null).ConfigureAwait(false);
    }

    /// <summary>
    /// 启动指定运行时版本的受控目标进程，并可在启动前保留指定数量的对象。
    /// </summary>
    /// <param name="targetFramework">目标进程要使用的目标框架。</param>
    /// <param name="initialObjectCount">启动时要保留的字节数组数量；空值表示默认小堆。</param>
    /// <returns>已输出 READY 的受控目标宿主。</returns>
    public static async Task<IntegrationTestHost> StartTargetAsync(string targetFramework, int? initialObjectCount)
    {
        var targetExecutable = ResolveTargetExecutablePath(targetFramework);
        var psi = new ProcessStartInfo(targetExecutable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (initialObjectCount is { } count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            psi.Environment["DOTNET_ANALYSIS_TEST_OBJECT_COUNT"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start target process.");
        var ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        if (!string.Equals(ready, "READY", StringComparison.Ordinal))
        {
            process.Dispose();
            throw new InvalidOperationException("Target process did not become ready.");
        }

        return new IntegrationTestHost(process);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _process.StandardInput.WriteLineAsync("EXIT");
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        }
        finally
        {
            _process.Dispose();
        }
    }

    private static ReadOnlyCollection<int> LoadInstalledRuntimeMajorVersions()
    {
        var psi = new ProcessStartInfo("dotnet", "--list-runtimes")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not query installed dotnet runtimes.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        var majors = new HashSet<int>();
        foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("Microsoft.NETCore.App ", StringComparison.Ordinal))
            {
                continue;
            }

            var versionText = line["Microsoft.NETCore.App ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
            if (Version.TryParse(versionText, out var version))
            {
                majors.Add(version.Major);
            }
        }

        return new ReadOnlyCollection<int>(majors.OrderBy(major => major).ToArray());
    }

    private static string GetTargetProjectDirectory() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DotnetAnalysis.Diagnostics.TestTarget"));
}
