using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Tests.Diagnostics;

/// <summary>
/// 验证保留分析专用快照文件的格式边界，避免将未完成或受损文件交给索引构建器。
/// </summary>
[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "测试名描述行为。")]
public sealed class RetentionHeapSnapshotTests
{
    /// <summary>
    /// 保留分析格式必须作为独立实现存在，不能伪装成 GCDump 文件。
    /// </summary>
    [TestMethod]
    public void RetentionHeapSnapshot_ExistsAsAnIndependentSnapshotFormat()
    {
        var snapshotType = typeof(GCDumpSnapshotReader).Assembly.GetType(
            "DotnetAnalysis.Diagnostics.Windows.RetentionHeapSnapshot");

        Assert.IsNotNull(snapshotType);
    }

    /// <summary>
    /// 专用格式必须保留栈根 FunctionID 已解析出的函数证据，并可恢复为正常引用路径索引。
    /// </summary>
    [TestMethod]
    public async Task WriteAndReadAsync_PreservesStackRootFunctionEvidenceAndObjectGraph()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.retentionheap");
        try
        {
            await RetentionHeapSnapshot.WriteAsync(
                path,
                new RetentionHeapSnapshot.SnapshotData(
                    [new TypeIdentity("Sample.Node", "Sample")],
                    [
                        new RetentionHeapSnapshot.ObjectRecord(1, 0, 24),
                        new RetentionHeapSnapshot.ObjectRecord(2, 0, 32)
                    ],
                    [new RetentionHeapSnapshot.EdgeRecord(1, 2)],
                    [new RetentionHeapSnapshot.RootRecord(
                        1,
                        new MemoryRetentionRoot(
                            MemoryRootKind.Stack,
                            MemoryRootFlags.StackRoot,
                            "Sample.Holder.KeepAlive",
                            "Sample"))]),
                CancellationToken.None);

            var index = await RetentionHeapSnapshot.ReadIndexAsync(path, CancellationToken.None);
            var paths = index.GetRetentionPaths(2, maxPathCount: 16);

            Assert.IsNotNull(paths);
            Assert.HasCount(1, paths.Paths);
            Assert.AreEqual("Sample.Holder.KeepAlive", paths.Paths[0].Root.FunctionName);
            CollectionAssert.AreEqual(new ulong[] { 1, 2 }, paths.Paths[0].Objects.Select(item => item.Address).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 修改已经完成的负载必须在分配对象表之前被校验和检测拒绝。
    /// </summary>
    [TestMethod]
    public async Task ReadIndexAsync_WhenPayloadIsTampered_RejectsTheSnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.retentionheap");
        try
        {
            await RetentionHeapSnapshot.WriteAsync(
                path,
                new RetentionHeapSnapshot.SnapshotData(
                    [new TypeIdentity("Sample.Node", "Sample")],
                    [new RetentionHeapSnapshot.ObjectRecord(1, 0, 24)],
                    [],
                    []),
                CancellationToken.None);
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Position = stream.Length - 1;
                var value = stream.ReadByte();
                stream.Position--;
                stream.WriteByte((byte)(value ^ 0xff));
            }

            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                async () => await RetentionHeapSnapshot.ReadIndexAsync(path, CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.ProfilerCaptureFailed, exception.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
