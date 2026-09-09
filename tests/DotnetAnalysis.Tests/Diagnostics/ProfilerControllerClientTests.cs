using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Tests.Diagnostics;

/// <summary>
/// 验证受管侧通过单独客户端调用原生 Controller，而不是把 P/Invoke 细节泄漏到会话编排。
/// </summary>
[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not have incorrect suffix", Justification = "测试名说明行为。")]
public sealed class ProfilerControllerClientTests
{
    /// <summary>
    /// Controller 客户端必须作为 Diagnostics 内部边界存在，以统一 native 加载和 HRESULT 映射。
    /// </summary>
    [TestMethod]
    public void ProfilerControllerClient_ExistsAsAnIndependentNativeInteropBoundary()
    {
        var type = typeof(MemorySnapshotStore).Assembly.GetType(
            "DotnetAnalysis.Diagnostics.Windows.ProfilerControllerClient");

        Assert.IsNotNull(type);
    }
}
