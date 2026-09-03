using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Desktop.Infrastructure;
using DotnetAnalysis.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics.CodeAnalysis;

namespace DotnetAnalysis.Tests.Desktop;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class ShellViewModelDiagnosticsEventTests
{
    [TestMethod]
    public async Task ShellViewModel_WhenAnalysisCompletes_ShowsChineseReadyStatus()
    {
        await using var eventBus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        using var shell = new ShellViewModel(
            eventBus,
            new InlineUiDispatcher(),
            NullLogger<ShellViewModel>.Instance);

        await eventBus.PublishAsync(
            new MemorySnapshotAnalysisCompleted(
                ProcessDiagnosticsSessionId.New(),
                MemorySnapshotId.New(),
                DateTimeOffset.UtcNow,
                "test"),
            CancellationToken.None);

        await WaitUntilAsync(() => shell.StatusText == "快照分析已完成");
        Assert.AreEqual("快照分析已完成", shell.StatusText);
    }

    [TestMethod]
    public async Task ShellViewModel_WhenSessionEnds_ShowsEndedStatus()
    {
        await using var eventBus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        using var shell = new ShellViewModel(
            eventBus,
            new InlineUiDispatcher(),
            NullLogger<ShellViewModel>.Instance);

        await eventBus.PublishAsync(
            new ProcessDiagnosticsSessionEnded(
                ProcessDiagnosticsSessionId.New(),
                DateTimeOffset.UtcNow,
                "test"),
            CancellationToken.None);

        await WaitUntilAsync(() => shell.StatusText == "诊断会话已结束");
        Assert.AreEqual("诊断会话已结束", shell.StatusText);
    }

    [TestMethod]
    public async Task ShellViewModel_WhenErrorArrives_DoesNotShowRawMessage()
    {
        await using var eventBus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        using var shell = new ShellViewModel(
            eventBus,
            new InlineUiDispatcher(),
            NullLogger<ShellViewModel>.Instance);

        await eventBus.PublishAsync(
            new MemorySnapshotAnalysisFailed(
                ProcessDiagnosticsSessionId.New(),
                MemorySnapshotId.New(),
                DiagnosticsErrorCode.TargetChanged,
                "raw secret detail",
                DateTimeOffset.UtcNow,
                "test"),
            CancellationToken.None);

        await WaitUntilAsync(() => shell.StatusText.StartsWith("快照分析失败：", StringComparison.Ordinal));
        Assert.AreEqual("快照分析失败：目标已变化", shell.StatusText);
        Assert.IsFalse(shell.StatusText.Contains("raw secret detail", StringComparison.Ordinal));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow.AddSeconds(1);
        while (!predicate() && DateTime.UtcNow < timeout)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(predicate());
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }
}
