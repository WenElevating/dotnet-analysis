using System.Text.Json;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using Microsoft.Diagnostics.Tracing;

namespace DotnetAnalysis.Diagnostics.Windows;

internal sealed record StoredSnapshot(
    MemorySnapshotId SnapshotId,
    string FilePath,
    AllocationProfile AllocationProfile);

internal interface ISnapshotReadabilityValidator
{
    Task ValidateAsync(string filePath, CancellationToken cancellationToken);
}

internal sealed class FileSnapshotReadabilityValidator : ISnapshotReadabilityValidator
{
    public async Task ValidateAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length < 32)
        {
            throw new IOException("Snapshot file is too small to be readable.");
        }

        var header = new byte[24];
        var read = await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false);
        if (read >= header.Length
            && BitConverter.ToInt32(header, 0) == 20
            && "!FastSerialization.1"u8.SequenceEqual(header.AsSpan(4, 20)))
        {
            return;
        }

        try
        {
            var sawEvent = false;
            using var source = new EventPipeEventSource(filePath);
            source.AllEvents += _ => sawEvent = true;
            source.Process();
            if (sawEvent)
            {
                return;
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or IOException
                or EndOfStreamException
                or ArgumentException)
        {
        }

        throw new IOException("Snapshot is not a readable gcdump or EventPipe heap stream.");
    }
}

internal sealed class MemorySnapshotStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly SnapshotStorageLayout _layout;
    private readonly ImportedSnapshotCatalog _catalog;
    private readonly ISnapshotReadabilityValidator _readabilityValidator;

    public MemorySnapshotStore(
        SnapshotStorageLayout layout,
        ImportedSnapshotCatalog catalog,
        ISnapshotReadabilityValidator? readabilityValidator = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _readabilityValidator = readabilityValidator ?? new FileSnapshotReadabilityValidator();
    }

    public async Task<StoredSnapshot> PromoteAsync(
        MemorySnapshotId snapshotId,
        string temporaryPath,
        AllocationProfile allocationProfile,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentNullException.ThrowIfNull(allocationProfile);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!File.Exists(temporaryPath))
            {
                throw new IOException("Temporary snapshot does not exist.");
            }

            await _readabilityValidator.ValidateAsync(temporaryPath, cancellationToken).ConfigureAwait(false);

            var snapshotDirectory = _layout.GetSnapshotDirectory(snapshotId);
            Directory.CreateDirectory(snapshotDirectory);
            var finalDumpPath = _layout.GetFinalDumpPath(snapshotId);
            var finalManifestPath = _layout.GetFinalManifestPath(snapshotId);
            var temporaryManifestPath = _layout.GetTemporaryManifestPath(snapshotId);

            var stored = new StoredSnapshot(snapshotId, finalDumpPath, allocationProfile);
            await WriteManifestAsync(temporaryManifestPath, stored, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, finalDumpPath, false);
            try
            {
                File.Move(temporaryManifestPath, finalManifestPath, false);
            }
            catch
            {
                TryDelete(finalDumpPath);
                throw;
            }
            _catalog.Register(snapshotId, finalDumpPath);
            return stored;
        }
        catch (Exception exception)
        {
            await DeleteTemporaryAsync(temporaryPath).ConfigureAwait(false);
            TryDelete(_layout.GetFinalDumpPath(snapshotId));
            TryDelete(_layout.GetFinalManifestPath(snapshotId));
            TryDelete(_layout.GetTemporaryManifestPath(snapshotId));
            if (exception is DiagnosticsException diagnosticsException)
            {
                throw diagnosticsException;
            }

            throw new DiagnosticsException(DiagnosticsErrorCode.CaptureFailed, "Snapshot promotion failed.", exception);
        }
    }

    public static Task DeleteTemporaryAsync(string temporaryPath)
    {
        if (!string.IsNullOrWhiteSpace(temporaryPath))
        {
            TryDelete(temporaryPath);
            TryDelete(Path.ChangeExtension(temporaryPath, ".json"));
        }

        return Task.CompletedTask;
    }

    public async Task<StoredSnapshot> ResolveAsync(MemorySnapshotId snapshotId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_catalog.TryResolve(snapshotId, out var path))
        {
            var manifestPath = _layout.GetFinalManifestPath(snapshotId);
            if (!File.Exists(manifestPath))
            {
                throw new DiagnosticsException(
                    DiagnosticsErrorCode.CaptureFailed,
                    "Snapshot is not available.");
            }

            await using var stream = File.OpenRead(manifestPath);
            var manifest = await JsonSerializer.DeserializeAsync<StoredSnapshotManifest>(stream, s_jsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new DiagnosticsException(DiagnosticsErrorCode.CaptureFailed, "Snapshot manifest is empty.");

            path = manifest.FilePath;
            _catalog.Register(snapshotId, path);
        }

        var manifestFile = _layout.GetFinalManifestPath(snapshotId);
        await using var manifestStream = File.OpenRead(manifestFile);
        var loaded = await JsonSerializer.DeserializeAsync<StoredSnapshotManifest>(manifestStream, s_jsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new DiagnosticsException(DiagnosticsErrorCode.CaptureFailed, "Snapshot manifest is empty.");
        return new StoredSnapshot(loaded.SnapshotId, loaded.FilePath, loaded.AllocationProfile.ToAllocationProfile());
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task WriteManifestAsync(
        string manifestPath,
        StoredSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var stream = File.Create(manifestPath);
        var manifest = StoredSnapshotManifest.From(snapshot);
        await JsonSerializer.SerializeAsync(stream, manifest, s_jsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private sealed class StoredSnapshotManifest
    {
        public MemorySnapshotId SnapshotId { get; init; }

        public string FilePath { get; init; } = string.Empty;

        public AllocationProfileManifest AllocationProfile { get; init; } = new();

        public static StoredSnapshotManifest From(StoredSnapshot snapshot) =>
            new()
            {
                SnapshotId = snapshot.SnapshotId,
                FilePath = snapshot.FilePath,
                AllocationProfile = AllocationProfileManifest.From(snapshot.AllocationProfile)
            };
    }

    private sealed class AllocationProfileManifest
    {
        public DateTimeOffset StartedAtUtc { get; init; }

        public DateTimeOffset EndedAtUtc { get; init; }

        public AllocationProfileDataQuality DataQuality { get; init; }

        public List<HotspotManifest> Hotspots { get; init; } = [];

        public static AllocationProfileManifest From(AllocationProfile profile) =>
            new()
            {
                StartedAtUtc = profile.StartedAtUtc,
                EndedAtUtc = profile.EndedAtUtc,
                DataQuality = profile.DataQuality,
                Hotspots = profile.Hotspots.Select(HotspotManifest.From).ToList()
            };

        public AllocationProfile ToAllocationProfile() =>
            new(
                StartedAtUtc,
                EndedAtUtc,
                Hotspots.Select(hotspot => hotspot.ToAllocationHotspot()).ToArray(),
                DataQuality);
    }

    private sealed class HotspotManifest
    {
        public string TypeName { get; init; } = string.Empty;

        public string? AssemblyName { get; init; }

        public long ObservedAllocatedBytes { get; init; }

        public List<CallStackFrameManifest> Frames { get; init; } = [];

        public static HotspotManifest From(AllocationHotspot hotspot) =>
            new()
            {
                TypeName = hotspot.Type.TypeName,
                AssemblyName = hotspot.Type.AssemblyName,
                ObservedAllocatedBytes = hotspot.ObservedAllocatedBytes,
                Frames = hotspot.Frames.Select(CallStackFrameManifest.From).ToList()
            };

        public AllocationHotspot ToAllocationHotspot() =>
            new(
                new TypeIdentity(TypeName, AssemblyName),
                ObservedAllocatedBytes,
                Frames.Select(frame => frame.ToCallStackFrame()).ToArray());
    }

    private sealed class CallStackFrameManifest
    {
        public string Name { get; init; } = string.Empty;

        public string? ModuleName { get; init; }

        public int? LineNumber { get; init; }

        public static CallStackFrameManifest From(CallStackFrame frame) =>
            new()
            {
                Name = frame.Name,
                ModuleName = frame.ModuleName,
                LineNumber = frame.LineNumber
            };

        public CallStackFrame ToCallStackFrame() => new(Name, ModuleName, LineNumber);
    }
}
