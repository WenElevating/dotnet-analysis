namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 为大快照索引构建提供固定宽度地址到对象标识记录的外部排序边界。
/// </summary>
internal static class HeapIndexExternalSorter
{
    private const int AddressIdRecordBytes = sizeof(ulong) + sizeof(int);
    private const int AddressPairRecordBytes = sizeof(ulong) + sizeof(ulong);
    private const int ObjectIdEdgeRecordBytes = sizeof(int) + sizeof(int);
    private const int ProfilerObjectRecordBytes = sizeof(ulong) + sizeof(ulong) + sizeof(ulong);
    private const int DominatorOrderRecordBytes = sizeof(long) + sizeof(long) + sizeof(ulong) + sizeof(int);
    private const int DominatorOrderManagedRecordBytes = 32;

    /// <summary>
    /// 按 retained size 降序、浅表大小降序、对象地址升序对支配对象固定记录执行有界外排，
    /// 输出仅包含分页工件需要的 objectId 序列。
    /// </summary>
    /// <param name="inputPath">未排序的 retained、shallow、address、objectId 固定宽度记录。</param>
    /// <param name="outputPath">不存在的 objectId 排序结果文件。</param>
    /// <param name="workingDirectory">仅供本次排序创建块目录的工作目录。</param>
    /// <param name="maximumChunkBytes">单个托管排序块的最大近似字节数。</param>
    /// <param name="cancellationToken">取消当前派生构建的令牌。</param>
    /// <returns>代表完成拆块、归并和临时块清理的任务。</returns>
    public static Task SortDominatorOrderRecordsAsync(
        string inputPath,
        string outputPath,
        string workingDirectory,
        int maximumChunkBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumChunkBytes, DominatorOrderManagedRecordBytes);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("支配对象外排排序输入文件不存在。", inputPath);
        }

        if (File.Exists(outputPath))
        {
            throw new IOException("支配对象外排排序输出文件必须尚不存在。");
        }

        return Task.Run(
            () => SortDominatorOrderRecords(inputPath, outputPath, workingDirectory, maximumChunkBytes, cancellationToken),
            CancellationToken.None);
    }

    /// <summary>
    /// 按 CLR ClassID、对象地址和大小对 Profiler 原始对象记录进行有界外部排序；
    /// 保留完整固定宽度记录，供保留快照写入阶段在不创建 ClassID 全量字典的前提下生成类型表。
    /// </summary>
    /// <param name="inputPath">未排序的 <c>ulong objectId + ulong classId + ulong size</c> 记录文件。</param>
    /// <param name="outputPath">不存在的排序结果文件。</param>
    /// <param name="workingDirectory">仅供本次排序使用的工作目录。</param>
    /// <param name="maximumChunkBytes">单个内存排序块的最大字节数。</param>
    /// <param name="cancellationToken">取消当前构建操作的令牌。</param>
    /// <returns>代表完成排序及归并的任务。</returns>
    public static Task SortProfilerObjectRecordsByClassIdAsync(
        string inputPath,
        string outputPath,
        string workingDirectory,
        int maximumChunkBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumChunkBytes, ProfilerObjectRecordBytes);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Profiler 对象外排排序输入文件不存在。", inputPath);
        }

        if (File.Exists(outputPath))
        {
            throw new IOException("Profiler 对象外排排序输出文件必须尚不存在。");
        }

        return Task.Run(
            () => SortProfilerObjectRecords(inputPath, outputPath, workingDirectory, maximumChunkBytes, ProfilerObjectRecordComparer.Instance, cancellationToken),
            CancellationToken.None);
    }

    /// <summary>
    /// 按对象地址、ClassID 和大小对 Profiler 原始对象记录执行有界外部排序，供去重阶段使用。
    /// </summary>
    /// <param name="inputPath">未排序的固定宽度对象记录文件。</param>
    /// <param name="outputPath">不存在的排序结果文件。</param>
    /// <param name="workingDirectory">仅供本次排序使用的临时目录。</param>
    /// <param name="maximumChunkBytes">单个内存排序块的最大字节数。</param>
    /// <param name="cancellationToken">取消当前构建操作的令牌。</param>
    /// <returns>代表完成排序及归并的任务。</returns>
    public static Task SortProfilerObjectRecordsByObjectIdAsync(
        string inputPath,
        string outputPath,
        string workingDirectory,
        int maximumChunkBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumChunkBytes, ProfilerObjectRecordBytes);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Profiler 对象外排排序输入文件不存在。", inputPath);
        }

        if (File.Exists(outputPath))
        {
            throw new IOException("Profiler 对象外排排序输出文件必须尚不存在。");
        }

        return Task.Run(
            () => SortProfilerObjectRecords(inputPath, outputPath, workingDirectory, maximumChunkBytes, ProfilerObjectRecordObjectIdComparer.Instance, cancellationToken),
            CancellationToken.None);
    }

    /// <summary>
    /// 按地址、对象标识顺序对 <c>ulong address + int objectId</c> 记录进行有界外部排序。
    /// </summary>
    /// <param name="inputPath">未排序固定宽度记录文件。</param>
    /// <param name="outputPath">不存在的排序结果文件。</param>
    /// <param name="workingDirectory">仅供本次排序使用的工作目录。</param>
    /// <param name="maximumChunkBytes">单个内存排序块的最大字节数。</param>
    /// <param name="cancellationToken">取消当前构建操作的令牌。</param>
    /// <returns>代表完成排序及归并的任务。</returns>
    public static Task SortAddressIdRecordsAsync(
        string inputPath,
        string outputPath,
        string workingDirectory,
        int maximumChunkBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumChunkBytes, AddressIdRecordBytes);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("外排排序输入文件不存在。", inputPath);
        }

        if (File.Exists(outputPath))
        {
            throw new IOException("外排排序输出文件必须尚不存在。");
        }

        return Task.Run(
            () => SortAddressIdRecords(inputPath, outputPath, workingDirectory, maximumChunkBytes, cancellationToken),
            CancellationToken.None);
    }

    /// <summary>
    /// 按第一或第二地址对 <c>ulong firstAddress + ulong secondAddress</c> 固定宽度记录进行有界外部排序。
    /// 此记录格式用于将原始引用边的两个地址分两阶段顺序归并为连续 objectId，避免对每条边执行随机二分查找。
    /// </summary>
    /// <param name="inputPath">未排序固定宽度地址对记录文件。</param>
    /// <param name="outputPath">不存在的排序结果文件。</param>
    /// <param name="workingDirectory">仅供本次排序使用的工作目录。</param>
    /// <param name="maximumChunkBytes">单个内存排序块的最大字节数。</param>
    /// <param name="sortBySecondAddress">为 <see langword="true"/> 时按第二、第一地址排序；否则按第一、第二地址排序。</param>
    /// <param name="cancellationToken">取消当前构建操作的令牌。</param>
    /// <returns>代表完成排序及归并的任务。</returns>
    public static Task SortAddressPairRecordsAsync(
        string inputPath,
        string outputPath,
        string workingDirectory,
        int maximumChunkBytes,
        bool sortBySecondAddress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumChunkBytes, AddressPairRecordBytes);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("地址对外排排序输入文件不存在。", inputPath);
        }

        if (File.Exists(outputPath))
        {
            throw new IOException("地址对外排排序输出文件必须尚不存在。");
        }

        return Task.Run(
            () => SortAddressPairs(inputPath, outputPath, workingDirectory, maximumChunkBytes, sortBySecondAddress, cancellationToken),
            CancellationToken.None);
    }

    /// <summary>
    /// 按 source 或 target objectId 对 <c>int sourceId + int targetId</c> 引用边记录进行有界外部排序。
    /// </summary>
    /// <param name="inputPath">未排序固定宽度边记录文件。</param>
    /// <param name="outputPath">不存在的排序结果文件。</param>
    /// <param name="workingDirectory">仅供本次排序使用的工作目录。</param>
    /// <param name="maximumChunkBytes">单个内存排序块的最大字节数。</param>
    /// <param name="sortByTarget">为 <see langword="true"/> 时按 target/source 排序，否则按 source/target 排序。</param>
    /// <param name="cancellationToken">取消当前构建操作的令牌。</param>
    /// <returns>代表完成排序及归并的任务。</returns>
    public static Task SortObjectIdEdgeRecordsAsync(
        string inputPath,
        string outputPath,
        string workingDirectory,
        int maximumChunkBytes,
        bool sortByTarget,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumChunkBytes, ObjectIdEdgeRecordBytes);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("外排边排序输入文件不存在。", inputPath);
        }

        if (File.Exists(outputPath))
        {
            throw new IOException("外排边排序输出文件必须尚不存在。");
        }

        return Task.Run(
            () => SortObjectIdEdges(inputPath, outputPath, workingDirectory, maximumChunkBytes, sortByTarget, cancellationToken),
            CancellationToken.None);
    }

    /// <summary>
    /// 将固定宽度边记录拆分排序并归并；排序键由调用方选择为 source/target 或 target/source。
    /// </summary>
    private static void SortObjectIdEdges(
        string inputPath,
        string outputPath,
        string workingDirectory,
        int maximumChunkBytes,
        bool sortByTarget,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(inputPath).Length % ObjectIdEdgeRecordBytes != 0)
        {
            throw new InvalidDataException("对象标识边外排记录长度无效。");
        }

        Directory.CreateDirectory(workingDirectory);
        HeapTemporaryArtifactCleanup.TryDeleteAbandonedDirectories(
            workingDirectory,
            ".edge-sort.*.tmp",
            HeapTemporaryArtifactCleanup.AbandonedDirectoryMinimumAge);
        var temporaryDirectory = Path.Combine(workingDirectory, $".edge-sort.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var chunks = WriteSortedEdgeChunks(inputPath, temporaryDirectory, maximumChunkBytes, sortByTarget, cancellationToken);
            MergeEdgeChunks(chunks, outputPath, sortByTarget, cancellationToken);
        }
        finally
        {
            HeapTemporaryArtifactCleanup.TryDeleteDirectory(temporaryDirectory);
        }
    }

    /// <summary>
    /// 将支配对象记录拆分为固定预算块，归并完成后只留下 objectId 排序工件。
    /// </summary>
    private static void SortDominatorOrderRecords(
        string inputPath,
        string outputPath,
        string workingDirectory,
        int maximumChunkBytes,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(inputPath).Length % DominatorOrderRecordBytes != 0)
        {
            throw new InvalidDataException("支配对象外排记录长度无效。");
        }

        Directory.CreateDirectory(workingDirectory);
        HeapTemporaryArtifactCleanup.TryDeleteAbandonedDirectories(
            workingDirectory,
            ".dominator-sort.*.tmp",
            HeapTemporaryArtifactCleanup.AbandonedDirectoryMinimumAge);
        var temporaryDirectory = Path.Combine(workingDirectory, $".dominator-sort.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var chunks = WriteSortedDominatorChunks(inputPath, temporaryDirectory, maximumChunkBytes, cancellationToken);
            MergeDominatorChunks(chunks, outputPath, cancellationToken);
        }
        finally
        {
            HeapTemporaryArtifactCleanup.TryDeleteDirectory(temporaryDirectory);
        }
    }

    /// <summary>
    /// 每次只分配一个由策略限制的记录数组，并将排序后的完整键写入临时块。
    /// </summary>
    private static List<string> WriteSortedDominatorChunks(
        string inputPath,
        string temporaryDirectory,
        int maximumChunkBytes,
        CancellationToken cancellationToken)
    {
        var recordsPerChunk = Math.Max(1, maximumChunkBytes / DominatorOrderManagedRecordBytes);
        var chunks = new List<string>();
        using var input = new BinaryReader(File.OpenRead(inputPath));
        while (input.BaseStream.Position < input.BaseStream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(
                recordsPerChunk,
                (input.BaseStream.Length - input.BaseStream.Position) / DominatorOrderRecordBytes);
            var records = new DominatorOrderRecord[count];
            for (var index = 0; index < count; index++)
            {
                if ((index & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                records[index] = ReadDominatorOrderRecord(input);
            }

            Array.Sort(records, DominatorOrderRecordComparer.Instance);
            var chunkPath = Path.Combine(temporaryDirectory, $"chunk-{chunks.Count:D8}.bin");
            using (var stream = new FileStream(chunkPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            using (var writer = new BinaryWriter(stream))
            {
                foreach (var record in records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteDominatorOrderRecord(writer, record);
                }
            }

            chunks.Add(chunkPath);
        }

        return chunks;
    }

    /// <summary>
    /// 以每块一条活动记录的最小堆归并支配排序块，并只输出 objectId。
    /// </summary>
    private static void MergeDominatorChunks(
        List<string> chunks,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var readers = new List<BinaryReader>(chunks.Count);
        try
        {
            foreach (var path in chunks)
            {
                readers.Add(new BinaryReader(File.OpenRead(path)));
            }

            var queue = new PriorityQueue<ChunkDominatorOrderRecord, DominatorOrderRecord>(DominatorOrderRecordComparer.Instance);
            for (var index = 0; index < readers.Count; index++)
            {
                if (TryReadDominatorOrderRecord(readers[index], out var record))
                {
                    queue.Enqueue(new ChunkDominatorOrderRecord(index, record), record);
                }
            }

            using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
            using var writer = new BinaryWriter(stream);
            while (queue.TryDequeue(out var current, out _))
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(current.Record.ObjectId);
                if (TryReadDominatorOrderRecord(readers[current.ChunkIndex], out var next))
                {
                    queue.Enqueue(new ChunkDominatorOrderRecord(current.ChunkIndex, next), next);
                }
            }
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    /// <summary>
    /// 从固定宽度输入读取一条完整支配排序记录。
    /// </summary>
    private static DominatorOrderRecord ReadDominatorOrderRecord(BinaryReader reader) => new(
        reader.ReadInt64(),
        reader.ReadInt64(),
        reader.ReadUInt64(),
        reader.ReadInt32());

    /// <summary>
    /// 在记录边界尝试读取下一条支配排序记录。
    /// </summary>
    private static bool TryReadDominatorOrderRecord(BinaryReader reader, out DominatorOrderRecord record)
    {
        if (reader.BaseStream.Position == reader.BaseStream.Length)
        {
            record = default;
            return false;
        }

        record = ReadDominatorOrderRecord(reader);
        return true;
    }

    /// <summary>
    /// 将完整排序键写入临时块，供多块归并保持与内存实现一致的确定顺序。
    /// </summary>
    private static void WriteDominatorOrderRecord(BinaryWriter writer, DominatorOrderRecord record)
    {
        writer.Write(record.RetainedSizeBytes);
        writer.Write(record.ShallowSizeBytes);
        writer.Write(record.Address);
        writer.Write(record.ObjectId);
    }

    /// <summary>
    /// 将 Profiler 原始对象记录拆块排序并归并；块内只保留预算允许的固定宽度记录。
    /// </summary>
    private static void SortProfilerObjectRecords(
        string inputPath,
        string outputPath,
        string workingDirectory,
        int maximumChunkBytes,
        IComparer<ProfilerObjectRecord> comparer,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(inputPath).Length % ProfilerObjectRecordBytes != 0)
        {
            throw new InvalidDataException("Profiler 对象外排记录长度无效。");
        }

        Directory.CreateDirectory(workingDirectory);
        HeapTemporaryArtifactCleanup.TryDeleteAbandonedDirectories(
            workingDirectory,
            ".profiler-object-sort.*.tmp",
            HeapTemporaryArtifactCleanup.AbandonedDirectoryMinimumAge);
        var temporaryDirectory = Path.Combine(workingDirectory, $".profiler-object-sort.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var chunks = WriteSortedProfilerObjectChunks(inputPath, temporaryDirectory, maximumChunkBytes, comparer, cancellationToken);
            MergeProfilerObjectChunks(chunks, outputPath, comparer, cancellationToken);
        }
        finally
        {
            HeapTemporaryArtifactCleanup.TryDeleteDirectory(temporaryDirectory);
        }
    }

    /// <summary>
    /// 按 ClassID、对象地址和大小写入每个已排序 Profiler 对象块。
    /// </summary>
    private static List<string> WriteSortedProfilerObjectChunks(
        string inputPath,
        string temporaryDirectory,
        int maximumChunkBytes,
        IComparer<ProfilerObjectRecord> comparer,
        CancellationToken cancellationToken)
    {
        var recordsPerChunk = Math.Max(1, maximumChunkBytes / ProfilerObjectRecordBytes);
        var chunks = new List<string>();
        using var input = new BinaryReader(File.OpenRead(inputPath));
        while (input.BaseStream.Position < input.BaseStream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var records = new List<ProfilerObjectRecord>(recordsPerChunk);
            while (records.Count < recordsPerChunk && input.BaseStream.Position < input.BaseStream.Length)
            {
                if ((records.Count & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                records.Add(new ProfilerObjectRecord(input.ReadUInt64(), input.ReadUInt64(), input.ReadUInt64()));
            }

            records.Sort(comparer);
            var path = Path.Combine(temporaryDirectory, $"chunk-{chunks.Count:D8}.bin");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            using (var writer = new BinaryWriter(stream))
            {
                foreach (var record in records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.Write(record.ObjectId);
                    writer.Write(record.ClassId);
                    writer.Write(record.SizeBytes);
                }
            }

            chunks.Add(path);
        }

        return chunks;
    }

    /// <summary>
    /// 用最小堆归并 Profiler 对象排序块；每个输入块仅保持一个待消费记录。
    /// </summary>
    private static void MergeProfilerObjectChunks(
        List<string> chunks,
        string outputPath,
        IComparer<ProfilerObjectRecord> comparer,
        CancellationToken cancellationToken)
    {
        var readers = new List<BinaryReader>(chunks.Count);
        try
        {
            foreach (var path in chunks)
            {
                readers.Add(new BinaryReader(File.OpenRead(path)));
            }

            var queue = new PriorityQueue<ChunkProfilerObjectRecord, ProfilerObjectRecord>(comparer);
            for (var index = 0; index < readers.Count; index++)
            {
                if (TryReadProfilerObjectRecord(readers[index], out var record))
                {
                    queue.Enqueue(new ChunkProfilerObjectRecord(index, record), record);
                }
            }

            using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
            using var writer = new BinaryWriter(stream);
            while (queue.TryDequeue(out var current, out _))
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(current.Record.ObjectId);
                writer.Write(current.Record.ClassId);
                writer.Write(current.Record.SizeBytes);
                if (TryReadProfilerObjectRecord(readers[current.ChunkIndex], out var next))
                {
                    queue.Enqueue(new ChunkProfilerObjectRecord(current.ChunkIndex, next), next);
                }
            }
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    /// <summary>
    /// 从 Profiler 对象排序块读取一个完整记录。
    /// </summary>
    private static bool TryReadProfilerObjectRecord(BinaryReader reader, out ProfilerObjectRecord record)
    {
        if (reader.BaseStream.Position == reader.BaseStream.Length)
        {
            record = default;
            return false;
        }

        record = new ProfilerObjectRecord(reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64());
        return true;
    }

    /// <summary>
    /// 以有界记录数量写入一个或多个已排序的对象标识边块。
    /// </summary>
    private static List<string> WriteSortedEdgeChunks(
        string inputPath,
        string temporaryDirectory,
        int maximumChunkBytes,
        bool sortByTarget,
        CancellationToken cancellationToken)
    {
        var recordsPerChunk = Math.Max(1, maximumChunkBytes / ObjectIdEdgeRecordBytes);
        var comparer = new ObjectIdEdgeRecordComparer(sortByTarget);
        var chunks = new List<string>();
        using var input = new BinaryReader(File.OpenRead(inputPath));
        while (input.BaseStream.Position < input.BaseStream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var records = new List<ObjectIdEdgeRecord>(recordsPerChunk);
            while (records.Count < recordsPerChunk && input.BaseStream.Position < input.BaseStream.Length)
            {
                if ((records.Count & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                records.Add(new ObjectIdEdgeRecord(input.ReadInt32(), input.ReadInt32()));
            }

            records.Sort(comparer);
            var path = Path.Combine(temporaryDirectory, $"chunk-{chunks.Count:D8}.bin");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            using (var writer = new BinaryWriter(stream))
            {
                foreach (var record in records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.Write(record.SourceId);
                    writer.Write(record.TargetId);
                }
            }

            chunks.Add(path);
        }

        return chunks;
    }

    /// <summary>
    /// 归并多个边排序块；每个输入块仅保留一个待写记录。
    /// </summary>
    private static void MergeEdgeChunks(
        List<string> chunks,
        string outputPath,
        bool sortByTarget,
        CancellationToken cancellationToken)
    {
        var readers = new List<BinaryReader>(chunks.Count);
        try
        {
            foreach (var path in chunks)
            {
                readers.Add(new BinaryReader(File.OpenRead(path)));
            }

            var comparer = new ObjectIdEdgeRecordComparer(sortByTarget);
            var queue = new PriorityQueue<ChunkEdgeRecord, ObjectIdEdgeRecord>(comparer);
            for (var index = 0; index < readers.Count; index++)
            {
                if (TryReadEdgeRecord(readers[index], out var record))
                {
                    queue.Enqueue(new ChunkEdgeRecord(index, record), record);
                }
            }

            using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
            using var writer = new BinaryWriter(stream);
            while (queue.TryDequeue(out var current, out _))
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(current.Record.SourceId);
                writer.Write(current.Record.TargetId);
                if (TryReadEdgeRecord(readers[current.ChunkIndex], out var next))
                {
                    queue.Enqueue(new ChunkEdgeRecord(current.ChunkIndex, next), next);
                }
            }
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    /// <summary>
    /// 从已排序边块读取一个固定宽度记录。
    /// </summary>
    private static bool TryReadEdgeRecord(BinaryReader reader, out ObjectIdEdgeRecord record)
    {
        if (reader.BaseStream.Position == reader.BaseStream.Length)
        {
            record = default;
            return false;
        }

        record = new ObjectIdEdgeRecord(reader.ReadInt32(), reader.ReadInt32());
        return true;
    }

    /// <summary>
    /// 先将输入拆为受控内存块排序，再以最小堆归并为一个顺序结果文件。
    /// </summary>
    private static void SortAddressIdRecords(
        string inputPath,
        string outputPath,
        string workingDirectory,
        int maximumChunkBytes,
        CancellationToken cancellationToken)
    {
        var inputLength = new FileInfo(inputPath).Length;
        if (inputLength % AddressIdRecordBytes != 0)
        {
            throw new InvalidDataException("地址到对象标识外排记录长度无效。");
        }

        Directory.CreateDirectory(workingDirectory);
        HeapTemporaryArtifactCleanup.TryDeleteAbandonedDirectories(
            workingDirectory,
            ".address-sort.*.tmp",
            HeapTemporaryArtifactCleanup.AbandonedDirectoryMinimumAge);
        var temporaryDirectory = Path.Combine(workingDirectory, $".address-sort.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var chunks = WriteSortedChunks(inputPath, temporaryDirectory, maximumChunkBytes, cancellationToken);
            MergeChunks(chunks, outputPath, cancellationToken);
        }
        finally
        {
            HeapTemporaryArtifactCleanup.TryDeleteDirectory(temporaryDirectory);
        }
    }

    /// <summary>
    /// 将地址对记录拆分排序并归并；调用方选择主地址键，所有中间块均受资源策略给定的内存上限约束。
    /// </summary>
    private static void SortAddressPairs(
        string inputPath,
        string outputPath,
        string workingDirectory,
        int maximumChunkBytes,
        bool sortBySecondAddress,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(inputPath).Length % AddressPairRecordBytes != 0)
        {
            throw new InvalidDataException("地址对外排记录长度无效。");
        }

        Directory.CreateDirectory(workingDirectory);
        HeapTemporaryArtifactCleanup.TryDeleteAbandonedDirectories(
            workingDirectory,
            ".address-pair-sort.*.tmp",
            HeapTemporaryArtifactCleanup.AbandonedDirectoryMinimumAge);
        var temporaryDirectory = Path.Combine(workingDirectory, $".address-pair-sort.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var chunks = WriteSortedAddressPairChunks(inputPath, temporaryDirectory, maximumChunkBytes, sortBySecondAddress, cancellationToken);
            MergeAddressPairChunks(chunks, outputPath, sortBySecondAddress, cancellationToken);
        }
        finally
        {
            HeapTemporaryArtifactCleanup.TryDeleteDirectory(temporaryDirectory);
        }
    }

    /// <summary>
    /// 将输入拆分为固定数量记录的排序块；每个块只占用配置允许的托管内存。
    /// </summary>
    private static List<string> WriteSortedChunks(
        string inputPath,
        string temporaryDirectory,
        int maximumChunkBytes,
        CancellationToken cancellationToken)
    {
        var recordsPerChunk = Math.Max(1, maximumChunkBytes / AddressIdRecordBytes);
        var chunkPaths = new List<string>();
        using var input = new BinaryReader(File.OpenRead(inputPath));
        while (input.BaseStream.Position < input.BaseStream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var records = new List<AddressIdRecord>(recordsPerChunk);
            while (records.Count < recordsPerChunk && input.BaseStream.Position < input.BaseStream.Length)
            {
                if ((records.Count & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                records.Add(new AddressIdRecord(input.ReadUInt64(), input.ReadInt32()));
            }

            records.Sort(AddressIdRecordComparer.Instance);
            var chunkPath = Path.Combine(temporaryDirectory, $"chunk-{chunkPaths.Count:D8}.bin");
            using (var stream = new FileStream(chunkPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            using (var writer = new BinaryWriter(stream))
            {
                foreach (var record in records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.Write(record.Address);
                    writer.Write(record.ObjectId);
                }
            }

            chunkPaths.Add(chunkPath);
        }

        return chunkPaths;
    }

    /// <summary>
    /// 以固定数量的地址对记录写入已排序块；每个块只持有预算允许的固定宽度记录。
    /// </summary>
    private static List<string> WriteSortedAddressPairChunks(
        string inputPath,
        string temporaryDirectory,
        int maximumChunkBytes,
        bool sortBySecondAddress,
        CancellationToken cancellationToken)
    {
        var recordsPerChunk = Math.Max(1, maximumChunkBytes / AddressPairRecordBytes);
        var comparer = new AddressPairRecordComparer(sortBySecondAddress);
        var chunks = new List<string>();
        using var input = new BinaryReader(File.OpenRead(inputPath));
        while (input.BaseStream.Position < input.BaseStream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var records = new List<AddressPairRecord>(recordsPerChunk);
            while (records.Count < recordsPerChunk && input.BaseStream.Position < input.BaseStream.Length)
            {
                if ((records.Count & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                records.Add(new AddressPairRecord(input.ReadUInt64(), input.ReadUInt64()));
            }

            records.Sort(comparer);
            var path = Path.Combine(temporaryDirectory, $"chunk-{chunks.Count:D8}.bin");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            using (var writer = new BinaryWriter(stream))
            {
                foreach (var record in records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.Write(record.FirstAddress);
                    writer.Write(record.SecondAddress);
                }
            }

            chunks.Add(path);
        }

        return chunks;
    }

    /// <summary>
    /// 使用以地址和对象标识为优先级的最小堆归并所有排序块；只为每个块保留一条当前记录。
    /// </summary>
    private static void MergeChunks(List<string> chunks, string outputPath, CancellationToken cancellationToken)
    {
        var readers = new List<BinaryReader>(chunks.Count);
        try
        {
            foreach (var path in chunks)
            {
                readers.Add(new BinaryReader(File.OpenRead(path)));
            }

            var queue = new PriorityQueue<ChunkRecord, AddressIdRecord>(AddressIdRecordComparer.Instance);
            for (var index = 0; index < readers.Count; index++)
            {
                if (TryReadRecord(readers[index], out var record))
                {
                    queue.Enqueue(new ChunkRecord(index, record), record);
                }
            }

            using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
            using var writer = new BinaryWriter(stream);
            while (queue.TryDequeue(out var current, out _))
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(current.Record.Address);
                writer.Write(current.Record.ObjectId);
                if (TryReadRecord(readers[current.ChunkIndex], out var next))
                {
                    queue.Enqueue(new ChunkRecord(current.ChunkIndex, next), next);
                }
            }
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    /// <summary>
    /// 归并多个地址对排序块；每个输入块仅保留一个待消费记录，以稳定维持排序键顺序。
    /// </summary>
    private static void MergeAddressPairChunks(
        List<string> chunks,
        string outputPath,
        bool sortBySecondAddress,
        CancellationToken cancellationToken)
    {
        var readers = new List<BinaryReader>(chunks.Count);
        try
        {
            foreach (var path in chunks)
            {
                readers.Add(new BinaryReader(File.OpenRead(path)));
            }

            var comparer = new AddressPairRecordComparer(sortBySecondAddress);
            var queue = new PriorityQueue<ChunkAddressPairRecord, AddressPairRecord>(comparer);
            for (var index = 0; index < readers.Count; index++)
            {
                if (TryReadAddressPairRecord(readers[index], out var record))
                {
                    queue.Enqueue(new ChunkAddressPairRecord(index, record), record);
                }
            }

            using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
            using var writer = new BinaryWriter(stream);
            while (queue.TryDequeue(out var current, out _))
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(current.Record.FirstAddress);
                writer.Write(current.Record.SecondAddress);
                if (TryReadAddressPairRecord(readers[current.ChunkIndex], out var next))
                {
                    queue.Enqueue(new ChunkAddressPairRecord(current.ChunkIndex, next), next);
                }
            }
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    /// <summary>
    /// 尝试读取一个完整固定宽度记录；输入块由本类型写入，因此 EOF 只允许出现在记录边界。
    /// </summary>
    private static bool TryReadRecord(BinaryReader reader, out AddressIdRecord record)
    {
        if (reader.BaseStream.Position == reader.BaseStream.Length)
        {
            record = default;
            return false;
        }

        record = new AddressIdRecord(reader.ReadUInt64(), reader.ReadInt32());
        return true;
    }

    /// <summary>
    /// 从已排序地址对块读取一个完整固定宽度记录；EOF 只允许位于记录边界。
    /// </summary>
    private static bool TryReadAddressPairRecord(BinaryReader reader, out AddressPairRecord record)
    {
        if (reader.BaseStream.Position == reader.BaseStream.Length)
        {
            record = default;
            return false;
        }

        record = new AddressPairRecord(reader.ReadUInt64(), reader.ReadUInt64());
        return true;
    }

    /// <summary>
    /// 表示固定宽度地址到对象标识记录。
    /// </summary>
    private readonly record struct AddressIdRecord(ulong Address, int ObjectId);

    /// <summary>
    /// 表示原始引用边中成对出现的源地址与目标地址。
    /// </summary>
    private readonly record struct AddressPairRecord(ulong FirstAddress, ulong SecondAddress);

    /// <summary>
    /// 为块内排序和归并队列提供完全确定的地址、对象标识比较。
    /// </summary>
    private sealed class AddressIdRecordComparer : IComparer<AddressIdRecord>
    {
        /// <summary>
        /// 共享的无状态比较器实例。
        /// </summary>
        public static AddressIdRecordComparer Instance { get; } = new();

        /// <inheritdoc />
        public int Compare(AddressIdRecord left, AddressIdRecord right)
        {
            var address = left.Address.CompareTo(right.Address);
            return address != 0 ? address : left.ObjectId.CompareTo(right.ObjectId);
        }
    }

    /// <summary>
    /// 按第一/第二地址及另一地址提供完全确定的地址对比较。
    /// </summary>
    private sealed class AddressPairRecordComparer : IComparer<AddressPairRecord>
    {
        private readonly bool _sortBySecondAddress;

        /// <summary>
        /// 创建指定主排序地址的比较器。
        /// </summary>
        public AddressPairRecordComparer(bool sortBySecondAddress) => _sortBySecondAddress = sortBySecondAddress;

        /// <inheritdoc />
        public int Compare(AddressPairRecord left, AddressPairRecord right)
        {
            var primary = _sortBySecondAddress
                ? left.SecondAddress.CompareTo(right.SecondAddress)
                : left.FirstAddress.CompareTo(right.FirstAddress);
            if (primary != 0)
            {
                return primary;
            }

            return _sortBySecondAddress
                ? left.FirstAddress.CompareTo(right.FirstAddress)
                : left.SecondAddress.CompareTo(right.SecondAddress);
        }
    }

    /// <summary>
    /// 表示一个排序块当前队首记录及其所属块编号。
    /// </summary>
    private readonly record struct ChunkRecord(int ChunkIndex, AddressIdRecord Record);

    /// <summary>
    /// 表示归并队列中一条地址对记录及其所属块编号。
    /// </summary>
    private readonly record struct ChunkAddressPairRecord(int ChunkIndex, AddressPairRecord Record);

    /// <summary>
    /// 表示一条以连续 objectId 编码的有向边。
    /// </summary>
    private readonly record struct ObjectIdEdgeRecord(int SourceId, int TargetId);

    /// <summary>
    /// 表示来自 Profiler 的固定宽度对象记录。
    /// </summary>
    private readonly record struct ProfilerObjectRecord(ulong ObjectId, ulong ClassId, ulong SizeBytes);

    /// <summary>
    /// 表示支配分页外排的完整排序键和最终 objectId 值。
    /// </summary>
    private readonly record struct DominatorOrderRecord(
        long RetainedSizeBytes,
        long ShallowSizeBytes,
        ulong Address,
        int ObjectId);

    /// <summary>
    /// 按 retained、shallow 降序和地址、objectId 升序提供确定性比较。
    /// </summary>
    private sealed class DominatorOrderRecordComparer : IComparer<DominatorOrderRecord>
    {
        /// <summary>共享的无状态比较器。</summary>
        public static DominatorOrderRecordComparer Instance { get; } = new();

        /// <inheritdoc />
        public int Compare(DominatorOrderRecord left, DominatorOrderRecord right)
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
    }

    /// <summary>
    /// 为 Profiler 对象块排序和归并提供 ClassID、对象地址、大小的确定性比较。
    /// </summary>
    private sealed class ProfilerObjectRecordComparer : IComparer<ProfilerObjectRecord>
    {
        /// <summary>共享的无状态比较器实例。</summary>
        public static ProfilerObjectRecordComparer Instance { get; } = new();

        /// <inheritdoc />
        public int Compare(ProfilerObjectRecord left, ProfilerObjectRecord right)
        {
            var classId = left.ClassId.CompareTo(right.ClassId);
            if (classId != 0)
            {
                return classId;
            }

            var objectId = left.ObjectId.CompareTo(right.ObjectId);
            return objectId != 0 ? objectId : left.SizeBytes.CompareTo(right.SizeBytes);
        }
    }

    /// <summary>
    /// 为对象去重排序提供按对象地址优先的确定性比较。
    /// </summary>
    private sealed class ProfilerObjectRecordObjectIdComparer : IComparer<ProfilerObjectRecord>
    {
        /// <summary>共享的无状态比较器实例。</summary>
        public static ProfilerObjectRecordObjectIdComparer Instance { get; } = new();

        /// <inheritdoc />
        public int Compare(ProfilerObjectRecord left, ProfilerObjectRecord right)
        {
            var objectId = left.ObjectId.CompareTo(right.ObjectId);
            if (objectId != 0)
            {
                return objectId;
            }

            var classId = left.ClassId.CompareTo(right.ClassId);
            return classId != 0 ? classId : left.SizeBytes.CompareTo(right.SizeBytes);
        }
    }

    /// <summary>
    /// 按 source/target 或 target/source 提供确定的边记录比较。
    /// </summary>
    private sealed class ObjectIdEdgeRecordComparer : IComparer<ObjectIdEdgeRecord>
    {
        private readonly bool _sortByTarget;

        /// <summary>
        /// 创建指定主排序键的边比较器。
        /// </summary>
        public ObjectIdEdgeRecordComparer(bool sortByTarget) => _sortByTarget = sortByTarget;

        /// <inheritdoc />
        public int Compare(ObjectIdEdgeRecord left, ObjectIdEdgeRecord right)
        {
            var primary = _sortByTarget
                ? left.TargetId.CompareTo(right.TargetId)
                : left.SourceId.CompareTo(right.SourceId);
            if (primary != 0)
            {
                return primary;
            }

            return _sortByTarget
                ? left.SourceId.CompareTo(right.SourceId)
                : left.TargetId.CompareTo(right.TargetId);
        }
    }

    /// <summary>
    /// 表示归并队列中一条边记录及其输入块编号。
    /// </summary>
    private readonly record struct ChunkEdgeRecord(int ChunkIndex, ObjectIdEdgeRecord Record);

    /// <summary>
    /// 表示 Profiler 对象归并队列中的记录及其输入块编号。
    /// </summary>
    private readonly record struct ChunkProfilerObjectRecord(int ChunkIndex, ProfilerObjectRecord Record);

    /// <summary>
    /// 表示归并队列中一条支配排序记录及其输入块编号。
    /// </summary>
    private readonly record struct ChunkDominatorOrderRecord(int ChunkIndex, DominatorOrderRecord Record);
}
