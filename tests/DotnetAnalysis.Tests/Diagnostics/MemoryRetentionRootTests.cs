using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Tests.Diagnostics;

/// <summary>
/// 验证保留根模型不会接受与 CLR 根类别矛盾的函数证据。
/// </summary>
[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "测试名称描述根证据约束。")]
public sealed class MemoryRetentionRootTests
{
    /// <summary>
    /// 验证只有栈根能携带 CLR FunctionID 解析出的持有函数。
    /// </summary>
    [TestMethod]
    public void Constructor_WhenNonStackRootContainsFunctionEvidence_RejectsFabricatedFunction()
    {
        Assert.ThrowsExactly<ArgumentException>(() => _ = new MemoryRetentionRoot(
            MemoryRootKind.Handle,
            MemoryRootFlags.None,
            "Sample.Holder.KeepAlive",
            "Sample"));
    }
}
