using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

internal sealed record IntegrationTargetOptions(
    int? InitialObjectCount = null,
    bool EnableExecutionWorkload = false,
    bool CopyWithoutPdb = false);

public sealed class IntegrationTestHost : IAsyncDisposable
{
    private const string ExecutionWorkloadVariable = "DOTNET_ANALYSIS_TEST_EXECUTION_WORKLOAD";
    private const string TargetPdbFileName = "DotnetAnalysis.Diagnostics.TestTarget.pdb";
    private static readonly Lazy<ReadOnlyCollection<int>> s_installedRuntimeMajorVersions = new(LoadInstalledRuntimeMajorVersions);
    private readonly string? _copiedOutputDirectory;
    private readonly Process _process;

    private IntegrationTestHost(Process process, string? copiedOutputDirectory)
    {
        _process = process;
        _copiedOutputDirectory = copiedOutputDirectory;
    }

    public int ProcessId => _process.Id;

    /// <summary>
    /// 向受控目标发送一条命令，并异步读取其对应的单行响应。
    /// </summary>
    /// <param name="command">目标程序识别的非空命令文本。</param>
    /// <param name="cancellationToken">取消写入或等待响应的令牌。</param>
    /// <returns>目标输出的响应行；目标提前退出时为空。</returns>
    internal async Task<string?> SendCommandAsync(string command, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        cancellationToken.ThrowIfCancellationRequested();
        await _process.StandardInput.WriteLineAsync(command).WaitAsync(cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        return await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
    }

    public static ReadOnlyCollection<int> GetInstalledRuntimeMajorVersions() => s_installedRuntimeMajorVersions.Value;

    public static IEnumerable<string> GetSupportedTargetFrameworks() =>
        GetSupportedTargetFrameworks(GetInstalledRuntimeMajorVersions());

    internal static IEnumerable<string> GetSupportedTargetFrameworks(IReadOnlyCollection<int> installedRuntimeMajorVersions)
    {
        ArgumentNullException.ThrowIfNull(installedRuntimeMajorVersions);

        foreach (var runtimeMajorVersion in new[] { 8, 9, 10 })
        {
            if (installedRuntimeMajorVersions.Contains(runtimeMajorVersion))
            {
                yield return $"net{runtimeMajorVersion}.0";
            }
        }
    }

    public static string ResolveTargetExecutablePath(string targetFramework)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFramework);

        var projectDirectory = GetTargetProjectDirectory();
        var candidates = new[]
        {
            Path.Combine(projectDirectory, "bin", "Debug", targetFramework, "DotnetAnalysis.Diagnostics.TestTarget.exe"),
            Path.Combine(projectDirectory, "bin", "x64", "Debug", targetFramework, "DotnetAnalysis.Diagnostics.TestTarget.exe"),
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
        return await StartTargetAsync(targetFramework, (int?)null).ConfigureAwait(false);
    }

    /// <summary>
    /// 启动指定运行时版本的受控目标进程，并可在启动前保留指定数量的对象。
    /// </summary>
    /// <param name="targetFramework">目标进程要使用的目标框架。</param>
    /// <param name="initialObjectCount">启动时要保留的字节数组数量；空值表示默认小堆。</param>
    /// <returns>已输出 READY 的受控目标宿主。</returns>
    public static async Task<IntegrationTestHost> StartTargetAsync(string targetFramework, int? initialObjectCount)
    {
        return await StartTargetAsync(
            targetFramework,
            new IntegrationTargetOptions(InitialObjectCount: initialObjectCount)).ConfigureAwait(false);
    }

    internal static async Task<IntegrationTestHost> StartTargetAsync(
        string targetFramework,
        IntegrationTargetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var targetExecutable = ResolveTargetExecutablePath(targetFramework);
        string? copiedOutputDirectory = null;
        Process? process = null;
        try
        {
            if (options.CopyWithoutPdb)
            {
                (targetExecutable, copiedOutputDirectory) = CopyTargetOutputWithoutPdb(targetExecutable);
            }

            var psi = CreateTargetProcessStartInfo(targetExecutable, options);
            process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start target process.");
            var ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (!string.Equals(ready, "READY", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Target process did not become ready.");
            }

            return new IntegrationTestHost(process, copiedOutputDirectory);
        }
        catch
        {
            if (process is not null)
            {
                await TerminateProcessAsync(process);
                process.Dispose();
            }

            await DeleteCopiedOutputDirectoryAsync(copiedOutputDirectory);
            throw;
        }
    }

    internal static ProcessStartInfo CreateTargetProcessStartInfo(
        string targetExecutable,
        IntegrationTargetOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetExecutable);
        ArgumentNullException.ThrowIfNull(options);
        var processStartInfo = new ProcessStartInfo(targetExecutable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (options.InitialObjectCount is { } count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            processStartInfo.Environment["DOTNET_ANALYSIS_TEST_OBJECT_COUNT"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        processStartInfo.Environment[ExecutionWorkloadVariable] = options.EnableExecutionWorkload ? "true" : "false";
        return processStartInfo;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                await _process.StandardInput.WriteLineAsync("EXIT");
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch
        {
            await TerminateProcessAsync(_process);
        }
        finally
        {
            _process.Dispose();
            await DeleteCopiedOutputDirectoryAsync(_copiedOutputDirectory);
        }
    }

    private static (string ExecutablePath, string OutputDirectory) CopyTargetOutputWithoutPdb(
        string targetExecutable)
    {
        var sourceDirectory = Path.GetDirectoryName(targetExecutable)
            ?? throw new InvalidOperationException("The target executable must have an output directory.");
        var copiedOutputDirectory = Path.Combine(
            Path.GetTempPath(),
            "DotnetAnalysis.Diagnostics.IntegrationTests",
            "TargetsWithoutPdb",
            Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(sourcePath), TargetPdbFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
                var destinationPath = Path.Combine(copiedOutputDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Copy(sourcePath, destinationPath);
            }

            return (
                Path.Combine(copiedOutputDirectory, Path.GetFileName(targetExecutable)),
                copiedOutputDirectory);
        }
        catch
        {
            if (Directory.Exists(copiedOutputDirectory))
            {
                Directory.Delete(copiedOutputDirectory, recursive: true);
            }

            throw;
        }
    }

    private static async Task TerminateProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException or Win32Exception)
        {
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or TimeoutException or Win32Exception)
        {
        }
    }

    private static async Task DeleteCopiedOutputDirectoryAsync(string? copiedOutputDirectory)
    {
        if (copiedOutputDirectory is null)
        {
            return;
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(copiedOutputDirectory))
                {
                    Directory.Delete(copiedOutputDirectory, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (
                attempt < 2 && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
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
