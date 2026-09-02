using System.Diagnostics;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

public sealed class IntegrationTestHost : IAsyncDisposable
{
    private readonly Process _process;

    private IntegrationTestHost(Process process) => _process = process;

    public int ProcessId => _process.Id;

    public static async Task<IntegrationTestHost> StartTargetAsync(string targetFramework)
    {
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DotnetAnalysis.Diagnostics.TestTarget", "DotnetAnalysis.Diagnostics.TestTarget.csproj"));
        var targetAssembly = Path.Combine(
            Path.GetDirectoryName(project)!,
            "bin",
            "x64",
            "Debug",
            targetFramework,
            "DotnetAnalysis.Diagnostics.TestTarget.dll");
        if (!File.Exists(targetAssembly))
        {
            throw new FileNotFoundException("Target assembly was not built.", targetAssembly);
        }

        var psi = new ProcessStartInfo("dotnet", $"\"{targetAssembly}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
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
}
