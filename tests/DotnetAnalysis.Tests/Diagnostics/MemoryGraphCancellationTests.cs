using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Core.Diagnostics;
using FastSerialization;
using Graphs;

namespace DotnetAnalysis.Tests.Diagnostics;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class MemoryGraphCancellationTests
{
    [TestMethod]
    public void Serialize_WhenCancellationArrivesDuringLargeObjectTraversal_ThrowsOperationCanceledException()
    {
        using var cancellation = new CancellationTokenSource();
        var objects = new CancellingObjects(cancellation, 16_385);
        var graph = new MemoryGraph(
            Array.Empty<(string Name, int Size, string? Module)>(),
            new int[objects.Count + 1],
            [],
            objects,
            cancellation.Token);
        var path = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.MemoryGraph.{Guid.NewGuid():N}.gcdump");

        try
        {
            Assert.ThrowsExactly<OperationCanceledException>(() =>
            {
                var serializer = new Serializer(path, graph, FileShare.Read);
                serializer.Close();
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class CancellingObjects : IReadOnlyList<MemoryObjectInfo>
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly MemoryObjectInfo _object = new(1, new TypeIdentity("Test.Object", "Tests"), 16);

        public CancellingObjects(CancellationTokenSource cancellation, int count)
        {
            _cancellation = cancellation;
            Count = count;
        }

        public int Count { get; }

        public MemoryObjectInfo this[int index]
        {
            get
            {
                if (index == 1)
                {
                    _cancellation.Cancel();
                }

                return _object;
            }
        }

        public IEnumerator<MemoryObjectInfo> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
