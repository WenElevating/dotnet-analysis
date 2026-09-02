using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

public sealed class AllocationSampleCollector
{
    private readonly AllocationProfileBuilder _builder;

    public AllocationSampleCollector(AllocationProfileBuilder builder)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
    }

    public void Add(TypeIdentity type, IReadOnlyList<CallStackFrame> frames, long bytes) => _builder.Add(type, frames, bytes);

    public void MarkInterrupted(DateTimeOffset observedAtUtc) => _builder.MarkInterrupted(observedAtUtc);
}
