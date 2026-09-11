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
TestState.ConstructedGenerics = new ConstructedGenericRoots();
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

/// <summary>
/// 同时保留具有不同 CLR 类型实参的同一泛型类型定义，供 Profiler 集成测试验证构造泛型身份。
/// </summary>
sealed class ConstructedGenericRoots
{
    /// <summary>
    /// 保留 <see cref="List{String}"/> 的实例，使其构造类型出现在实时托管堆中。
    /// </summary>
    public List<string> Strings { get; } = [new string('s', 8)];

    /// <summary>
    /// 保留 <see cref="List{Object}"/> 的实例，使其与字符串实参构造类型可被区分。
    /// </summary>
    public List<object> Objects { get; } = [new object()];
}

static class TestState
{
    public static RetainedGraph? Graph;

    /// <summary>
    /// 在目标进程生命周期内固定持有构造泛型测试对象。
    /// </summary>
    public static ConstructedGenericRoots? ConstructedGenerics;
}
