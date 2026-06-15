using Miningcore.Persistence.Model;

namespace Miningcore.Notifications.Messages;

public abstract class BlockNotification
{
    public string PoolId { get; set; }
    public ulong BlockHeight { get; set; }
    public string Symbol { get; set; }
    public string Name { get; set; }
}

public class BlockFoundNotification : BlockNotification
{
    public string Miner { get; set; }
    public string MinerExplorerLink { get; set; }
    public string Source { get; set; }
}

public class NewChainHeightNotification : BlockNotification
{
    /// <summary>Height of the last block accepted by the network (BlockHeight - 1).</summary>
    public ulong NetworkBlockHeight { get; set; }

    /// <summary>
    /// True when this notification was triggered by OUR pool successfully submitting a block
    /// (i.e. UpdateJob was called via JobRefreshBy.BlockFound).
    ///
    /// When true, BlockClassifierService MUST skip the network-path classification run because:
    ///   a) The block is not yet committed to the database (ShareRecorder flushes asynchronously).
    ///   b) A BlockFoundNotification is already en-route and will trigger the pool-path run
    ///      AFTER the DB commit, with fresh data.
    ///
    /// Skipping avoids the double-classification race where the first (network) run sees stale
    /// or missing data and the second (pool) run does the real work.
    /// </summary>
    public bool IsFromPoolBlockFind { get; set; }
}

public class BlockConfirmationProgressNotification : BlockNotification
{
    public double Progress { get; set; }
    public double? Effort { get; set; }
    public decimal Reward { get; set; }
    public string Miner { get; set; }
    public DateTime Created { get; set; }

    /// <summary>"pending" | "confirmed" | "orphaned" — already set on Block at classification time, no extra DB query.</summary>
    public string Status { get; set; }

    /// <summary>Block explorer URL computed from CoinTemplate.ExplorerBlockLinks. Null when no template is configured.</summary>
    public string InfoLink { get; set; }
}
