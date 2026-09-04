using DotnetAnalysis.Diagnostics.Windows;
using System.Diagnostics.CodeAnalysis;

namespace DotnetAnalysis.Tests.Diagnostics;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class EventPipeManagedHeapReaderTests
{
    [TestMethod]
    public void ConvertCounterValueToBytes_ConvertsKnownUnitsAndRejectsUnknownUnit()
    {
        Assert.AreEqual(2L * 1024 * 1024, EventPipeManagedHeapReader.ConvertCounterValueToBytes(2, "MB"));
        Assert.AreEqual(2L * 1024, EventPipeManagedHeapReader.ConvertCounterValueToBytes(2, "KB"));
        Assert.IsNull(EventPipeManagedHeapReader.ConvertCounterValueToBytes(2, "widgets"));
        Assert.IsNull(EventPipeManagedHeapReader.ConvertCounterValueToBytes(2, null));
    }
}
