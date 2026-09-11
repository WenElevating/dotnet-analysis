using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Tests.Diagnostics;

/// <summary>
/// 验证大快照外排构建所需的固定宽度地址排序不会依赖全量地址字典。
/// </summary>
[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not have incorrect suffix", Justification = "测试名称描述场景。")]
public sealed class HeapIndexExternalSorterTests
{
    /// <summary>
    /// 外排已创建块目录但调用被取消时，即使外部锁阻止立即删除目录，也必须保留取消而不是泄漏清理 IO 异常。
    /// </summary>
    [TestMethod]
    public async Task SortAddressIdRecordsAsync_WhenCancellationCleanupIsLocked_PreservesCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.ExternalSortCancellation.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        FileStream? blocker = null;
        try
        {
            var input = Path.Combine(root, "input.bin");
            var output = Path.Combine(root, "output.bin");
            using (var stream = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                for (var index = 250_000; index > 0; index--)
                {
                    writer.Write((ulong)index);
                    writer.Write(index);
                }
            }

            var blockerReady = new TaskCompletionSource<FileStream>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FileSystemWatcher(root)
            {
                NotifyFilter = NotifyFilters.DirectoryName,
                EnableRaisingEvents = true
            };
            watcher.Created += (_, args) =>
            {
                if (!Path.GetFileName(args.FullPath).StartsWith(".address-sort.", StringComparison.Ordinal))
                {
                    return;
                }

                try
                {
                    var stream = new FileStream(
                        Path.Combine(args.FullPath, "cleanup-blocker.bin"),
                        FileMode.CreateNew,
                        FileAccess.ReadWrite,
                        FileShare.None);
                    if (!blockerReady.TrySetResult(stream))
                    {
                        stream.Dispose();
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    blockerReady.TrySetException(exception);
                }
            };

            using var cancellation = new CancellationTokenSource();
            var sorting = HeapIndexExternalSorter.SortAddressIdRecordsAsync(
                input,
                output,
                root,
                maximumChunkBytes: 12 * 1024,
                cancellation.Token);
            blocker = await blockerReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await sorting);
        }
        finally
        {
            blocker?.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 支配对象记录跨越多个块时必须按 retained size、浅表大小和地址排序，
    /// 最终文件只保留分页需要的 objectId，不能创建整份托管 order 列表。
    /// </summary>
    [TestMethod]
    public async Task SortDominatorOrderRecordsAsync_WhenInputExceedsChunk_WritesSemanticObjectOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.ExternalDominatorSort.{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var input = Path.Combine(root, "input.bin");
            var output = Path.Combine(root, "output.bin");
            await using (var stream = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                WriteDominatorRecord(writer, retainedSizeBytes: 80, shallowSizeBytes: 10, address: 9, objectId: 4);
                WriteDominatorRecord(writer, retainedSizeBytes: 100, shallowSizeBytes: 20, address: 8, objectId: 3);
                WriteDominatorRecord(writer, retainedSizeBytes: 100, shallowSizeBytes: 20, address: 1, objectId: 2);
                WriteDominatorRecord(writer, retainedSizeBytes: 100, shallowSizeBytes: 30, address: 7, objectId: 1);
                WriteDominatorRecord(writer, retainedSizeBytes: 40, shallowSizeBytes: 40, address: 2, objectId: 0);
            }

            await HeapIndexExternalSorter.SortDominatorOrderRecordsAsync(
                input,
                output,
                root,
                maximumChunkBytes: 56,
                CancellationToken.None);

            using var reader = new BinaryReader(File.OpenRead(output));
            var objectIds = new List<int>();
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                objectIds.Add(reader.ReadInt32());
            }

            CollectionAssert.AreEqual(new List<int> { 1, 2, 3, 4, 0 }, objectIds);
            Assert.IsEmpty(Directory.GetDirectories(root, ".dominator-sort.*.tmp"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 地址对记录跨越多个排序块时，必须按调用方选择的第二地址、第一地址顺序归并，供顺序地址映射阶段消费。
    /// </summary>
    [TestMethod]
    public async Task SortAddressPairRecordsAsync_WhenSortingBySecondAddressAcrossChunks_UsesSecondThenFirstOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.ExternalAddressPair.{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var input = Path.Combine(root, "input.bin");
            var output = Path.Combine(root, "output.bin");
            await using (var stream = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(9UL); writer.Write(4UL);
                writer.Write(1UL); writer.Write(8UL);
                writer.Write(9UL); writer.Write(2UL);
                writer.Write(3UL); writer.Write(1UL);
                writer.Write(1UL); writer.Write(5UL);
            }

            await HeapIndexExternalSorter.SortAddressPairRecordsAsync(
                input,
                output,
                root,
                maximumChunkBytes: 32,
                sortBySecondAddress: true,
                CancellationToken.None);

            using var reader = new BinaryReader(File.OpenRead(output));
            var values = new List<(ulong FirstAddress, ulong SecondAddress)>();
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                values.Add((reader.ReadUInt64(), reader.ReadUInt64()));
            }

            CollectionAssert.AreEqual(
                new List<(ulong FirstAddress, ulong SecondAddress)> { (3, 1), (9, 2), (9, 4), (1, 5), (1, 8) },
                values);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 强制使用多个很小排序块时，归并结果仍必须按地址和对象标识稳定有序。
    /// </summary>
    [TestMethod]
    public async Task SortAddressIdRecordsAsync_WhenInputExceedsChunk_SortsAllRecords()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.ExternalSort.{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var input = Path.Combine(root, "input.bin");
            var output = Path.Combine(root, "output.bin");
            await using (var stream = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(9UL);
                writer.Write(4);
                writer.Write(1UL);
                writer.Write(8);
                writer.Write(9UL);
                writer.Write(2);
                writer.Write(3UL);
                writer.Write(1);
                writer.Write(1UL);
                writer.Write(5);
            }

            await HeapIndexExternalSorter.SortAddressIdRecordsAsync(input, output, root, 24, CancellationToken.None);

            using var reader = new BinaryReader(File.OpenRead(output));
            var values = new List<(ulong Address, int ObjectId)>();
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                values.Add((reader.ReadUInt64(), reader.ReadInt32()));
            }

            CollectionAssert.AreEqual(
                new List<(ulong Address, int ObjectId)> { (1, 5), (1, 8), (3, 1), (9, 2), (9, 4) },
                values);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 边外排排序必须能以 target/source 顺序生成反向 CSR 所需的连续目标分组。
    /// </summary>
    [TestMethod]
    public async Task SortObjectIdEdgeRecordsAsync_WhenSortingByTarget_UsesTargetThenSourceOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.ExternalEdgeSort.{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var input = Path.Combine(root, "input.bin");
            var output = Path.Combine(root, "output.bin");
            await using (var stream = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(4);
                writer.Write(2);
                writer.Write(1);
                writer.Write(3);
                writer.Write(2);
                writer.Write(2);
                writer.Write(3);
                writer.Write(1);
            }

            await HeapIndexExternalSorter.SortObjectIdEdgeRecordsAsync(
                input,
                output,
                root,
                maximumChunkBytes: 16,
                sortByTarget: true,
                CancellationToken.None);

            using var reader = new BinaryReader(File.OpenRead(output));
            var values = new List<(int SourceId, int TargetId)>();
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                values.Add((reader.ReadInt32(), reader.ReadInt32()));
            }

            CollectionAssert.AreEqual(
                new List<(int SourceId, int TargetId)> { (3, 1), (2, 2), (4, 2), (1, 3) },
                values);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// Profiler 原始对象必须按 ClassID 外排，以便后续流式写入类型表；多个小块归并时不得丢失对象大小或地址排序。
    /// </summary>
    [TestMethod]
    public async Task SortProfilerObjectRecordsByClassIdAsync_WhenInputExceedsChunk_SortsFullRecords()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.ExternalProfilerObject.{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var input = Path.Combine(root, "input.bin");
            var output = Path.Combine(root, "output.bin");
            await using (var stream = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(9UL); writer.Write(2UL); writer.Write(90UL);
                writer.Write(2UL); writer.Write(1UL); writer.Write(20UL);
                writer.Write(4UL); writer.Write(2UL); writer.Write(40UL);
                writer.Write(1UL); writer.Write(1UL); writer.Write(10UL);
            }

            await HeapIndexExternalSorter.SortProfilerObjectRecordsByClassIdAsync(
                input,
                output,
                root,
                maximumChunkBytes: 24,
                CancellationToken.None);

            using var reader = new BinaryReader(File.OpenRead(output));
            var values = new List<(ulong ObjectId, ulong ClassId, ulong SizeBytes)>();
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                values.Add((reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64()));
            }

            CollectionAssert.AreEqual(
                new List<(ulong ObjectId, ulong ClassId, ulong SizeBytes)>
                {
                    (1, 1, 10),
                    (2, 1, 20),
                    (4, 2, 40),
                    (9, 2, 90)
                },
                values);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 按派生排序输入的固定宽度布局写入一条测试记录；期望顺序由测试中的字面值独立给出。
    /// </summary>
    private static void WriteDominatorRecord(
        BinaryWriter writer,
        long retainedSizeBytes,
        long shallowSizeBytes,
        ulong address,
        int objectId)
    {
        writer.Write(retainedSizeBytes);
        writer.Write(shallowSizeBytes);
        writer.Write(address);
        writer.Write(objectId);
    }
}
