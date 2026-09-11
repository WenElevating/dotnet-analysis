namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 描述一个可从 GC 根到达对象在支配树中的直接大小、保留大小和直接支配者。
/// </summary>
public sealed record MemoryDominatorInfo
{
    /// <summary>
    /// 创建一个支配树对象信息。
    /// </summary>
    /// <param name="objectInfo">对应的托管对象。</param>
    /// <param name="retainedSizeBytes">移除该对象后可一并释放的总字节数。</param>
    /// <param name="immediateDominatorAddress">直接支配者地址；虚拟超级根直接支配时为空。</param>
    /// <param name="rootEvidence">对象本身是 GC 根时的首选根证据；否则为空。</param>
    public MemoryDominatorInfo(
        MemoryObjectInfo objectInfo,
        long retainedSizeBytes,
        ulong? immediateDominatorAddress,
        MemoryRetentionRoot? rootEvidence)
    {
        ArgumentNullException.ThrowIfNull(objectInfo);
        if (retainedSizeBytes < objectInfo.SizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedSizeBytes), retainedSizeBytes, "Retained size cannot be smaller than shallow size.");
        }

        ObjectInfo = objectInfo;
        DirectSizeBytes = objectInfo.SizeBytes;
        RetainedSizeBytes = retainedSizeBytes;
        ImmediateDominatorAddress = immediateDominatorAddress;
        RootEvidence = rootEvidence;
    }

    /// <summary>
    /// 对应的托管对象。
    /// </summary>
    public MemoryObjectInfo ObjectInfo { get; }

    /// <summary>
    /// 对象自身的浅表大小。
    /// </summary>
    public long DirectSizeBytes { get; }

    /// <summary>
    /// 对象支配子树的总保留大小。
    /// </summary>
    public long RetainedSizeBytes { get; }

    /// <summary>
    /// 直接支配者的对象地址；顶层根对象为空。
    /// </summary>
    public ulong? ImmediateDominatorAddress { get; }

    /// <summary>
    /// 对象本身是 GC 根时可用的根证据摘要；普通对象为空。
    /// </summary>
    public MemoryRetentionRoot? RootEvidence { get; }
}
