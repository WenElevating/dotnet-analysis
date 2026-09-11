using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Tests.Diagnostics;

/// <summary>
/// 验证 Diagnostics 分析服务在不泄漏索引实现的前提下编排跨快照查询。
/// </summary>
[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "测试名称描述行为。")]
public sealed class DiagnosticsMemorySnapshotAnalysisServiceTests
{
    /// <summary>
    /// 快照对比必须按类型身份聚合不同地址的对象，并按浅表大小增长排序；不得以地址或 retained size 推断跨快照对象身份。
    /// </summary>
    [TestMethod]
    public async Task CompareSnapshotsAsync_AggregatesByTypeIdentityInsteadOfObjectAddress()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.ServiceComparison.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var catalog = new ImportedSnapshotCatalog();
            var store = new MemorySnapshotStore(layout, catalog);
            var registry = new MemorySnapshotReaderRegistry([new RetentionHeapSnapshotReader()]);
            using var service = new DiagnosticsMemorySnapshotAnalysisService(catalog, store, registry, layout);
            var baseline = CreateImportedSnapshot();
            var candidate = CreateImportedSnapshot();
            var baselinePath = Path.Combine(root, "baseline.retentionheap");
            var candidatePath = Path.Combine(root, "candidate.retentionheap");
            await RetentionHeapSnapshot.WriteAsync(
                baselinePath,
                new RetentionHeapSnapshot.SnapshotData(
                    [new TypeIdentity("Sample.Shared", "Sample"), new TypeIdentity("Sample.BaselineOnly", "Sample")],
                    [
                        new RetentionHeapSnapshot.ObjectRecord(1, 0, 10),
                        new RetentionHeapSnapshot.ObjectRecord(2, 1, 20)
                    ],
                    [],
                    []),
                CancellationToken.None);
            await RetentionHeapSnapshot.WriteAsync(
                candidatePath,
                new RetentionHeapSnapshot.SnapshotData(
                    [new TypeIdentity("Sample.Shared", "Sample"), new TypeIdentity("Sample.CandidateOnly", "Sample")],
                    [
                        new RetentionHeapSnapshot.ObjectRecord(101, 0, 30),
                        new RetentionHeapSnapshot.ObjectRecord(102, 0, 40),
                        new RetentionHeapSnapshot.ObjectRecord(103, 1, 100)
                    ],
                    [],
                    []),
                CancellationToken.None);
            catalog.Register(baseline.Id, baselinePath);
            catalog.Register(candidate.Id, candidatePath);

            var comparison = await service.CompareSnapshotsAsync(baseline, candidate, CancellationToken.None);

            Assert.AreEqual(baseline.Id, comparison.BaselineSnapshotId);
            Assert.AreEqual(candidate.Id, comparison.CandidateSnapshotId);
            Assert.AreEqual("Sample.CandidateOnly", comparison.Types[0].Type.TypeName);
            Assert.AreEqual("Sample.Shared", comparison.Types[1].Type.TypeName);
            Assert.AreEqual("Sample.BaselineOnly", comparison.Types[2].Type.TypeName);
            var shared = comparison.Types.Single(item => item.Type.TypeName == "Sample.Shared");
            Assert.AreEqual(1L, shared.BaselineObjectCount);
            Assert.AreEqual(2L, shared.CandidateObjectCount);
            Assert.AreEqual(10L, shared.BaselineShallowSizeBytes);
            Assert.AreEqual(70L, shared.CandidateShallowSizeBytes);
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
    /// 创建一个满足服务查询前置条件的导入快照描述。
    /// </summary>
    /// <returns>处于就绪状态的导入快照。</returns>
    private static MemorySnapshot CreateImportedSnapshot() => new(
        MemorySnapshotId.New(),
        MemorySnapshotOrigin.Imported,
        DateTimeOffset.UtcNow,
        captureStartedAtUtc: null,
        capturedAtUtc: DateTimeOffset.UtcNow,
        MemorySnapshotState.Ready);
}
