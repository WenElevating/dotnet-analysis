using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using Microsoft.Diagnostics.Tracing;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 描述已提升到持久化目录的快照文件及其分配概要。
/// </summary>
internal sealed record StoredSnapshot(
    MemorySnapshot Snapshot,
    string FilePath,
    AllocationProfile AllocationProfile,
    SnapshotStorageFormat SnapshotFormat = SnapshotStorageFormat.GCDump)
{
    /// <summary>
    /// 持久化快照的稳定标识。
    /// </summary>
    public MemorySnapshotId SnapshotId => Snapshot.Id;
}

/// <summary>
/// 表示由应用持久化的快照文件格式；该信息仅用于 Diagnostics 内部选择读取器和存储规则。
/// </summary>
internal enum SnapshotStorageFormat
{
    /// <summary>
    /// 官方 FastSerialization/EventPipe GCDump 文件。
    /// </summary>
    GCDump,

    /// <summary>
    /// 由保留分析 Profiler 输出的受校验专用对象图文件。
    /// </summary>
    RetentionHeap
}

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
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SnapshotStorageLayout _layout;
    private readonly ImportedSnapshotCatalog _catalog;
    private readonly ISnapshotReadabilityValidator _readabilityValidator;
    private readonly IRetentionSnapshotStorageGuard _retentionStorageGuard;

    /// <summary>
    /// 创建快照提升、清单持久化和路径解析所需的存储。
    /// </summary>
    public MemorySnapshotStore(
        SnapshotStorageLayout layout,
        ImportedSnapshotCatalog catalog,
        ISnapshotReadabilityValidator? readabilityValidator = null,
        IRetentionSnapshotStorageGuard? retentionStorageGuard = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _readabilityValidator = readabilityValidator ?? new FileSnapshotReadabilityValidator();
        _retentionStorageGuard = retentionStorageGuard ?? new RetentionSnapshotStorageGuard(_layout);
    }

    /// <summary>
    /// 校验临时快照并原子提升为最终文件。
    /// </summary>
    public async Task<StoredSnapshot> PromoteAsync(
        MemorySnapshot snapshot,
        string temporaryPath,
        AllocationProfile allocationProfile,
        CancellationToken cancellationToken)
    {
        return await PromoteCoreAsync(
            snapshot,
            temporaryPath,
            allocationProfile,
            SnapshotStorageFormat.GCDump,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 校验并提升 Profiler 输出的保留分析专用快照，且不会将它伪装成 GCDump。
    /// </summary>
    /// <param name="snapshot">已成功采集但尚待分析的快照元数据。</param>
    /// <param name="temporaryPath">包含完整标记与校验和的临时保留快照路径。</param>
    /// <param name="allocationProfile">本次捕获封存的分配区间概要。</param>
    /// <param name="cancellationToken">取消当前验证或提升操作的令牌。</param>
    /// <returns>包含保留格式和最终路径的已持久化快照。</returns>
    public async Task<StoredSnapshot> PromoteRetentionAsync(
        MemorySnapshot snapshot,
        string temporaryPath,
        AllocationProfile allocationProfile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentNullException.ThrowIfNull(allocationProfile);
        using var promotionReservation = await _retentionStorageGuard.ReservePromotionAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
        await RetentionHeapSnapshot.ReadIndexAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
        return await PromoteCoreAsync(
            snapshot,
            temporaryPath,
            allocationProfile,
            SnapshotStorageFormat.RetentionHeap,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 为不同格式执行共用的完整性计算、原子提升和清单写入。
    /// </summary>
    private async Task<StoredSnapshot> PromoteCoreAsync(
        MemorySnapshot snapshot,
        string temporaryPath,
        AllocationProfile allocationProfile,
        SnapshotStorageFormat snapshotFormat,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentNullException.ThrowIfNull(allocationProfile);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshotId = snapshot.Id;
        try
        {
            if (!File.Exists(temporaryPath))
            {
                throw new IOException("Temporary snapshot does not exist.");
            }

            if (snapshotFormat is SnapshotStorageFormat.GCDump)
            {
                await _readabilityValidator.ValidateAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            }

            var snapshotDirectory = _layout.GetSnapshotDirectory(snapshotId);
            Directory.CreateDirectory(snapshotDirectory);
            var finalDumpPath = GetFinalSnapshotPath(snapshotId, snapshotFormat);
            var finalManifestPath = _layout.GetFinalManifestPath(snapshotId);
            var temporaryManifestPath = _layout.GetTemporaryManifestPath(snapshotId);

            var stored = new StoredSnapshot(snapshot, finalDumpPath, allocationProfile, snapshotFormat);
            var integrity = await SnapshotIntegrity.CreateAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            await WriteManifestAsync(temporaryManifestPath, stored, integrity, cancellationToken).ConfigureAwait(false);
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
            TryDelete(GetFinalSnapshotPath(snapshotId, snapshotFormat));
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
    /// 根据受管快照格式返回专属最终文件路径。
    /// </summary>
    private string GetFinalSnapshotPath(MemorySnapshotId snapshotId, SnapshotStorageFormat snapshotFormat) =>
        snapshotFormat is SnapshotStorageFormat.GCDump
            ? _layout.GetFinalDumpPath(snapshotId)
            : _layout.GetFinalRetentionHeapPath(snapshotId);

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
        var manifestFile = _layout.GetFinalManifestPath(snapshotId);
        if (!File.Exists(manifestFile))
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.CaptureFailed,
                "Snapshot is not available.");
        }

        var stored = await ReadManifestAsync(manifestFile, cancellationToken).ConfigureAwait(false);
        if (stored.SnapshotId != snapshotId)
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.CaptureFailed, "Snapshot manifest identity does not match its directory.");
        }

        _catalog.Register(snapshotId, stored.FilePath);
        return stored;
    }

    /// <summary>
    /// 尝试从受管快照同目录的版本化清单恢复捕获快照；外部文件返回空。
    /// </summary>
    public async Task<StoredSnapshot?> TryRestoreAsync(string filePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(normalizedPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var manifestPath = Path.Combine(directory, "snapshot.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var stored = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(stored.FilePath, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        _catalog.Register(stored.SnapshotId, stored.FilePath);
        return stored;
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
        SnapshotIntegrity integrity,
        CancellationToken cancellationToken)
    {
        await using var stream = File.Create(manifestPath);
        var manifest = StoredSnapshotManifest.From(snapshot, integrity);
        await JsonSerializer.SerializeAsync(stream, manifest, s_jsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读取、验证并还原版本化快照清单。
    /// </summary>
    private async Task<StoredSnapshot> ReadManifestAsync(string manifestPath, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            var manifest = await JsonSerializer.DeserializeAsync<StoredSnapshotManifest>(stream, s_jsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Snapshot manifest is empty.");
            var stored = manifest.ToStoredSnapshot(_layout);
            await manifest.Integrity.ValidateAsync(stored.FilePath, cancellationToken).ConfigureAwait(false);
            return stored;
        }
        catch (DiagnosticsException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or JsonException
                or CryptographicException)
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.CaptureFailed, "Snapshot manifest is invalid or the snapshot file is damaged.", exception);
        }
    }

    /// <summary>
    /// 快照清单的 JSON 传输模型。
    /// </summary>
    private sealed class StoredSnapshotManifest
    {
        public int ManifestVersion { get; init; }

        public Guid SnapshotId { get; init; }

        public MemorySnapshotOrigin Origin { get; init; }

        public DateTimeOffset RequestedAtUtc { get; init; }

        public DateTimeOffset? CaptureStartedAtUtc { get; init; }

        public DateTimeOffset? CapturedAtUtc { get; init; }

        public string RelativeFilePath { get; init; } = string.Empty;

        public SnapshotStorageFormat SnapshotFormat { get; init; }

        public SnapshotIntegrity Integrity { get; init; } = new();

        public AllocationProfileManifest AllocationProfile { get; init; } = new();

        /// <summary>
        /// 从运行时快照模型创建可序列化清单。
        /// </summary>
        public static StoredSnapshotManifest From(StoredSnapshot snapshot, SnapshotIntegrity integrity) =>
            new()
            {
                ManifestVersion = 2,
                SnapshotId = snapshot.SnapshotId.Value,
                Origin = snapshot.Snapshot.Origin,
                RequestedAtUtc = snapshot.Snapshot.RequestedAtUtc,
                CaptureStartedAtUtc = snapshot.Snapshot.CaptureStartedAtUtc,
                CapturedAtUtc = snapshot.Snapshot.CapturedAtUtc,
                RelativeFilePath = Path.Combine(
                    snapshot.SnapshotId.ToString(),
                    Path.GetFileName(snapshot.FilePath)),
                SnapshotFormat = snapshot.SnapshotFormat,
                Integrity = integrity,
                AllocationProfile = AllocationProfileManifest.From(snapshot.AllocationProfile)
            };

        public StoredSnapshot ToStoredSnapshot(SnapshotStorageLayout layout)
        {
            if (ManifestVersion is < 1 or > 2 || string.IsNullOrWhiteSpace(RelativeFilePath))
            {
                throw new InvalidDataException("Snapshot manifest version or relative path is invalid.");
            }

            var root = Path.GetFullPath(layout.RootDirectory);
            var filePath = Path.GetFullPath(Path.Combine(root, RelativeFilePath));
            var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (!filePath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Snapshot manifest path escapes the managed storage directory.");
            }

            var snapshot = new MemorySnapshot(
                new MemorySnapshotId(SnapshotId),
                Origin,
                RequestedAtUtc,
                CaptureStartedAtUtc,
                CapturedAtUtc,
                MemorySnapshotState.Analyzing);
            var snapshotFormat = ManifestVersion == 1
                ? SnapshotStorageFormat.GCDump
                : SnapshotFormat;
            if (!Enum.IsDefined(snapshotFormat))
            {
                throw new InvalidDataException("Snapshot manifest format is invalid.");
            }

            return new StoredSnapshot(snapshot, filePath, AllocationProfile.ToAllocationProfile(), snapshotFormat);
        }
    }

    /// <summary>
    /// 分配概要清单的 JSON 传输模型。
    /// </summary>
    private sealed class AllocationProfileManifest
    {
        public DateTimeOffset StartedAtUtc { get; init; }

        public DateTimeOffset EndedAtUtc { get; init; }

        public AllocationProfileDataQuality DataQuality { get; init; }

        public AllocationCallStackQuality CallStackQuality { get; init; }

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
                CallStackQuality = profile.CallStackQuality,
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
                DataQuality,
                CallStackQuality);
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

    /// <summary>
    /// 描述快照文件长度和 SHA-256 校验值，用于打开前检测损坏或替换。
    /// </summary>
    private sealed class SnapshotIntegrity
    {
        public long LengthBytes { get; init; }

        public string Sha256 { get; init; } = string.Empty;

        public static async Task<SnapshotIntegrity> CreateAsync(string filePath, CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return new SnapshotIntegrity
            {
                LengthBytes = stream.Length,
                Sha256 = Convert.ToHexString(hash)
            };
        }

        public async Task ValidateAsync(string filePath, CancellationToken cancellationToken)
        {
            if (LengthBytes < 0 || string.IsNullOrWhiteSpace(Sha256) || !File.Exists(filePath))
            {
                throw new InvalidDataException("Snapshot integrity metadata is invalid.");
            }

            var actual = await CreateAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (actual.LengthBytes != LengthBytes
                || !string.Equals(actual.Sha256, Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Snapshot file does not match its manifest integrity data.");
            }
        }
    }
}
