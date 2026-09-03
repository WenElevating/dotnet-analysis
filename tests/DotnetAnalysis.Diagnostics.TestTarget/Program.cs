var allocations = new List<byte[]>();
TestState.Graph = new RetainedGraph(allocations, new byte[4096]);
Console.WriteLine("READY");
while (Console.ReadLine() is not "EXIT")
{
    allocations.Add(new byte[1024]);
    if (allocations.Count > 256)
    {
        allocations.RemoveAt(0);
    }
}

sealed class RetainedGraph
{
    public RetainedGraph(List<byte[]> allocations, byte[] payload)
    {
        Allocations = allocations;
        Payload = payload;
    }

    public List<byte[]> Allocations { get; }

    public byte[] Payload { get; }
}

static class TestState
{
    public static RetainedGraph? Graph;
}
