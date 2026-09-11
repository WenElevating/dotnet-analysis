using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.MemoryMappedFiles;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Tests.Diagnostics;

/// <summary>
/// 验证派生分析的分段映射读取器只打开受限窗口，且读取跨窗口的固定宽度值不会越界或读取错误数据。
/// </summary>
[TestClass]
[DoNotParallelize]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "测试名称描述场景。")]
public sealed class HeapMappedReadWindowTests
{
    /// <summary>
    /// 合法但没有任何 CSR 目标的空工件必须可由查询生命周期打开和释放；
    /// 只有实际读取空文件时才应报告越界，而不是在构造阶段失败。
    /// </summary>
    [TestMethod]
    public void Constructor_WhenArtifactIsEmpty_AllowsLifetimeWithoutCreatingAnInvalidMapping()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.EmptyMappedWindow.{Guid.NewGuid():N}.bin");
        try
        {
            using (File.Create(path)) { }

            using var reader = new HeapMappedReadWindow(path);

            Assert.AreEqual(0L, reader.CurrentMappedWindowBytes);
            Assert.ThrowsExactly<EndOfStreamException>(() => reader.ReadInt32(0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 当请求跨越第一个映射窗口时，读取器必须关闭旧视图、打开下一个有界视图并返回正确的数值。
    /// </summary>
    [TestMethod]
    public void ReadInt64_WhenOffsetMovesAcrossWindow_ReturnsCorrectValueAndKeepsWindowBounded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.MappedWindow.{Guid.NewGuid():N}.bin");
        try
        {
            var bytes = new byte[128 * 1024];
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(4), 123L);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(96 * 1024 + 8), 456L);
            File.WriteAllBytes(path, bytes);

            using var reader = new HeapMappedReadWindow(path, maximumWindowBytes: 64 * 1024);

            Assert.AreEqual(123L, reader.ReadInt64(4));
            Assert.AreEqual(456L, reader.ReadInt64(96 * 1024 + 8));
            Assert.IsLessThanOrEqualTo(64 * 1024L, reader.MaximumMappedWindowBytes);
            Assert.IsLessThanOrEqualTo(64 * 1024L, reader.CurrentMappedWindowBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 固定宽度值从窗口末尾前四字节开始时，读取器必须跨两个映射视图拼接完整值，
    /// 且不能为覆盖该值临时突破调用方指定的单窗口上限。
    /// </summary>
    [TestMethod]
    public void ReadInt64_WhenValueCrossesWindowBoundary_ReturnsCompleteValueWithinWindowLimit()
    {
        const int windowBytes = 64 * 1024;
        const long expected = 0x0102030405060708;
        var path = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.MappedWindowBoundary.{Guid.NewGuid():N}.bin");
        try
        {
            var bytes = new byte[windowBytes * 2];
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(windowBytes - sizeof(int)), expected);
            File.WriteAllBytes(path, bytes);

            using var reader = new HeapMappedReadWindow(path, windowBytes);

            Assert.AreEqual(expected, reader.ReadInt64(windowBytes - sizeof(int)));
            Assert.IsLessThanOrEqualTo(windowBytes, reader.CurrentMappedWindowBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 两个热点窗口之间反复随机访问时必须复用已打开的视图，避免 Dominator 在当前节点与虚拟根之间
    /// 每次往返都刷新和重建映射；该约束按视图创建次数验证，不依赖机器执行速度。
    /// </summary>
    [TestMethod]
    public void ReadInt32_WhenTwoCachedWindowsAreRevisited_ReusesBothViewsWithoutRemapping()
    {
        const int windowBytes = 64 * 1024;
        var path = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.MappedWindowLru.{Guid.NewGuid():N}.bin");
        try
        {
            var bytes = new byte[windowBytes * 2];
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 123);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(windowBytes + 4), 456);
            File.WriteAllBytes(path, bytes);

            using var window = new HeapMappedFileWindow(
                path,
                MemoryMappedFileAccess.Read,
                windowBytes,
                maximumCachedWindows: 2);

            Assert.AreEqual(123, window.ReadInt32(4));
            Assert.AreEqual(456, window.ReadInt32(windowBytes + 4));
            Assert.AreEqual(123, window.ReadInt32(4));
            Assert.AreEqual(2L, window.ViewOpenOperations);
            Assert.AreEqual(2, window.CachedWindowCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 多个独立查询同时保留映射窗口时，Diagnostics 全局窗口预算不能超过 64 MiB；
    /// 额外读取必须稳定拒绝，而不是仅依赖每个读取器的局部上限。
    /// </summary>
    [TestMethod]
    public void ReadInt32_WhenGlobalMappedWindowBudgetIsExhausted_RejectsTheNextWindow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.MappedWindowBudget.{Guid.NewGuid():N}.bin");
        var readers = new List<HeapMappedReadWindow>();
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(HeapIndexResourcePolicy.MaximumMappedWindowBytes / 8);
            }

            for (var index = 0; index < 8; index++)
            {
                var reader = new HeapMappedReadWindow(path);
                readers.Add(reader);
                Assert.AreEqual(0, reader.ReadInt32(0));
            }

            using var rejected = new HeapMappedReadWindow(path);
            var exception = Assert.ThrowsExactly<DiagnosticsException>(() => rejected.ReadInt32(0));

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotQueryLimitReached, exception.ErrorCode);
            Assert.AreEqual(0L, rejected.CurrentMappedWindowBytes);
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }

            File.Delete(path);
        }
    }
}
