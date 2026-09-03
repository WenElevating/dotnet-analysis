using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

public sealed class AllocationProfileBuilder
{
    private readonly object _gate = new();
    private DateTimeOffset _startedAtUtc;
    private readonly Dictionary<(TypeIdentity Type, string Frames), (long Bytes, IReadOnlyList<CallStackFrame> Frames)> _entries = [];
    private bool _interrupted;

    public AllocationProfileBuilder(DateTimeOffset startedAtUtc)
    {
        _startedAtUtc = startedAtUtc;
    }

    public void Add(TypeIdentity type, IReadOnlyList<CallStackFrame> frames, long observedAllocatedBytes)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfNegative(observedAllocatedBytes);

        lock (_gate)
        {
            var normalizedFrames = frames.ToArray();
            var key = (type, string.Join("|", normalizedFrames.Select(frame => frame.Name)));
            if (_entries.TryGetValue(key, out var current))
            {
                _entries[key] = (checked(current.Bytes + observedAllocatedBytes), current.Frames);
            }
            else
            {
                _entries[key] = (observedAllocatedBytes, normalizedFrames);
            }
        }
    }

    public void MarkInterrupted(DateTimeOffset observedAtUtc)
    {
        lock (_gate)
        {
            _interrupted = true;
        }
    }

    public AllocationProfile Seal(DateTimeOffset capturedAtUtc)
    {
        lock (_gate)
        {
            return new AllocationProfile(
                _startedAtUtc,
                capturedAtUtc < _startedAtUtc ? _startedAtUtc : capturedAtUtc,
                _entries
                    .Select(entry => new AllocationHotspot(entry.Key.Type, entry.Value.Bytes, entry.Value.Frames))
                    .OrderByDescending(hotspot => hotspot.ObservedAllocatedBytes)
                    .ToArray(),
                _interrupted ? AllocationProfileDataQuality.Interrupted : AllocationProfileDataQuality.Continuous);
        }
    }

    public void BeginNextInterval(DateTimeOffset startedAtUtc)
    {
        lock (_gate)
        {
            _startedAtUtc = startedAtUtc;
            _entries.Clear();
            _interrupted = false;
        }
    }
}
