var allocations = new List<byte[]>();
Console.WriteLine("READY");
while (Console.ReadLine() is not "EXIT")
{
    allocations.Add(new byte[1024]);
    if (allocations.Count > 256)
    {
        allocations.RemoveAt(0);
    }
}
