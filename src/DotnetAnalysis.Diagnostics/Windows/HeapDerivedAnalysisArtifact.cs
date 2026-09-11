using System.Security.Cryptography;
using System.Text.Json;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 管理基于 .heapidx 的不可变支配树派生工件；构建过程只在后台任务中短暂映射 CSR 文件，
/// 不把对象 DTO、边集合或完整 <see cref="SnapshotIndex"/> 留在查询缓存中。
/// </summary>
internal sealed class HeapDerivedAnalysisArtifact
{
    private const int FormatVersion = 1;
    private const int ObjectRecordBytes = sizeof(ulong) + sizeof(int) + sizeof(long);
    private const int StateRecordBytes = sizeof(int) + sizeof(int) + sizeof(long);
    private const int UnreachableImmediateDominator = -2;
    private const int VirtualRootImmediateDominator = -1;
    private static readonly string[] s_requiredArtifactNames = ["dominator-state.bin", "dominator-order.bin"];
    private readonly string _directory;

    /// <summary>
    /// 创建已验证的派生分析工件读取器。
    /// </summary>
    /// <param name="directory">.heapderived 工件目录。</param>
    /// <param name="objectCount">可从非弱 GC 根到达并参与排序的对象数。</param>
    private HeapDerivedAnalysisArtifact(string directory, int objectCount)
    {
        _directory = directory;
        ObjectCount = objectCount;
    }

    /// <summary>
    /// 可从非弱 GC 根到达的对象数。
    /// </summary>
    public int ObjectCount { get; }

    /// <summary>
    /// 打开已有完整工件，或根据基础 CSR 索引构建并原子发布新的派生工件。
    /// </summary>
    /// <param name="heapIndexDirectory">已验证 .heapidx 工件目录。</param>
    /// <param name="cancellationToken">后台构建取消令牌；共享构建通常传入不可取消令牌。</param>
    /// <param name="storageGuard">可选的工件容量保护器；生产调用省略，测试可注入确定的卷余量。</param>
    /// <returns>可按页读取的派生工件。</returns>
    /// <exception cref="DiagnosticsException">基础索引不完整、资源预算不足或派生工件无法发布时引发。</exception>
    public static HeapDerivedAnalysisArtifact OpenOrBuild(
        string heapIndexDirectory,
        CancellationToken cancellationToken,
        HeapArtifactStorageGuard? storageGuard = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(heapIndexDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(heapIndexDirectory, ".heapderived");
        using var publicationGate = HeapArtifactPublicationGate
            .EnterAsync(directory, cancellationToken)
            .GetAwaiter()
            .GetResult();
        try
        {
            if (TryOpen(directory, cancellationToken, out var existing))
            {
                return existing;
            }
        }
        catch (DiagnosticsException exception) when (exception.ErrorCode == DiagnosticsErrorCode.SnapshotQueryLimitReached)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.DerivedAnalysisUnavailable,
                "当前映射资源不足以验证快照支配树派生工件。",
                exception);
        }

        var parent = Path.GetDirectoryName(directory) ?? throw new InvalidOperationException("派生索引目录缺少父目录。");
        Directory.CreateDirectory(parent);
        storageGuard ??= HeapArtifactStorageGuard.CreateForTargetDirectory(directory);
        var objectPath = Path.Combine(heapIndexDirectory, "objects.bin");
        var objectLength = File.Exists(objectPath) ? new FileInfo(objectPath).Length : 0;
        var estimatedObjectCount = objectLength / ObjectRecordBytes + (objectLength % ObjectRecordBytes == 0 ? 0 : 1);
        var estimatedPeakAdditionalBytes = HeapArtifactStorageGuard.EstimateDerivedBuildPeakBytes(estimatedObjectCount);
        using var publicationReservation = storageGuard
            .ReservePublicationAsync(directory, estimatedPeakAdditionalBytes, cancellationToken)
            .GetAwaiter()
            .GetResult();
        if (Directory.Exists(directory))
        {
            ArchiveInvalidArtifact(directory);
        }

        HeapTemporaryArtifactCleanup.TryDeleteAbandonedDirectories(
            parent,
            $".{Path.GetFileName(directory)}.*.tmp",
            HeapTemporaryArtifactCleanup.AbandonedDirectoryMinimumAge);
        var temporaryDirectory = Path.Combine(parent, $".{Path.GetFileName(directory)}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var result = Build(heapIndexDirectory, temporaryDirectory, cancellationToken);
            WriteManifest(temporaryDirectory, result, cancellationToken);
            publicationReservation
                .ReconcileActualUsageAsync(temporaryDirectory, cancellationToken)
                .GetAwaiter()
                .GetResult();
            Directory.Move(temporaryDirectory, directory);
            return new HeapDerivedAnalysisArtifact(directory, result.ReachableObjectCount);
        }
        catch (DiagnosticsException exception) when (exception.ErrorCode is not (
            DiagnosticsErrorCode.DerivedAnalysisUnavailable
            or DiagnosticsErrorCode.SnapshotStorageLimitReached))
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.DerivedAnalysisUnavailable,
                "当前资源或基础工件状态无法完成快照支配树派生分析。",
                exception);
        }
        catch (DiagnosticsException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or OverflowException
            or OutOfMemoryException
            or ArgumentException)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.DerivedAnalysisUnavailable,
                "无法构建快照支配树派生工件。",
                exception);
        }
        finally
        {
            HeapTemporaryArtifactCleanup.TryDeleteDirectory(temporaryDirectory);
        }
    }

    /// <summary>
    /// 读取已按 retained size 排序的派生条目；仅分配请求页大小的托管数组。
    /// </summary>
    /// <param name="offset">零基排序偏移量。</param>
    /// <param name="pageSize">读取数量，范围由调用方验证。</param>
    /// <param name="cancellationToken">取消当前文件读取的令牌。</param>
    /// <returns>当前页的对象标识、直接支配者和 retained size。</returns>
    public IReadOnlyList<HeapDerivedDominatorEntry> ReadPage(int offset, int pageSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        if (offset > ObjectCount)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset cannot exceed derived object count.");
        }

        var count = Math.Min(pageSize, ObjectCount - offset);
        var values = new HeapDerivedDominatorEntry[count];
        using var order = new BinaryReader(File.OpenRead(Path.Combine(_directory, "dominator-order.bin")));
        using var state = new BinaryReader(File.OpenRead(Path.Combine(_directory, "dominator-state.bin")));
        order.BaseStream.Position = (long)offset * sizeof(int);
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var objectId = order.ReadInt32();
            state.BaseStream.Position = (long)objectId * StateRecordBytes;
            var immediateDominatorObjectId = state.ReadInt32();
            _ = state.ReadInt32();
            var retainedSizeBytes = state.ReadInt64();
            values[index] = new HeapDerivedDominatorEntry(objectId, immediateDominatorObjectId, retainedSizeBytes);
        }

        return values;
    }

    /// <summary>
    /// 验证已发布工件并创建读取器；无清单、版本不匹配或哈希不匹配均视为不可复用。
    /// </summary>
    private static bool TryOpen(string directory, CancellationToken cancellationToken, out HeapDerivedAnalysisArtifact artifact)
    {
        artifact = null!;
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<HeapDerivedManifest>(File.ReadAllText(manifestPath));
            if (manifest is null
                || manifest.Version != FormatVersion
                || !manifest.Completed
                || manifest.ReachableObjectCount < 0
                || !HasRequiredArtifactSet(manifest.Files))
            {
                return false;
            }

            foreach (var file in manifest.Files!)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file is null)
                {
                    return false;
                }

                var path = Path.Combine(directory, file.Name!);
                if (!File.Exists(path) || new FileInfo(path).Length != file.Length)
                {
                    return false;
                }

                if (!string.Equals(ComputeHash(path), file.Sha256, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            var heapIndexDirectory = Path.GetDirectoryName(directory);
            if (heapIndexDirectory is null)
            {
                return false;
            }

            var objectPath = Path.Combine(heapIndexDirectory, "objects.bin");
            if (!File.Exists(objectPath))
            {
                return false;
            }

            var objectLength = new FileInfo(objectPath).Length;
            if (objectLength % ObjectRecordBytes != 0 || objectLength / ObjectRecordBytes > int.MaxValue)
            {
                return false;
            }

            var baseObjectCount = checked((int)(objectLength / ObjectRecordBytes));
            if (manifest.ReachableObjectCount > baseObjectCount)
            {
                return false;
            }

            var stateLength = new FileInfo(Path.Combine(directory, "dominator-state.bin")).Length;
            var orderLength = new FileInfo(Path.Combine(directory, "dominator-order.bin")).Length;
            if (stateLength != checked((long)baseObjectCount * StateRecordBytes)
                || orderLength != (long)manifest.ReachableObjectCount * sizeof(int))
            {
                return false;
            }

            if (!HasValidPayloadEntries(
                    directory,
                    baseObjectCount,
                    manifest.ReachableObjectCount,
                    cancellationToken))
            {
                return false;
            }

            artifact = new HeapDerivedAnalysisArtifact(directory, manifest.ReachableObjectCount);
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException
            or OverflowException
            or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 验证派生清单精确包含固定文件集合，且每条长度和 SHA-256 字段均可用于可信校验。
    /// </summary>
    /// <param name="files">反序列化得到的派生工件条目。</param>
    /// <returns>条目完整、唯一且结构有效时返回 <see langword="true"/>。</returns>
    private static bool HasRequiredArtifactSet(IReadOnlyList<HeapDerivedManifestFile?>? files)
    {
        if (files is null || files.Count != s_requiredArtifactNames.Length)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (file is null
                || file.Length < 0
                || !IsSha256(file.Sha256)
                || string.IsNullOrWhiteSpace(file.Name)
                || !names.Add(file.Name))
            {
                return false;
            }
        }

        return s_requiredArtifactNames.All(names.Contains);
    }

    /// <summary>
    /// 判断清单摘要是否为固定长度的十六进制 SHA-256，避免接受空值或非摘要文本。
    /// </summary>
    /// <param name="value">清单中的摘要字符串。</param>
    /// <returns>字符串可表示 SHA-256 时返回 <see langword="true"/>。</returns>
    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != SHA256.HashSizeInBytes * 2)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 流式验证 order 对象标识的范围与唯一性，并验证每条状态记录的直接支配者标识；
    /// 唯一性使用按基础对象数分配的位图，避免大快照打开时创建高开销哈希集合。
    /// </summary>
    /// <param name="directory">已通过长度和摘要校验的派生工件目录。</param>
    /// <param name="objectCount">基础 objects.bin 中的对象数。</param>
    /// <param name="reachableObjectCount">order 文件声明的可达对象数。</param>
    /// <param name="cancellationToken">取消当前验证读取的令牌。</param>
    /// <returns>所有固定宽度记录均满足派生索引语义时返回 <see langword="true"/>。</returns>
    private static bool HasValidPayloadEntries(
        string directory,
        int objectCount,
        int reachableObjectCount,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(directory)
            ?? throw new InvalidDataException("派生工件目录缺少基础索引父目录。");
        var validationDirectory = Path.Combine(
            parent,
            $"{Path.GetFileName(directory)}.validation.{Guid.NewGuid():N}.tmp");
        HeapTemporaryArtifactCleanup.TryDeleteAbandonedDirectories(
            parent,
            $"{Path.GetFileName(directory)}.validation.*.tmp",
            HeapTemporaryArtifactCleanup.AbandonedDirectoryMinimumAge);
        Directory.CreateDirectory(validationDirectory);
        try
        {
            using var seenObjectIds = HeapMappedBitSet.Create(
                Path.Combine(validationDirectory, "seen-object-ids.bin"),
                objectCount);
            using (var order = new BinaryReader(File.OpenRead(Path.Combine(directory, "dominator-order.bin"))))
            {
                for (var index = 0; index < reachableObjectCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var objectId = order.ReadInt32();
                    if ((uint)objectId >= (uint)objectCount || !seenObjectIds.TrySet(objectId))
                    {
                        return false;
                    }
                }
            }

            using var state = new BinaryReader(File.OpenRead(Path.Combine(directory, "dominator-state.bin")));
            using var objects = new BinaryReader(File.OpenRead(Path.Combine(parent, "objects.bin")));
            for (var objectId = 0; objectId < objectCount; objectId++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var immediateDominatorObjectId = state.ReadInt32();
                var reserved = state.ReadInt32();
                var retainedSizeBytes = state.ReadInt64();
                _ = objects.ReadUInt64();
                _ = objects.ReadInt32();
                var shallowSizeBytes = objects.ReadInt64();
                if (immediateDominatorObjectId < UnreachableImmediateDominator
                    || immediateDominatorObjectId >= objectCount
                    || reserved != 0
                    || shallowSizeBytes < 0)
                {
                    return false;
                }

                var isReachable = seenObjectIds.Contains(objectId);
                if (isReachable)
                {
                    if (immediateDominatorObjectId == UnreachableImmediateDominator
                        || retainedSizeBytes < shallowSizeBytes
                        || immediateDominatorObjectId == objectId
                        || immediateDominatorObjectId >= 0 && !seenObjectIds.Contains(immediateDominatorObjectId))
                    {
                        return false;
                    }
                }
                else if (immediateDominatorObjectId != UnreachableImmediateDominator || retainedSizeBytes != 0)
                {
                    return false;
                }
            }

            return HasValidDominatorOrder(directory, reachableObjectCount, cancellationToken);
        }
        finally
        {
            HeapTemporaryArtifactCleanup.TryDeleteDirectory(validationDirectory);
        }
    }

    /// <summary>
    /// 流式复核已发布 order 的 retained、shallow、address 排序语义，避免自哈希但乱序的工件返回错误分页。
    /// </summary>
    private static bool HasValidDominatorOrder(
        string directory,
        int reachableObjectCount,
        CancellationToken cancellationToken)
    {
        var heapIndexDirectory = Path.GetDirectoryName(directory)
            ?? throw new InvalidDataException("派生工件目录缺少基础索引父目录。");
        using var order = new BinaryReader(File.OpenRead(Path.Combine(directory, "dominator-order.bin")));
        using var state = new BinaryReader(File.OpenRead(Path.Combine(directory, "dominator-state.bin")));
        using var objects = new BinaryReader(File.OpenRead(Path.Combine(heapIndexDirectory, "objects.bin")));
        DominatorSortKey? previous = null;
        for (var index = 0; index < reachableObjectCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var objectId = order.ReadInt32();
            state.BaseStream.Position = (long)objectId * StateRecordBytes + sizeof(int) * 2;
            var retainedSizeBytes = state.ReadInt64();
            objects.BaseStream.Position = (long)objectId * ObjectRecordBytes;
            var address = objects.ReadUInt64();
            _ = objects.ReadInt32();
            var shallowSizeBytes = objects.ReadInt64();
            var current = new DominatorSortKey(retainedSizeBytes, shallowSizeBytes, address, objectId);
            if (previous is not null && CompareDominatorSortKeys(previous.Value, current) > 0)
            {
                return false;
            }

            previous = current;
        }

        return true;
    }

    /// <summary>
    /// 比较两个已发布排序键；负值表示 left 应在 right 之前。
    /// </summary>
    private static int CompareDominatorSortKeys(DominatorSortKey left, DominatorSortKey right)
    {
        var retained = right.RetainedSizeBytes.CompareTo(left.RetainedSizeBytes);
        if (retained != 0)
        {
            return retained;
        }

        var shallow = right.ShallowSizeBytes.CompareTo(left.ShallowSizeBytes);
        if (shallow != 0)
        {
            return shallow;
        }

        var address = left.Address.CompareTo(right.Address);
        return address != 0 ? address : left.ObjectId.CompareTo(right.ObjectId);
    }

    /// <summary>
    /// 将无法验证的历史派生工件保留为带时间戳的证据目录，再发布新的工件，避免自动删除诊断证据。
    /// </summary>
    private static void ArchiveInvalidArtifact(string directory)
    {
        var archivedDirectory = $"{directory}.corrupt.{DateTime.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}";
        try
        {
            Directory.Move(directory, archivedDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.DerivedAnalysisUnavailable,
                "现有支配树派生工件已损坏且无法保留后重建。",
                exception);
        }
    }

    /// <summary>
    /// 从 forward/reverse CSR 运行非递归 Lengauer-Tarjan，并将状态和排序结果顺序写入临时目录。
    /// </summary>
    private static HeapDerivedBuildResult Build(string heapIndexDirectory, string temporaryDirectory, CancellationToken cancellationToken)
    {
        var objectPath = Path.Combine(heapIndexDirectory, "objects.bin");
        var forwardOffsetPath = Path.Combine(heapIndexDirectory, "forward-offsets.bin");
        var forwardTargetPath = Path.Combine(heapIndexDirectory, "forward-targets.bin");
        var reverseOffsetPath = Path.Combine(heapIndexDirectory, "reverse-offsets.bin");
        var reverseTargetPath = Path.Combine(heapIndexDirectory, "reverse-targets.bin");
        var rootPath = Path.Combine(heapIndexDirectory, "roots-by-object.bin");
        var rootEvidencePath = Path.Combine(heapIndexDirectory, "root-evidence.bin");
        var objectLength = new FileInfo(objectPath).Length;
        if (objectLength % ObjectRecordBytes != 0 || objectLength / ObjectRecordBytes > int.MaxValue)
        {
            throw new InvalidDataException("堆索引对象工件长度无效。");
        }

        var objectCount = checked((int)(objectLength / ObjectRecordBytes));
        ValidateCsrFileLengths(objectCount, forwardOffsetPath, forwardTargetPath, "forward");
        ValidateCsrFileLengths(objectCount, reverseOffsetPath, reverseTargetPath, "reverse");
        var budgetBytes = HeapIndexResourcePolicy.GetBudgetBytes();
        var externalSortChunkBytes = SelectExternalSortChunkBytes(
            objectCount,
            budgetBytes,
            HeapIndexResourcePolicy.GetExternalSortChunkBytes());
        var forwardTargetCount = checked((int)(new FileInfo(forwardTargetPath).Length / sizeof(int)));
        var reverseTargetCount = checked((int)(new FileInfo(reverseTargetPath).Length / sizeof(int)));
        var statePath = Path.Combine(temporaryDirectory, "dominator-state.bin");
        var orderInputPath = Path.Combine(temporaryDirectory, "dominator-order.unsorted.bin");
        var orderPath = Path.Combine(temporaryDirectory, "dominator-order.bin");
        var workspacePath = Path.Combine(temporaryDirectory, "dominator-workspace.bin");
        int reachableObjectCount;
        try
        {
            using var objects = new HeapMappedReadWindow(objectPath);
            using var forwardOffsets = new HeapMappedReadWindow(forwardOffsetPath);
            using var forwardTargets = new HeapMappedReadWindow(forwardTargetPath);
            using var reverseOffsets = new HeapMappedReadWindow(reverseOffsetPath);
            using var reverseTargets = new HeapMappedReadWindow(reverseTargetPath);
            using var roots = new RootObjectIdReader(rootPath, rootEvidencePath, objectCount);
            using var workspace = HeapDominatorWorkspace.Create(workspacePath, objectCount, budgetBytes);
            var dfsCount = TraverseFromRoots(
                objectCount,
                forwardTargetCount,
                roots,
                workspace,
                forwardOffsets,
                forwardTargets,
                cancellationToken);
            if (dfsCount == 1)
            {
                WriteEmptyState(statePath, objectCount, cancellationToken);
                using var emptyOrder = new FileStream(orderPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return new HeapDerivedBuildResult(0);
            }

            ComputeImmediateDominators(
                dfsCount,
                objectCount,
                reverseTargetCount,
                workspace,
                reverseOffsets,
                reverseTargets,
                cancellationToken);
            AccumulateRetainedSizes(dfsCount, workspace, objects, cancellationToken);
            WriteStateAndOrderInput(
                statePath,
                orderInputPath,
                objectCount,
                workspace,
                objects,
                cancellationToken);
            reachableObjectCount = dfsCount - 1;
        }
        finally
        {
            HeapTemporaryArtifactCleanup.TryDeleteFile(workspacePath);
        }

        try
        {
            HeapIndexExternalSorter.SortDominatorOrderRecordsAsync(
                    orderInputPath,
                    orderPath,
                    temporaryDirectory,
                    externalSortChunkBytes,
                    cancellationToken)
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            HeapTemporaryArtifactCleanup.TryDeleteFile(orderInputPath);
        }

        return new HeapDerivedBuildResult(reachableObjectCount);
    }

    /// <summary>
    /// 从虚拟根顺序消费非弱根偏移，并沿 DFS parent 链回退；边游标复用节点 scratch 字段，不创建深度栈。
    /// </summary>
    private static int TraverseFromRoots(
        int objectCount,
        int forwardTargetCount,
        RootObjectIdReader roots,
        HeapDominatorWorkspace workspace,
        HeapMappedReadWindow forwardOffsets,
        HeapMappedReadWindow forwardTargets,
        CancellationToken cancellationToken)
    {
        var dfsCount = 1;
        workspace.WriteNode(1, CreateInitialNode(objectCount, parent: 0, dfsNumber: 1, edgeCursor: 0, isRoot: false));
        var current = 1;
        while (current != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = workspace.ReadNode(current);
            if (current == 1)
            {
                var descended = false;
                while (roots.TryReadNext(cancellationToken, out var rootObjectId))
                {
                    var rootDfsNumber = workspace.GetDfsNumber(rootObjectId);
                    if (rootDfsNumber != 0)
                    {
                        var rootState = workspace.ReadNode(rootDfsNumber);
                        if (!rootState.IsRoot)
                        {
                            workspace.WriteIsRoot(rootDfsNumber, isRoot: true);
                        }

                        continue;
                    }

                    rootDfsNumber = ++dfsCount;
                    workspace.SetDfsNumber(rootObjectId, rootDfsNumber);
                    workspace.WriteNode(
                        rootDfsNumber,
                        CreateInitialNode(
                            rootObjectId,
                            parent: 1,
                            rootDfsNumber,
                            ReadCsrOffset(forwardOffsets, rootObjectId, forwardTargetCount),
                            isRoot: true));
                    current = rootDfsNumber;
                    descended = true;
                    break;
                }

                if (!descended)
                {
                    current = 0;
                }

                continue;
            }

            var end = ReadCsrOffset(forwardOffsets, state.ObjectId + 1, forwardTargetCount);
            if (state.Scratch >= end)
            {
                current = state.Parent;
                continue;
            }

            var child = forwardTargets.ReadInt32((long)state.Scratch * sizeof(int));
            workspace.WriteScratch(current, checked(state.Scratch + 1));
            ValidateObjectId(child, objectCount, "forward");
            if (workspace.GetDfsNumber(child) != 0)
            {
                continue;
            }

            var childDfsNumber = ++dfsCount;
            workspace.SetDfsNumber(child, childDfsNumber);
            workspace.WriteNode(
                childDfsNumber,
                CreateInitialNode(
                    child,
                    current,
                    childDfsNumber,
                    ReadCsrOffset(forwardOffsets, child, forwardTargetCount),
                    isRoot: false));
            current = childDfsNumber;
        }

        return dfsCount;
    }

    /// <summary>
    /// 在文件工作区上运行非递归 Lengauer-Tarjan 主循环和直接支配者修正。
    /// </summary>
    private static void ComputeImmediateDominators(
        int dfsCount,
        int objectCount,
        int reverseTargetCount,
        HeapDominatorWorkspace workspace,
        HeapMappedReadWindow reverseOffsets,
        HeapMappedReadWindow reverseTargets,
        CancellationToken cancellationToken)
    {
        for (var current = dfsCount; current >= 2; current--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = workspace.ReadNode(current);
            var start = ReadCsrOffset(reverseOffsets, state.ObjectId, reverseTargetCount);
            var end = ReadCsrOffset(reverseOffsets, state.ObjectId + 1, reverseTargetCount);
            var semi = state.Semi;
            for (var cursor = start; cursor < end; cursor++)
            {
                if (((cursor - start) & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var predecessor = reverseTargets.ReadInt32((long)cursor * sizeof(int));
                ValidateObjectId(predecessor, objectCount, "reverse");
                var predecessorDfsNumber = workspace.GetDfsNumber(predecessor);
                if (predecessorDfsNumber == 0)
                {
                    continue;
                }

                var evaluated = Evaluate(predecessorDfsNumber, workspace, cancellationToken);
                semi = Math.Min(semi, workspace.ReadNode(evaluated).Semi);
            }

            if (state.IsRoot)
            {
                semi = 1;
            }

            if (semi <= 0 || semi >= current)
            {
                throw new InvalidDataException("支配树半支配者状态无效。");
            }

            var semiState = workspace.ReadNode(semi);
            workspace.WriteBucketHead(semi, current);
            workspace.WriteLinkedState(current, semi, state.Parent, semiState.BucketHead);

            var parentState = workspace.ReadNode(state.Parent);
            var bucketIndex = 0;
            for (var candidate = parentState.BucketHead; candidate != 0; bucketIndex++)
            {
                if ((bucketIndex & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var evaluated = Evaluate(candidate, workspace, cancellationToken);
                var candidateState = workspace.ReadNode(candidate);
                var next = candidateState.BucketNext;
                var immediateDominator = workspace.ReadNode(evaluated).Semi < candidateState.Semi
                    ? evaluated
                    : state.Parent;
                workspace.WriteImmediateDominator(candidate, immediateDominator);
                candidate = next;
            }

            workspace.WriteBucketHead(state.Parent, 0);
        }

        for (var current = 2; current <= dfsCount; current++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = workspace.ReadNode(current);
            if (state.ImmediateDominator <= 0 || state.ImmediateDominator >= current)
            {
                throw new InvalidDataException("支配树直接支配者状态无效。");
            }

            if (state.ImmediateDominator != state.Semi)
            {
                var corrected = workspace.ReadNode(state.ImmediateDominator).ImmediateDominator;
                workspace.WriteImmediateDominator(current, corrected);
            }
        }
    }

    /// <summary>
    /// 顺序读取浅表大小并按逆 DFS 顺序累计到直接支配者，保持原有 retained size 语义。
    /// </summary>
    private static void AccumulateRetainedSizes(
        int dfsCount,
        HeapDominatorWorkspace workspace,
        HeapMappedReadWindow objects,
        CancellationToken cancellationToken)
    {
        for (var current = 2; current <= dfsCount; current++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = workspace.ReadNode(current);
            var shallowSize = objects.ReadInt64((long)state.ObjectId * ObjectRecordBytes + sizeof(ulong) + sizeof(int));
            workspace.WriteRetainedSize(current, shallowSize);
        }

        for (var current = dfsCount; current >= 2; current--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = workspace.ReadNode(current);
            var dominator = workspace.ReadNode(state.ImmediateDominator);
            workspace.WriteRetainedSize(
                state.ImmediateDominator,
                checked(dominator.RetainedSizeBytes + state.RetainedSizeBytes));
        }
    }

    /// <summary>
    /// 按 objectId 顺序写最终状态，并为每个可达对象顺序写出外排排序键。
    /// </summary>
    private static void WriteStateAndOrderInput(
        string statePath,
        string orderInputPath,
        int objectCount,
        HeapDominatorWorkspace workspace,
        HeapMappedReadWindow objects,
        CancellationToken cancellationToken)
    {
        using var stateStream = new FileStream(statePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
        using var stateWriter = new BinaryWriter(stateStream);
        using var orderStream = new FileStream(orderInputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
        using var orderWriter = new BinaryWriter(orderStream);
        for (var objectId = 0; objectId < objectCount; objectId++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dfsNumber = workspace.GetDfsNumber(objectId);
            if (dfsNumber == 0)
            {
                stateWriter.Write(UnreachableImmediateDominator);
                stateWriter.Write(0);
                stateWriter.Write(0L);
                continue;
            }

            var state = workspace.ReadNode(dfsNumber);
            var immediateDominatorObjectId = state.ImmediateDominator == 1
                ? VirtualRootImmediateDominator
                : workspace.ReadNode(state.ImmediateDominator).ObjectId;
            stateWriter.Write(immediateDominatorObjectId);
            stateWriter.Write(0);
            stateWriter.Write(state.RetainedSizeBytes);

            var objectOffset = (long)objectId * ObjectRecordBytes;
            orderWriter.Write(state.RetainedSizeBytes);
            orderWriter.Write(objects.ReadInt64(objectOffset + sizeof(ulong) + sizeof(int)));
            orderWriter.Write(objects.ReadUInt64(objectOffset));
            orderWriter.Write(objectId);
        }
    }

    /// <summary>
    /// 创建新发现 DFS 节点的完整初始状态；semi 和 label 均从节点自身编号开始。
    /// </summary>
    private static HeapDominatorNodeState CreateInitialNode(
        int objectId,
        int parent,
        int dfsNumber,
        int edgeCursor,
        bool isRoot) => new(
            objectId,
            parent,
            dfsNumber,
            ImmediateDominator: 0,
            Ancestor: 0,
            dfsNumber,
            BucketHead: 0,
            BucketNext: 0,
            edgeCursor,
            isRoot,
            RetainedSizeBytes: 0);

    /// <summary>
    /// 验证 CSR 偏移和目标工件满足 objectId 编码的固定布局。
    /// </summary>
    private static void ValidateCsrFileLengths(int objectCount, string offsetPath, string targetPath, string name)
    {
        if (new FileInfo(offsetPath).Length != checked(((long)objectCount + 1) * sizeof(long))
            || new FileInfo(targetPath).Length % sizeof(int) != 0
            || new FileInfo(targetPath).Length / sizeof(int) > int.MaxValue)
        {
            throw new InvalidDataException($"{name} CSR 工件长度无效。");
        }
    }

    /// <summary>
    /// 从当前支配树预算选择实际外排块，并在连最小块也无法容纳时于创建工作区前稳定拒绝。
    /// </summary>
    /// <param name="objectCount">基础索引对象数。</param>
    /// <param name="budgetBytes">当前派生构建预算。</param>
    /// <param name="preferredExternalSortChunkBytes">常规资源策略给出的首选排序块大小。</param>
    /// <returns>当前构建实际使用的 16–64 MiB 排序块大小。</returns>
    /// <exception cref="DiagnosticsException">固定需求和最小排序块无法同时落入预算时引发。</exception>
    private static int SelectExternalSortChunkBytes(int objectCount, long budgetBytes, int preferredExternalSortChunkBytes)
    {
        var selectedChunkBytes = HeapIndexResourcePolicy.CalculateDominatorExternalSortChunkBytes(
            objectCount,
            budgetBytes,
            preferredExternalSortChunkBytes);
        if (selectedChunkBytes == 0)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.DerivedAnalysisUnavailable,
                "当前可用资源不足以构建快照支配树派生分析。");
        }

        return selectedChunkBytes;
    }

    /// <summary>
    /// 读取一个 CSR 偏移，并同时验证单调性和目标文件边界。
    /// </summary>
    private static int ReadCsrOffset(HeapMappedReadWindow offsets, int index, int targetCount)
    {
        var offset = offsets.ReadInt64((long)index * sizeof(long));
        if (offset < 0 || offset > targetCount || offset > int.MaxValue)
        {
            throw new InvalidDataException("CSR 偏移超出目标工件范围。");
        }

        if (index > 0)
        {
            var previous = offsets.ReadInt64((long)(index - 1) * sizeof(long));
            if (previous > offset)
            {
                throw new InvalidDataException("CSR 偏移不是单调递增序列。");
            }
        }

        return checked((int)offset);
    }

    /// <summary>
    /// 验证 CSR 或根表中的对象标识仍处于基础对象工件范围内。
    /// </summary>
    private static void ValidateObjectId(int objectId, int objectCount, string source)
    {
        if (objectId < 0 || objectId >= objectCount)
        {
            throw new InvalidDataException($"{source} 工件包含越界对象标识。");
        }
    }

    /// <summary>
    /// 以非递归并查集路径压缩计算 Lengauer-Tarjan 半支配标签；
    /// 访问链通过节点 scratch 字段反向串接，避免深图递归和 O(N) 托管路径数组。
    /// </summary>
    /// <param name="node">要计算半支配标签的 DFS 节点编号。</param>
    /// <param name="workspace">保存并查集和临时反向链的文件工作区。</param>
    /// <param name="cancellationToken">周期取消长路径压缩。</param>
    /// <returns>当前节点的最小半支配标签 DFS 编号。</returns>
    private static int Evaluate(
        int node,
        HeapDominatorWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var nodeState = workspace.ReadNode(node);
        if (nodeState.Ancestor == 0)
        {
            return nodeState.Label;
        }

        var pathHead = 0;
        var pathLength = 0;
        for (var cursor = node; ; pathLength++)
        {
            if ((pathLength & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var state = workspace.ReadNode(cursor);
            if (state.Ancestor == 0 || workspace.ReadNode(state.Ancestor).Ancestor == 0)
            {
                break;
            }

            workspace.WriteScratch(cursor, pathHead);
            pathHead = cursor;
            cursor = state.Ancestor;
        }

        pathLength = 0;
        while (pathHead != 0)
        {
            if ((pathLength++ & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var state = workspace.ReadNode(pathHead);
            var next = state.Scratch;
            var ancestorState = workspace.ReadNode(state.Ancestor);
            var label = state.Label;
            if (workspace.ReadNode(ancestorState.Label).Semi < workspace.ReadNode(state.Label).Semi)
            {
                label = ancestorState.Label;
            }

            workspace.WriteEvaluatedState(pathHead, ancestorState.Ancestor, label, scratch: 0);
            pathHead = next;
        }

        nodeState = workspace.ReadNode(node);
        var parentState = workspace.ReadNode(nodeState.Ancestor);
        return workspace.ReadNode(parentState.Label).Semi >= workspace.ReadNode(nodeState.Label).Semi
            ? nodeState.Label
            : parentState.Label;
    }

    /// <summary>
    /// 将没有根可达对象的状态表写入完整哨兵记录，保证后续工件验证和随机读取保持固定布局。
    /// </summary>
    private static void WriteEmptyState(string path, int objectCount, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
        using var writer = new BinaryWriter(stream);
        for (var objectId = 0; objectId < objectCount; objectId++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Write(UnreachableImmediateDominator);
            writer.Write(0);
            writer.Write(0L);
        }
    }

    /// <summary>
    /// 为新工件写入含长度与 SHA-256 的完成清单；清单只在两个数据文件完全落盘后创建。
    /// </summary>
    private static void WriteManifest(string directory, HeapDerivedBuildResult result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = new[]
        {
            CreateManifestFile(directory, "dominator-state.bin"),
            CreateManifestFile(directory, "dominator-order.bin")
        };
        var manifest = new HeapDerivedManifest(FormatVersion, true, result.ReachableObjectCount, files);
        using var stream = new FileStream(Path.Combine(directory, "manifest.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
        JsonSerializer.Serialize(stream, manifest);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// 计算并返回单个派生数据文件的验证条目。
    /// </summary>
    private static HeapDerivedManifestFile CreateManifestFile(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        return new HeapDerivedManifestFile(name, new FileInfo(path).Length, ComputeHash(path));
    }

    /// <summary>
    /// 计算工件 SHA-256；调用方保证文件在当前临时目录中已关闭写入句柄。
    /// </summary>
    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// 顺序扫描每对象根证据偏移，仅返回拥有非弱根证据的 objectId；
    /// 生命周期覆盖一次 DFS，因而不创建根对象列表或重复扫描根工件。
    /// </summary>
    private sealed class RootObjectIdReader : IDisposable
    {
        private const int MaximumEvidenceStringBytes = 1_048_576;
        private readonly BinaryReader _reader;
        private readonly BinaryReader _evidence;
        private readonly int _objectCount;
        private int _nextObjectId;
        private long _previousOffset;

        /// <summary>
        /// 打开固定长度的根偏移工件并读取初始偏移。
        /// </summary>
        /// <param name="path">基础索引中的 roots-by-object 工件。</param>
        /// <param name="evidencePath">基础索引中的 root-evidence 工件。</param>
        /// <param name="objectCount">基础对象数。</param>
        public RootObjectIdReader(string path, string evidencePath, int objectCount)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentException.ThrowIfNullOrWhiteSpace(evidencePath);
            ArgumentOutOfRangeException.ThrowIfNegative(objectCount);
            var stream = File.OpenRead(path);
            FileStream? evidenceStream = null;
            try
            {
                evidenceStream = File.OpenRead(evidencePath);
                if (stream.Length != checked(((long)objectCount + 1) * sizeof(long)))
                {
                    throw new InvalidDataException("GC 根工件长度无效。");
                }

                _reader = new BinaryReader(stream);
                _evidence = new BinaryReader(evidenceStream);
                _previousOffset = _reader.ReadInt64();
                if (_previousOffset != 0)
                {
                    throw new InvalidDataException("GC 根工件初始偏移必须为零。");
                }

                _objectCount = objectCount;
            }
            catch
            {
                stream.Dispose();
                evidenceStream?.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 继续扫描到下一条非空根证据区间，并在长无根区间中传播构建取消。
        /// </summary>
        /// <param name="cancellationToken">取消当前派生构建的令牌。</param>
        /// <param name="objectId">找到的根对象标识；返回 false 时未定义。</param>
        /// <returns>找到下一根对象时返回 <see langword="true"/>。</returns>
        public bool TryReadNext(CancellationToken cancellationToken, out int objectId)
        {
            while (_nextObjectId < _objectCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentObjectId = _nextObjectId++;
                var nextOffset = _reader.ReadInt64();
                if (nextOffset < _previousOffset)
                {
                    throw new InvalidDataException("GC 根工件偏移不是单调递增序列。");
                }

                if (nextOffset > _evidence.BaseStream.Length)
                {
                    throw new InvalidDataException("GC 根工件偏移越过证据文件末尾。");
                }

                var hasEvidence = ContainsNonWeakEvidence(_previousOffset, nextOffset);
                _previousOffset = nextOffset;
                if (hasEvidence)
                {
                    objectId = currentObjectId;
                    return true;
                }
            }

            objectId = default;
            return false;
        }

        /// <summary>
        /// 顺序解析单个对象的全部根记录；只有至少一条非弱根才能参与支配树虚拟根连接。
        /// </summary>
        /// <param name="start">当前对象证据起始偏移。</param>
        /// <param name="end">当前对象证据结束偏移。</param>
        /// <returns>证据区间中存在非弱根时返回 true。</returns>
        private bool ContainsNonWeakEvidence(long start, long end)
        {
            var containsNonWeak = false;
            _evidence.BaseStream.Position = start;
            while (_evidence.BaseStream.Position < end)
            {
                if (end - _evidence.BaseStream.Position < sizeof(byte) + sizeof(int))
                {
                    throw new InvalidDataException("GC 根证据记录截断。");
                }

                var kind = (MemoryRootKind)_evidence.ReadByte();
                if (!Enum.IsDefined(kind))
                {
                    throw new InvalidDataException("GC 根证据类别无效。");
                }

                var flags = (MemoryRootFlags)_evidence.ReadInt32();
                SkipNullableString(end);
                SkipNullableString(end);
                containsNonWeak |= !flags.HasFlag(MemoryRootFlags.WeakReference);
            }

            if (_evidence.BaseStream.Position != end)
            {
                throw new InvalidDataException("GC 根证据记录越过对象偏移范围。");
            }

            return containsNonWeak;
        }

        /// <summary>
        /// 跳过一段受当前对象证据边界限制的可空 UTF-8 字符串。
        /// </summary>
        /// <param name="end">当前对象证据结束偏移。</param>
        private void SkipNullableString(long end)
        {
            if (end - _evidence.BaseStream.Position < sizeof(int))
            {
                throw new InvalidDataException("GC 根证据字符串长度截断。");
            }

            var length = _evidence.ReadInt32();
            if (length == -1)
            {
                return;
            }

            if (length < 0
                || length > MaximumEvidenceStringBytes
                || length > end - _evidence.BaseStream.Position)
            {
                throw new InvalidDataException("GC 根证据字符串长度无效。");
            }

            _evidence.BaseStream.Position += length;
        }

        /// <summary>
        /// 关闭根偏移顺序读取器，使派生临时目录和基础索引可立即移动或清理。
        /// </summary>
        public void Dispose()
        {
            _reader.Dispose();
            _evidence.Dispose();
        }
    }

    /// <summary>
    /// 表示构建阶段输出的可达对象数量。
    /// </summary>
    private readonly record struct HeapDerivedBuildResult(int ReachableObjectCount);

    /// <summary>
    /// 表示派生工件恢复校验当前读取到的确定排序键。
    /// </summary>
    private readonly record struct DominatorSortKey(
        long RetainedSizeBytes,
        long ShallowSizeBytes,
        ulong Address,
        int ObjectId);

    /// <summary>
    /// 表示派生目录完成清单。
    /// </summary>
    private sealed record HeapDerivedManifest(
        int Version,
        bool Completed,
        int ReachableObjectCount,
        IReadOnlyList<HeapDerivedManifestFile?>? Files);

    /// <summary>
    /// 表示派生数据文件的固定验证信息。
    /// </summary>
    private sealed record HeapDerivedManifestFile(string? Name, long Length, string? Sha256);
}

/// <summary>
/// 表示从支配树派生工件读取的单个固定宽度记录。
/// </summary>
internal readonly record struct HeapDerivedDominatorEntry(
    int ObjectId,
    int ImmediateDominatorObjectId,
    long RetainedSizeBytes);
