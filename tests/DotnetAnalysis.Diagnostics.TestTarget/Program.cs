const string InitialObjectCountVariable = "DOTNET_ANALYSIS_TEST_OBJECT_COUNT";
const string ExecutionWorkloadVariable = "DOTNET_ANALYSIS_TEST_EXECUTION_WORKLOAD";
const int LargeObjectPayloadBytes = 256;

var initialObjectCount = ReadInitialObjectCount();
var allocations = new List<byte[]>(initialObjectCount);
for (var index = 0; index < initialObjectCount; index++)
{
    allocations.Add(new byte[LargeObjectPayloadBytes]);
}

TestState.Graph = new RetainedGraph(allocations, new byte[4096]);
var executionWorkload = ReadExecutionWorkloadEnabled() ? ExecutionSamplingWorkload.Start() : null;
try
{
    Console.WriteLine("READY");
    while (Console.ReadLine() is not "EXIT")
    {
        allocations.Add(new byte[1024]);
        if (allocations.Count > 256)
        {
            allocations.RemoveAt(0);
        }
    }
}
finally
{
    if (executionWorkload is not null)
    {
        await executionWorkload.StopAsync();
    }
}

static int ReadInitialObjectCount()
{
    var value = Environment.GetEnvironmentVariable(InitialObjectCountVariable);
    return int.TryParse(value, out var count) && count is >= 0 and <= 2_000_000
        ? count
        : 0;
}

static bool ReadExecutionWorkloadEnabled() =>
    string.Equals(
        Environment.GetEnvironmentVariable(ExecutionWorkloadVariable),
        "true",
        StringComparison.OrdinalIgnoreCase);

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
