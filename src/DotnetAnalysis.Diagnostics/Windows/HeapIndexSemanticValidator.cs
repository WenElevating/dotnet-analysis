using System.Text;
using System.Text.Json;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 对已通过长度和摘要检查的 v3 堆索引执行跨工件语义校验，避免自洽哈希掩盖不可查询的数据关系。
/// </summary>
internal static class HeapIndexSemanticValidator
{
    private const int MaximumTypeCount = 1_000_000;
    private const int MaximumObjectCount = 100_000_000;
    private const long MaximumEdgeCount = 500_000_000;
    private const int MaximumStringBytes = 1_048_576;
    private const int ObjectRecordBytes = sizeof(ulong) + sizeof(int) + sizeof(long);
    private const int AddressRecordBytes = sizeof(ulong) + sizeof(int);
    private static readonly UTF8Encoding s_strictUtf8 = new(false, true);
    private const MemoryRootFlags AllRootFlags = MemoryRootFlags.StackRoot
        | MemoryRootFlags.Interior
        | MemoryRootFlags.Pinned
        | MemoryRootFlags.WeakReference
        | MemoryRootFlags.RefCounted;

    /// <summary>
    /// 在线程池上校验完整工件族；读取使用顺序流和受限映射窗口，内存只随类型数量增长。
    /// </summary>
    /// <param name="directory">已通过 v3 manifest 长度与摘要校验的目录。</param>
    /// <param name="cancellationToken">取消当前热打开校验。</param>
    /// <returns>全部语义约束均成立时完成的任务。</returns>
    public static Task ValidateAsync(string directory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return Task.Run(() => Validate(directory, cancellationToken), CancellationToken.None);
    }

    /// <summary>
    /// 按类型、对象、查找表、CSR 和根证据的依赖顺序执行一次完整校验。
    /// </summary>
    private static void Validate(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var types = ReadTypes(directory, cancellationToken);
        var summaries = ReadTypeSummaries(directory, types.Length, cancellationToken);
        var (objectCount, objectCountsByType, objectSizesByType) = ValidateObjects(directory, types.Length, cancellationToken);
        ValidateTypeSummaries(types, summaries, objectCount, objectCountsByType, objectSizesByType);
        ValidateAddressTable(directory, objectCount, cancellationToken);
        ValidateObjectsByType(directory, objectCount, objectCountsByType, cancellationToken);
        var forwardEdgeCount = ValidateCsr(directory, "forward", objectCount, cancellationToken);
        var reverseEdgeCount = ValidateCsr(directory, "reverse", objectCount, cancellationToken);
        if (forwardEdgeCount != reverseEdgeCount)
        {
            throw new InvalidDataException("正向与反向 CSR 边数不一致。");
        }

        ValidateCsrTranspose(directory, objectCount, forwardEdgeCount, cancellationToken);
        ValidateRoots(directory, objectCount, cancellationToken);
    }

    /// <summary>
    /// 读取有界类型表并拒绝空名称、重复身份和未声明尾部数据。
    /// </summary>
    private static TypeIdentity[] ReadTypes(string directory, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(Path.Combine(directory, "types.bin"));
        using var reader = new BinaryReader(stream, s_strictUtf8, leaveOpen: true);
        if (stream.Length < sizeof(int))
        {
            throw new InvalidDataException("类型工件截断。");
        }

        var count = reader.ReadInt32();
        if (count < 0 || count > MaximumTypeCount)
        {
            throw new InvalidDataException("类型数量超出支持范围。");
        }

        var types = new TypeIdentity[count];
        var uniqueTypes = new HashSet<TypeIdentity>();
        for (var index = 0; index < count; index++)
        {
            CheckCancellation(index, cancellationToken);
            var name = ReadBoundedString(reader);
            var assembly = ReadBoundedString(reader);
            var type = new TypeIdentity(name, string.IsNullOrEmpty(assembly) ? null : assembly);
            if (!uniqueTypes.Add(type))
            {
                throw new InvalidDataException("类型工件包含重复类型身份。");
            }

            types[index] = type;
        }

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("类型工件包含未声明尾部数据。");
        }

        return types;
    }

    /// <summary>
    /// 读取受类型数量约束的统计元数据，并拒绝重复或缺失的类型身份。
    /// </summary>
    private static Dictionary<TypeIdentity, MemoryTypeSummary> ReadTypeSummaries(
        string directory,
        int typeCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(Path.Combine(directory, "type-summary.bin"));
        var summaries = JsonSerializer.Deserialize<MemoryTypeSummary[]>(stream)
            ?? throw new InvalidDataException("类型统计工件为空。");
        if (summaries.Length != typeCount || summaries.Any(static summary => summary is null))
        {
            throw new InvalidDataException("类型统计数量与类型工件不一致。");
        }

        try
        {
            return summaries.ToDictionary(static summary => summary.Type);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("类型统计包含重复类型身份。", exception);
        }
    }

    /// <summary>
    /// 顺序验证对象地址、类型索引和大小，并累积按类型统计供跨文件比对。
    /// </summary>
    private static (int ObjectCount, long[] CountsByType, long[] SizesByType) ValidateObjects(
        string directory,
        int typeCount,
        CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(Path.Combine(directory, "objects.bin"));
        if (stream.Length % ObjectRecordBytes != 0 || stream.Length / ObjectRecordBytes > MaximumObjectCount)
        {
            throw new InvalidDataException("对象工件长度无效。");
        }

        var objectCount = checked((int)(stream.Length / ObjectRecordBytes));
        var countsByType = new long[typeCount];
        var sizesByType = new long[typeCount];
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        for (var objectId = 0; objectId < objectCount; objectId++)
        {
            CheckCancellation(objectId, cancellationToken);
            var address = reader.ReadUInt64();
            var typeIndex = reader.ReadInt32();
            var sizeBytes = reader.ReadInt64();
            if (address == 0 || (uint)typeIndex >= (uint)typeCount || sizeBytes < 0)
            {
                throw new InvalidDataException("对象工件包含无效地址、类型索引或大小。");
            }

            countsByType[typeIndex]++;
            sizesByType[typeIndex] = checked(sizesByType[typeIndex] + sizeBytes);
        }

        return (objectCount, countsByType, sizesByType);
    }

    /// <summary>
    /// 将对象表累积值与每个类型的声明统计精确比对，并确保统计集合与类型表相同。
    /// </summary>
    private static void ValidateTypeSummaries(
        TypeIdentity[] types,
        Dictionary<TypeIdentity, MemoryTypeSummary> summaries,
        int objectCount,
        long[] countsByType,
        long[] sizesByType)
    {
        long declaredObjectCount = 0;
        for (var typeIndex = 0; typeIndex < types.Length; typeIndex++)
        {
            if (!summaries.TryGetValue(types[typeIndex], out var summary)
                || summary.ObjectCount != countsByType[typeIndex]
                || summary.TotalSizeBytes != sizesByType[typeIndex])
            {
                throw new InvalidDataException("类型统计与对象工件不一致。");
            }

            declaredObjectCount = checked(declaredObjectCount + summary.ObjectCount);
        }

        if (declaredObjectCount != objectCount)
        {
            throw new InvalidDataException("类型统计对象总数与对象工件不一致。");
        }
    }

    /// <summary>
    /// 验证地址表严格递增且每条地址与其 objectId 指向的对象记录完全一致。
    /// </summary>
    private static void ValidateAddressTable(string directory, int objectCount, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(Path.Combine(directory, "address-to-id.bin"));
        if (stream.Length != checked((long)objectCount * AddressRecordBytes))
        {
            throw new InvalidDataException("地址索引长度与对象数不一致。");
        }

        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        using var objects = new HeapMappedReadWindow(Path.Combine(directory, "objects.bin"));
        ulong previousAddress = 0;
        for (var index = 0; index < objectCount; index++)
        {
            CheckCancellation(index, cancellationToken);
            var address = reader.ReadUInt64();
            var objectId = reader.ReadInt32();
            if (address == 0
                || address <= previousAddress
                || (uint)objectId >= (uint)objectCount
                || objects.ReadUInt64((long)objectId * ObjectRecordBytes) != address)
            {
                throw new InvalidDataException("地址索引未形成对象表的严格递增双射。");
            }

            previousAddress = address;
        }
    }

    /// <summary>
    /// 验证按类型对象表覆盖每个对象一次，并在每个类型区间内按 objectId 严格递增。
    /// </summary>
    private static void ValidateObjectsByType(
        string directory,
        int objectCount,
        long[] objectCountsByType,
        CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(Path.Combine(directory, "objects-by-type.bin"));
        if (stream.Length != checked((long)objectCount * sizeof(int)))
        {
            throw new InvalidDataException("按类型对象索引长度与对象数不一致。");
        }

        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        using var objects = new HeapMappedReadWindow(Path.Combine(directory, "objects.bin"));
        long visited = 0;
        for (var typeIndex = 0; typeIndex < objectCountsByType.Length; typeIndex++)
        {
            var previousObjectId = -1;
            for (long index = 0; index < objectCountsByType[typeIndex]; index++)
            {
                CheckCancellation(visited++, cancellationToken);
                var objectId = reader.ReadInt32();
                if ((uint)objectId >= (uint)objectCount
                    || objectId <= previousObjectId
                    || objects.ReadInt32(checked((long)objectId * ObjectRecordBytes + sizeof(ulong))) != typeIndex)
                {
                    throw new InvalidDataException("按类型对象索引的范围、顺序或对象类型无效。");
                }

                previousObjectId = objectId;
            }
        }

        if (visited != objectCount || stream.Position != stream.Length)
        {
            throw new InvalidDataException("按类型对象索引未完整覆盖对象表。");
        }
    }

    /// <summary>
    /// 验证一个正向或反向 CSR 的偏移单调性、终点、目标范围及每行严格排序。
    /// </summary>
    private static long ValidateCsr(
        string directory,
        string prefix,
        int objectCount,
        CancellationToken cancellationToken)
    {
        using var offsetsStream = File.OpenRead(Path.Combine(directory, $"{prefix}-offsets.bin"));
        using var targetsStream = File.OpenRead(Path.Combine(directory, $"{prefix}-targets.bin"));
        if (offsetsStream.Length != checked(((long)objectCount + 1) * sizeof(long))
            || targetsStream.Length % sizeof(int) != 0
            || targetsStream.Length / sizeof(int) > MaximumEdgeCount)
        {
            throw new InvalidDataException($"{prefix} CSR 工件长度无效。");
        }

        var targetCount = targetsStream.Length / sizeof(int);
        using var offsets = new BinaryReader(offsetsStream, Encoding.UTF8, leaveOpen: true);
        using var targets = new BinaryReader(targetsStream, Encoding.UTF8, leaveOpen: true);
        var start = offsets.ReadInt64();
        if (start != 0)
        {
            throw new InvalidDataException($"{prefix} CSR 起始偏移必须为零。");
        }

        long visitedEdges = 0;
        for (var objectId = 0; objectId < objectCount; objectId++)
        {
            CheckCancellation(objectId, cancellationToken);
            var end = offsets.ReadInt64();
            if (end < start || end > targetCount)
            {
                throw new InvalidDataException($"{prefix} CSR 偏移不单调或超出目标工件。");
            }

            var previousTarget = -1;
            while (visitedEdges < end)
            {
                CheckCancellation(visitedEdges, cancellationToken);
                var target = targets.ReadInt32();
                if ((uint)target >= (uint)objectCount || target <= previousTarget)
                {
                    throw new InvalidDataException($"{prefix} CSR 目标越界、重复或顺序无效。");
                }

                previousTarget = target;
                visitedEdges++;
            }

            start = end;
        }

        if (start != targetCount || visitedEdges != targetCount || targetsStream.Position != targetsStream.Length)
        {
            throw new InvalidDataException($"{prefix} CSR 终止偏移与目标数不一致。");
        }

        return targetCount;
    }

    /// <summary>
    /// 对每条正向边在反向 CSR 的有序父区间内做二分查找，精确验证两个方向互为转置。
    /// </summary>
    private static void ValidateCsrTranspose(
        string directory,
        int objectCount,
        long edgeCount,
        CancellationToken cancellationToken)
    {
        using var forwardOffsets = new HeapMappedReadWindow(Path.Combine(directory, "forward-offsets.bin"));
        using var forwardTargets = new HeapMappedReadWindow(Path.Combine(directory, "forward-targets.bin"));
        using var reverseOffsets = new HeapMappedReadWindow(Path.Combine(directory, "reverse-offsets.bin"));
        using var reverseTargets = new HeapMappedReadWindow(Path.Combine(directory, "reverse-targets.bin"));
        long visitedEdges = 0;
        for (var source = 0; source < objectCount; source++)
        {
            CheckCancellation(source, cancellationToken);
            var start = forwardOffsets.ReadInt64((long)source * sizeof(long));
            var end = forwardOffsets.ReadInt64((long)(source + 1) * sizeof(long));
            for (var edgeIndex = start; edgeIndex < end; edgeIndex++)
            {
                CheckCancellation(visitedEdges++, cancellationToken);
                var target = forwardTargets.ReadInt32(edgeIndex * sizeof(int));
                var reverseStart = reverseOffsets.ReadInt64((long)target * sizeof(long));
                var reverseEnd = reverseOffsets.ReadInt64((long)(target + 1) * sizeof(long));
                if (!ContainsSortedTarget(reverseTargets, reverseStart, reverseEnd, source))
                {
                    throw new InvalidDataException("正向与反向 CSR 不互为转置。");
                }
            }
        }

        if (visitedEdges != edgeCount)
        {
            throw new InvalidDataException("正向 CSR 实际遍历边数与声明不一致。");
        }
    }

    /// <summary>
    /// 在一个已验证有序的 CSR 目标区间内二分查找指定对象标识。
    /// </summary>
    private static bool ContainsSortedTarget(HeapMappedReadWindow targets, long start, long end, int expected)
    {
        var low = start;
        var high = end - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var candidate = targets.ReadInt32(middle * sizeof(int));
            if (candidate == expected)
            {
                return true;
            }

            if (candidate < expected)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return false;
    }

    /// <summary>
    /// 验证根偏移覆盖整个证据流，并逐条检查类别、标志、字符串边界和模型语义。
    /// </summary>
    private static void ValidateRoots(string directory, int objectCount, CancellationToken cancellationToken)
    {
        using var offsetsStream = File.OpenRead(Path.Combine(directory, "roots-by-object.bin"));
        using var evidenceStream = File.OpenRead(Path.Combine(directory, "root-evidence.bin"));
        if (offsetsStream.Length != checked(((long)objectCount + 1) * sizeof(long)))
        {
            throw new InvalidDataException("根偏移工件长度与对象数不一致。");
        }

        using var offsets = new BinaryReader(offsetsStream, Encoding.UTF8, leaveOpen: true);
        using var evidence = new BinaryReader(evidenceStream, s_strictUtf8, leaveOpen: true);
        var start = offsets.ReadInt64();
        if (start != 0)
        {
            throw new InvalidDataException("根证据起始偏移必须为零。");
        }

        for (var objectId = 0; objectId < objectCount; objectId++)
        {
            CheckCancellation(objectId, cancellationToken);
            var end = offsets.ReadInt64();
            if (end < start || end > evidenceStream.Length || evidenceStream.Position != start)
            {
                throw new InvalidDataException("根证据偏移不单调或超出证据工件。");
            }

            var previousPriority = -1;
            while (evidenceStream.Position < end)
            {
                var root = ReadRootEvidence(evidence, end);
                var priority = GetRootPriority(root);
                if (priority < previousPriority)
                {
                    throw new InvalidDataException("同一对象的根证据顺序无效。");
                }

                previousPriority = priority;
            }

            if (evidenceStream.Position != end)
            {
                throw new InvalidDataException("根证据记录越过声明区间。");
            }

            start = end;
        }

        if (start != evidenceStream.Length)
        {
            throw new InvalidDataException("根偏移终点与证据长度不一致。");
        }
    }

    /// <summary>
    /// 从当前根证据区间读取并构造一条领域根记录，以复用其函数与模块约束。
    /// </summary>
    private static MemoryRetentionRoot ReadRootEvidence(BinaryReader reader, long end)
    {
        if (end - reader.BaseStream.Position < sizeof(byte) + sizeof(int))
        {
            throw new InvalidDataException("根证据记录截断。");
        }

        var kindValue = reader.ReadByte();
        if (!Enum.IsDefined((MemoryRootKind)kindValue))
        {
            throw new InvalidDataException("根证据类别无效。");
        }

        var flags = (MemoryRootFlags)reader.ReadInt32();
        if ((flags & ~AllRootFlags) != 0)
        {
            throw new InvalidDataException("根证据包含未知标志。");
        }

        var functionName = ReadNullableString(reader, end);
        var moduleName = ReadNullableString(reader, end);
        return new MemoryRetentionRoot((MemoryRootKind)kindValue, flags, functionName, moduleName);
    }

    /// <summary>
    /// 读取根证据中的定长前缀 UTF-8 可空字符串，并限制其不得越过当前对象区间。
    /// </summary>
    private static string? ReadNullableString(BinaryReader reader, long end)
    {
        if (end - reader.BaseStream.Position < sizeof(int))
        {
            throw new InvalidDataException("根证据字符串长度截断。");
        }

        var length = reader.ReadInt32();
        if (length == -1)
        {
            return null;
        }

        if (length < 0 || length > MaximumStringBytes || length > end - reader.BaseStream.Position)
        {
            throw new InvalidDataException("根证据字符串长度无效。");
        }

        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new InvalidDataException("根证据字符串截断。");
        }

        return s_strictUtf8.GetString(bytes);
    }

    /// <summary>
    /// 读取 BinaryWriter 字符串格式的有界 UTF-8 值，拒绝超长声明和截断数据。
    /// </summary>
    private static string ReadBoundedString(BinaryReader reader)
    {
        var length = Read7BitEncodedInt(reader);
        if (length < 0 || length > MaximumStringBytes || length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException("类型字符串长度无效。");
        }

        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new InvalidDataException("类型字符串截断。");
        }

        return s_strictUtf8.GetString(bytes);
    }

    /// <summary>
    /// 读取最多五字节的非负 32 位七位编码整数，拒绝溢出或未终止前缀。
    /// </summary>
    private static int Read7BitEncodedInt(BinaryReader reader)
    {
        uint result = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var value = reader.ReadByte();
            if (shift == 28 && (value & 0xF0) != 0)
            {
                throw new InvalidDataException("类型字符串长度前缀溢出。");
            }

            result |= (uint)(value & 0x7F) << shift;
            if ((value & 0x80) == 0)
            {
                return checked((int)result);
            }
        }

        throw new InvalidDataException("类型字符串长度前缀未终止。");
    }

    /// <summary>
    /// 按固定粒度观察取消，避免对每条大图记录执行昂贵的令牌检查。
    /// </summary>
    private static void CheckCancellation(long index, CancellationToken cancellationToken)
    {
        if ((index & 0xFFF) == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// 返回与工件写入端一致的根证据稳定优先级。
    /// </summary>
    private static int GetRootPriority(MemoryRetentionRoot root) => root.Kind switch
    {
        MemoryRootKind.Stack when root.FunctionName is not null => 0,
        MemoryRootKind.Stack => 1,
        MemoryRootKind.Handle => 2,
        MemoryRootKind.Finalizer => 3,
        MemoryRootKind.Other => 4,
        _ => 5
    };
}
