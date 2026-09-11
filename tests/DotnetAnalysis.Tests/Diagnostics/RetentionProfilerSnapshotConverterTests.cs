using System.Diagnostics.CodeAnalysis;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Tests.Diagnostics;

/// <summary>
/// 验证原生 Profiler 原始记录投影为持久化保留快照时不会伪造根函数证据。
/// </summary>
[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "测试名称描述转换行为。")]
public sealed class RetentionProfilerSnapshotConverterTests
{
    /// <summary>
    /// 栈根只在其 FunctionID 对应的已验证记录存在且名称非空时保留函数证据，并映射 CLR 根类别和标志。
    /// </summary>
    [TestMethod]
    public void Convert_MapsVerifiedStackFunctionAndNeverAssignsItToNonStackRoots()
    {
        var raw = new RetentionProfilerRawCapture(
            [
                Object((nuint)0x1000, (nuint)0x10),
                Object((nuint)0x2000, (nuint)0x10)
            ],
            [Edge((nuint)0x1000, (nuint)0x2000)],
            [
                Root((nuint)0x1000, 1, 5, (nuint)0xABC, 0),
                Root((nuint)0x2000, 3, 1, (nuint)0xABC, 0),
                Root((nuint)0x1000, 2, 0, (nuint)0xABC, 0),
                Root((nuint)0x2000, 0, 0, (nuint)0xABC, 0)
            ],
            [new RetentionProfilerRawFunction((nuint)0xABC, "Target.Holder.KeepAlive", "Target")],
            [new RetentionProfilerRawType((nuint)0x10, "Target.RetainedObject", "Target")]);

        var data = RetentionProfilerSnapshotConverter.Convert(raw);

        Assert.HasCount(1, data.Types);
        Assert.AreEqual("Target.RetainedObject", data.Types[0].TypeName);
        Assert.AreEqual("Target", data.Types[0].AssemblyName);
        Assert.HasCount(2, data.Objects);
        Assert.HasCount(1, data.Edges);
        Assert.HasCount(4, data.Roots);
        Assert.AreEqual(MemoryRootKind.Stack, data.Roots[0].Root.Kind);
        Assert.AreEqual(MemoryRootFlags.StackRoot | MemoryRootFlags.Pinned | MemoryRootFlags.Interior, data.Roots[0].Root.Flags);
        Assert.AreEqual("Target.Holder.KeepAlive", data.Roots[0].Root.FunctionName);
        Assert.AreEqual("Target", data.Roots[0].Root.ModuleName);
        Assert.AreEqual(MemoryRootKind.Handle, data.Roots[1].Root.Kind);
        Assert.IsNull(data.Roots[1].Root.FunctionName);
        Assert.IsNull(data.Roots[1].Root.ModuleName);
        Assert.AreEqual(MemoryRootKind.Finalizer, data.Roots[2].Root.Kind);
        Assert.IsNull(data.Roots[2].Root.FunctionName);
        Assert.IsNull(data.Roots[2].Root.ModuleName);
        Assert.AreEqual(MemoryRootKind.Other, data.Roots[3].Root.Kind);
        Assert.IsNull(data.Roots[3].Root.FunctionName);
        Assert.IsNull(data.Roots[3].Root.ModuleName);
    }

    /// <summary>
    /// 原生协议给出的完整构造泛型名称必须按 ClassID 投影为两个不同的类型身份。
    /// </summary>
    [TestMethod]
    public void Convert_WhenConstructedGenericEvidenceDiffers_PreservesDistinctTypeIdentities()
    {
        var raw = new RetentionProfilerRawCapture(
            [
                Object((nuint)0x1000, (nuint)0x10),
                Object((nuint)0x2000, (nuint)0x20)
            ],
            [],
            [],
            [],
            [
                new RetentionProfilerRawType(
                    (nuint)0x10,
                    "System.Collections.Generic.List`1<System.String>",
                    "System.Private.CoreLib"),
                new RetentionProfilerRawType(
                    (nuint)0x20,
                    "System.Collections.Generic.List`1<System.Object>",
                    "System.Private.CoreLib")
            ]);

        var data = RetentionProfilerSnapshotConverter.Convert(raw);

        CollectionAssert.AreEquivalent(
            new[]
            {
                new TypeIdentity("System.Collections.Generic.List`1<System.String>", "System.Private.CoreLib"),
                new TypeIdentity("System.Collections.Generic.List`1<System.Object>", "System.Private.CoreLib")
            },
            data.Types.ToArray());
    }

    /// <summary>
    /// 未知 CLR 根类别和无效函数证据索引必须保留为未知根且不产生函数名。
    /// </summary>
    [TestMethod]
    public void Convert_WhenRootKindOrFunctionEvidenceIsInvalid_ProducesNoFunctionEvidence()
    {
        var raw = new RetentionProfilerRawCapture(
            [Object((nuint)0x1000, (nuint)0x10)],
            [],
            [Root((nuint)0x1000, 99, 0, 0, 1)],
            [new RetentionProfilerRawFunction((nuint)0xABC, null, "Target")]);

        var data = RetentionProfilerSnapshotConverter.Convert(raw);

        Assert.AreEqual(MemoryRootKind.Unknown, data.Roots[0].Root.Kind);
        Assert.AreEqual(MemoryRootFlags.None, data.Roots[0].Root.Flags);
        Assert.IsNull(data.Roots[0].Root.FunctionName);
        Assert.IsNull(data.Roots[0].Root.ModuleName);
    }

    /// <summary>
    /// CLR 的 WEAKREF 与 REFCOUNTED 是不同位；转换必须完整保留两者，不能将弱引用误报为引用计数根。
    /// </summary>
    [TestMethod]
    public void Convert_WhenClrRootIsWeakAndRefCounted_PreservesBothDistinctFlags()
    {
        var raw = new RetentionProfilerRawCapture(
            [Object((nuint)0x1000, (nuint)0x10)],
            [],
            [Root((nuint)0x1000, 3, 0xA, 0, uint.MaxValue)],
            []);

        var data = RetentionProfilerSnapshotConverter.Convert(raw);

        Assert.AreEqual(
            MemoryRootFlags.WeakReference | MemoryRootFlags.RefCounted,
            data.Roots.Single().Root.Flags);
    }

    /// <summary>
    /// Profiler 已读取到的真实对象大小必须写入专用快照，供类型大小统计和大对象分析复用。
    /// </summary>
    [TestMethod]
    public void Convert_WhenRawObjectContainsSize_PreservesSizeBytes()
    {
        var raw = new RetentionProfilerRawCapture(
            [Object((nuint)0x1000, (nuint)0x10, (nuint)64)],
            [],
            [],
            []);

        var data = RetentionProfilerSnapshotConverter.Convert(raw);

        Assert.AreEqual(64L, data.Objects.Single().SizeBytes);
    }

    /// <summary>
    /// 从固定 Windows x64 ABI 字节序列构造原始对象记录，避免测试依赖实现字段可写性。
    /// </summary>
    private static RetentionProfilerRawObject Object(nuint objectId, nuint classId, nuint sizeBytes = 0)
    {
        Span<byte> bytes = stackalloc byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)objectId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], (ulong)classId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], (ulong)sizeBytes);
        return MemoryMarshal.Read<RetentionProfilerRawObject>(bytes);
    }

    /// <summary>
    /// 从固定 Windows x64 ABI 字节序列构造原始引用边记录。
    /// </summary>
    private static RetentionProfilerRawEdge Edge(nuint sourceObjectId, nuint targetObjectId)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)sourceObjectId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], (ulong)targetObjectId);
        return MemoryMarshal.Read<RetentionProfilerRawEdge>(bytes);
    }

    /// <summary>
    /// 从固定 Windows x64 ABI 字节序列构造原始根记录。
    /// </summary>
    private static RetentionProfilerRawRoot Root(
        nuint objectId,
        uint rootKind,
        uint rootFlags,
        nuint rootId,
        uint functionEvidenceIndex)
    {
        Span<byte> bytes = stackalloc byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], rootKind);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], rootFlags);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], (ulong)rootId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[24..], functionEvidenceIndex);
        return MemoryMarshal.Read<RetentionProfilerRawRoot>(bytes);
    }
}
