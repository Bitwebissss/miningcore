namespace Miningcore.Notifications.Messages;

/// <summary>Sent on every new network block (OnNewChainHeight).</summary>
public record ChainHeightStatsNotification
{
    public string PoolId { get; init; }
    public double NetworkHashrate { get; init; }
    public double? NetworkDifficulty { get; init; }
    public ulong BlockHeight { get; init; }
    public ulong NetworkBlockHeight { get; init; }
    public DateTime? LastNetworkBlockTime { get; init; }
    public uint? TotalConfirmedBlocks { get; init; }
    public uint? TotalPendingBlocks { get; init; }
    public uint? TotalOrphanedBlocks { get; init; }
    public decimal BlockReward { get; init; }
}

/// <summary>Sent when pool finds a block (OnBlockFound).</summary>
public record BlockFoundStatsNotification
{
    public string PoolId { get; init; }
    public double NetworkHashrate { get; init; }
    public double? NetworkDifficulty { get; init; }
    public ulong BlockHeight { get; init; }
    public ulong NetworkBlockHeight { get; init; }
    public DateTime? LastNetworkBlockTime { get; init; }
    public DateTime? LastPoolBlockTime { get; init; }
    public uint? Blocks24h { get; init; }
    public uint? TotalBlocks { get; init; }
    public uint? TotalConfirmedBlocks { get; init; }
    public uint? TotalPendingBlocks { get; init; }
    public uint? TotalOrphanedBlocks { get; init; }
    public decimal BlockReward { get; init; }
}

/// <summary>Sent on every stats cycle timer tick.</summary>
public record CycleStatsNotification
{
    public string PoolId { get; init; }
    public double PoolHashrate { get; init; }
    public int ConnectedMiners { get; init; }
    public double SharesPerSecond { get; init; }
    public int? ConnectedPeers { get; init; }
    public double? PoolEffort { get; init; }
}
