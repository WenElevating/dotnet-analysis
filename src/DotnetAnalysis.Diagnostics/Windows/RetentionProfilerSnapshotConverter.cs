using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 将原生 Profiler 已冻结的共享内存记录转换为可持久化的保留分析对象图。
/// </summary>
/// <remarks>
/// 转换只接受 CLR 回调给出的根类别、标志和 FunctionID 证据。对象大小使用 CLR GetObjectSize 的原始结果；
/// 无法读取大小时以零表示未知。类型名称尚未解析时使用明确含 ClassID 的未知类型占位，绝不把它当作 CLR 验证名称。
/// </remarks>
internal static class RetentionProfilerSnapshotConverter
{
    private const uint StackRootKind = 1;
    private const uint FinalizerRootKind = 2;
    private const uint HandleRootKind = 3;
    private const uint OtherRootKind = 0;
    private const uint PinnedFlag = 0x1;
    private const uint WeakReferenceFlag = 0x2;
    private const uint InteriorFlag = 0x4;
    private const uint RefCountedFlag = 0x8;

    /// <summary>
    /// 转换一次完成的 Profiler 捕获；重复对象和重复边会被去重，协议外根类别不会获得函数证据。
    /// </summary>
    /// <param name="rawCapture">已由共享内存完成事件冻结并复制到诊断进程的原始记录。</param>
    /// <returns>可写入专用保留快照格式的类型、对象、边和根集合。</returns>
    public static RetentionHeapSnapshot.SnapshotData Convert(RetentionProfilerRawCapture rawCapture)
    {
        ArgumentNullException.ThrowIfNull(rawCapture);
        var verifiedTypesByClassId = rawCapture.Types
            .Where(type => type.ClassId != 0 && !string.IsNullOrWhiteSpace(type.TypeName))
            .GroupBy(type => type.ClassId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(type => type.TypeName, StringComparer.Ordinal).First());
        var typeIndexes = new Dictionary<nuint, int>();
        var types = new List<TypeIdentity>();
        var objects = new List<RetentionHeapSnapshot.ObjectRecord>(rawCapture.Objects.Count);
        var objectAddresses = new HashSet<ulong>();
        foreach (var rawObject in rawCapture.Objects)
        {
            var address = (ulong)rawObject.ObjectId;
            if (address == 0 || !objectAddresses.Add(address))
            {
                continue;
            }

            if (!typeIndexes.TryGetValue(rawObject.ClassId, out var typeIndex))
            {
                typeIndex = types.Count;
                typeIndexes.Add(rawObject.ClassId, typeIndex);
                types.Add(CreateTypeIdentity(rawObject.ClassId, verifiedTypesByClassId));
            }

            var sizeBytes = rawObject.SizeBytes <= (nuint)uint.MaxValue
                ? (long)rawObject.SizeBytes
                : 0;
            objects.Add(new RetentionHeapSnapshot.ObjectRecord(address, typeIndex, sizeBytes));
        }

        var edges = new List<RetentionHeapSnapshot.EdgeRecord>(rawCapture.Edges.Count);
        var edgeSet = new HashSet<RetentionHeapSnapshot.EdgeRecord>();
        foreach (var rawEdge in rawCapture.Edges)
        {
            var edge = new RetentionHeapSnapshot.EdgeRecord(
                (ulong)rawEdge.SourceObjectId,
                (ulong)rawEdge.TargetObjectId);
            if (edge.SourceAddress != 0 && edge.TargetAddress != 0 && edgeSet.Add(edge))
            {
                edges.Add(edge);
            }
        }

        var roots = new List<RetentionHeapSnapshot.RootRecord>(rawCapture.Roots.Count);
        foreach (var rawRoot in rawCapture.Roots)
        {
            var address = (ulong)rawRoot.ObjectId;
            if (address == 0)
            {
                continue;
            }

            roots.Add(new RetentionHeapSnapshot.RootRecord(address, CreateRoot(rawRoot, rawCapture.Functions)));
        }

        return new RetentionHeapSnapshot.SnapshotData(types, objects, edges, roots);
    }

    /// <summary>
    /// 优先使用 CLR Metadata API 已验证的类型名；缺少证据时显式保留 ClassID，避免错误宣称类型名称。
    /// </summary>
    internal static TypeIdentity CreateTypeIdentity(
        nuint classId,
        IReadOnlyDictionary<nuint, RetentionProfilerRawType> verifiedTypesByClassId)
    {
        if (verifiedTypesByClassId.TryGetValue(classId, out var type))
        {
            return new TypeIdentity(
                type.TypeName!,
                string.IsNullOrWhiteSpace(type.ModuleName) ? null : type.ModuleName);
        }

        return new TypeIdentity($"未知类型 (ClassID 0x{(ulong)classId:X})", null);
    }

    /// <summary>
    /// 将 CLR 原生根类别映射为公开模型；未知数值保留为 Unknown 而非猜测为 Other。
    /// </summary>
    /// <summary>
    /// 使用 CLR 回调的根类别、标志和已验证 FunctionID 证据创建公开根模型；
    /// 非栈根或证据不匹配时绝不返回函数名称。
    /// </summary>
    /// <param name="rawRoot">原始 CLR 根记录。</param>
    /// <param name="functions">按原始证据索引排列的函数记录。</param>
    /// <returns>包含可信根类别和可选函数证据的根模型。</returns>
    internal static MemoryRetentionRoot CreateRoot(
        RetentionProfilerRawRoot rawRoot,
        IReadOnlyList<RetentionProfilerRawFunction> functions)
    {
        var kind = MapRootKind(rawRoot.RootKind);
        return new MemoryRetentionRoot(
            kind,
            MapRootFlags(kind, rawRoot.RootFlags),
            ResolveFunctionName(kind, rawRoot, functions, out var moduleName),
            moduleName);
    }

    /// <summary>
    /// 将 CLR 原生根类别映射为公开模型；未知数值保留为 Unknown 而非猜测为 Other。
    /// </summary>
    private static MemoryRootKind MapRootKind(uint rawKind) => rawKind switch
    {
        StackRootKind => MemoryRootKind.Stack,
        FinalizerRootKind => MemoryRootKind.Finalizer,
        HandleRootKind => MemoryRootKind.Handle,
        OtherRootKind => MemoryRootKind.Other,
        _ => MemoryRootKind.Unknown
    };

    /// <summary>
    /// 将 CLR 原生根标志映射为公开位标记，并为栈根补充由根类别直接证明的 StackRoot 标记。
    /// </summary>
    private static MemoryRootFlags MapRootFlags(MemoryRootKind kind, uint rawFlags)
    {
        var flags = kind is MemoryRootKind.Stack ? MemoryRootFlags.StackRoot : MemoryRootFlags.None;
        if ((rawFlags & PinnedFlag) != 0)
        {
            flags |= MemoryRootFlags.Pinned;
        }
        if ((rawFlags & WeakReferenceFlag) != 0)
        {
            flags |= MemoryRootFlags.WeakReference;
        }
        if ((rawFlags & RefCountedFlag) != 0)
        {
            flags |= MemoryRootFlags.RefCounted;
        }
        if ((rawFlags & InteriorFlag) != 0)
        {
            flags |= MemoryRootFlags.Interior;
        }

        return flags;
    }

    /// <summary>
    /// 仅对栈根使用与 FunctionID 一致、名称非空的证据记录；非栈根和不一致记录一律不返回名称。
    /// </summary>
    private static string? ResolveFunctionName(
        MemoryRootKind kind,
        RetentionProfilerRawRoot rawRoot,
        IReadOnlyList<RetentionProfilerRawFunction> functions,
        out string? moduleName)
    {
        moduleName = null;
        if (kind is not MemoryRootKind.Stack
            || rawRoot.FunctionEvidenceIndex == uint.MaxValue
            || rawRoot.FunctionEvidenceIndex >= functions.Count)
        {
            return null;
        }

        var evidence = functions[checked((int)rawRoot.FunctionEvidenceIndex)];
        if (evidence.FunctionId != rawRoot.RootId || string.IsNullOrWhiteSpace(evidence.FunctionName))
        {
            return null;
        }

        moduleName = string.IsNullOrWhiteSpace(evidence.ModuleName) ? null : evidence.ModuleName;
        return evidence.FunctionName;
    }
}
