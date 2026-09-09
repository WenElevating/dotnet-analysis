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
    while (Console.ReadLine() is { } command && command is not "EXIT")
    {
        if (command is "HOLD_STACK")
        {
            HoldStackRoot();
            continue;
        }
        if (command is "COLLECT")
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
            Console.WriteLine("COLLECTED");
            continue;
        }

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

/// <summary>
/// 在同步调用帧中保持一个对象引用，直到测试发出释放命令，供 Profiler 验证 CLR 的栈根函数证据。
/// </summary>
static void HoldStackRoot()
{
    var retained = new StackHeldObject();
    Console.WriteLine("STACK_READY");
    var command = Console.ReadLine();
    GC.KeepAlive(retained);
    if (command is "RELEASE")
    {
        Console.WriteLine("RELEASED");
    }
}

/// <summary>
/// 仅用于确保握持引用拥有独立的托管对象类型。
/// </summary>
sealed class StackHeldObject
{
    /// <summary>
    /// 固定有效负载可防止运行时把该对象视为无内容的临时对象。
    /// </summary>
    public byte[] Payload { get; } = new byte[256];
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
