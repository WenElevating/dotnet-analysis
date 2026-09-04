using System.Text.Json;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using Microsoft.Diagnostics.Tracing;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 描述已提升到持久化目录的快照文件及其分配概要。
/// </summary>
internal sealed record StoredSnapshot(
    MemorySnapshotId SnapshotId,
    string FilePath,
    AllocationProfile AllocationProfile);

/// <summary>
/// 抽象快照文件可读性校验，便于存储流程隔离文件格式检查。
/// </summary>
internal interface ISnapshotReadabilityValidator
{
    /// <summary>
    /// 验证指定文件可以被诊断读取器解析。
    /// </summary>
    Task ValidateAsync(string filePath, CancellationToken cancellationToken);
}

/// <summary>
/// 使用文件头和 EventPipe 事件验证快照可读性的实现。
/// </summary>
internal sealed class FileSnapshotReadabilityValidator : ISnapshotReadabilityValidator
{
    /// <summary>
    /// 验证文件包含 FastSerialization 头或至少一条 EventPipe 事件。
    /// </summary>
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

/// <summary>
/// 负责临时快照提升、清单持久化和路径恢复的内部存储。
/// </summary>
internal sealed class MemorySnapshotStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly SnapshotStorageLayout _layout;
    private readonly ImportedSnapshotCatalog _catalog;
    private readonly ISnapshotReadabilityValidator _readabilityValidator;

    /// <summary>
    /// 创建快照提升、清单持久化和路径解析所需的存储。
    /// </summary>
    public MemorySnapshotStore(
        SnapshotStorageLayout layout,
        ImportedSnapshotCatalog catalog,
        ISnapshotReadabilityValidator? readabilityValidator = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _readabilityValidator = readabilityValidator ?? new FileSnapshotReadabilityValidator();
    }

    /// <summary>
    /// 校验临时快照并原子提升为最终文件。
    /// </summary>
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

    /// <summary>
    /// 清理临时堆文件及其同名清单。
    /// </summary>
    public static Task DeleteTemporaryAsync(string temporaryPath)
    {
        if (!string.IsNullOrWhiteSpace(temporaryPath))
        {
            TryDelete(temporaryPath);
            TryDelete(Path.ChangeExtension(temporaryPath, ".json"));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 从内存目录或持久化清单恢复快照存储信息。
    /// </summary>
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

    /// <summary>
    /// 尽力删除文件；清理阶段忽略文件不存在和权限错误。
    /// </summary>
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

    /// <summary>
    /// 把快照元数据序列化到指定清单路径。
    /// </summary>
    private static async Task WriteManifestAsync(
        string manifestPath,
        StoredSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var stream = File.Create(manifestPath);
        var manifest = StoredSnapshotManifest.From(snapshot);
        await JsonSerializer.SerializeAsync(stream, manifest, s_jsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 快照清单的 JSON 传输模型。
    /// </summary>
    private sealed class StoredSnapshotManifest
    {
        public MemorySnapshotId SnapshotId { get; init; }

        public string FilePath { get; init; } = string.Empty;

        public AllocationProfileManifest AllocationProfile { get; init; } = new();

        /// <summary>
        /// 从运行时快照模型创建可序列化清单。
        /// </summary>
        public static StoredSnapshotManifest From(StoredSnapshot snapshot) =>
            new()
            {
                SnapshotId = snapshot.SnapshotId,
                FilePath = snapshot.FilePath,
                AllocationProfile = AllocationProfileManifest.From(snapshot.AllocationProfile)
            };
    }

    /// <summary>
    /// 分配概要清单的 JSON 传输模型。
    /// </summary>
    private sealed class AllocationProfileManifest
    {
        public DateTimeOffset StartedAtUtc { get; init; }

        public DateTimeOffset EndedAtUtc { get; init; }

        public AllocationProfileDataQuality DataQuality { get; init; }

        public List<HotspotManifest> Hotspots { get; init; } = [];

        /// <summary>
        /// 从分配概要创建清单模型。
        /// </summary>
        public static AllocationProfileManifest From(AllocationProfile profile) =>
            new()
            {
                StartedAtUtc = profile.StartedAtUtc,
                EndedAtUtc = profile.EndedAtUtc,
                DataQuality = profile.DataQuality,
                Hotspots = profile.Hotspots.Select(HotspotManifest.From).ToList()
            };

        /// <summary>
        /// 把清单模型还原为核心分配概要。
        /// </summary>
        public AllocationProfile ToAllocationProfile() =>
            new(
                StartedAtUtc,
                EndedAtUtc,
                Hotspots.Select(hotspot => hotspot.ToAllocationHotspot()).ToArray(),
                DataQuality);
    }

    /// <summary>
    /// 分配热点清单的 JSON 传输模型。
    /// </summary>
    private sealed class HotspotManifest
    {
        public string TypeName { get; init; } = string.Empty;

        public string? AssemblyName { get; init; }

        public long ObservedAllocatedBytes { get; init; }

        public List<CallStackFrameManifest> Frames { get; init; } = [];

        /// <summary>
        /// 从分配热点创建清单模型。
        /// </summary>
        public static HotspotManifest From(AllocationHotspot hotspot) =>
            new()
            {
                TypeName = hotspot.Type.TypeName,
                AssemblyName = hotspot.Type.AssemblyName,
                ObservedAllocatedBytes = hotspot.ObservedAllocatedBytes,
                Frames = hotspot.Frames.Select(CallStackFrameManifest.From).ToList()
            };

        /// <summary>
        /// 把清单模型还原为核心分配热点。
        /// </summary>
        public AllocationHotspot ToAllocationHotspot() =>
            new(
                new TypeIdentity(TypeName, AssemblyName),
                ObservedAllocatedBytes,
                Frames.Select(frame => frame.ToCallStackFrame()).ToArray());
    }

    /// <summary>
    /// 调用栈帧清单的 JSON 传输模型。
    /// </summary>
    private sealed class CallStackFrameManifest
    {
        public string Name { get; init; } = string.Empty;

        public string? ModuleName { get; init; }

        public int? LineNumber { get; init; }

        /// <summary>
        /// 从调用栈帧创建清单模型。
        /// </summary>
        public static CallStackFrameManifest From(CallStackFrame frame) =>
            new()
            {
                Name = frame.Name,
                ModuleName = frame.ModuleName,
                LineNumber = frame.LineNumber
            };

        /// <summary>
        /// 把清单模型还原为核心调用栈帧。
        /// </summary>
        public CallStackFrame ToCallStackFrame() => new(Name, ModuleName, LineNumber);
    }
}
