using System.Globalization;
using Miningcore.Blockchain;
using Miningcore.Messaging;
using Miningcore.Persistence.Model;
using Miningcore.Notifications.Messages;
using Miningcore.Configuration;
using Miningcore.Mining;

namespace Miningcore.Extensions;

public static class MessageBusExtensions
{
    public static void NotifyBlockFound(this IMessageBus messageBus, string poolId, Block block, CoinTemplate coin)
    {
        // miner account explorer link
        string minerExplorerLink = null;

        if(!string.IsNullOrEmpty(coin.ExplorerAccountLink))
            minerExplorerLink = string.Format(coin.ExplorerAccountLink, block.Miner);

        messageBus.SendMessage(new BlockFoundNotification
        {
            PoolId = poolId,
            BlockHeight = block.BlockHeight,
            Symbol = coin.Symbol,
            Name = coin.CanonicalName ?? coin.Name,
            Miner = block.Miner,
            MinerExplorerLink = minerExplorerLink,
            Source = block.Source,
        });
    }

    public static void NotifyBlockConfirmationProgress(this IMessageBus messageBus, string poolId, Block block, CoinTemplate coin)
    {
        // Compute infoLink from coin template — same logic as PoolApiController.
        // Uses data already on the block object (BlockHeight, Hash, Type) — zero extra DB queries.
        string infoLink = null;

        if(coin.ExplorerBlockLinks != null)
        {
            var blockType = !string.IsNullOrEmpty(block.Type) ? block.Type : "block";
            coin.ExplorerBlockLinks.TryGetValue(blockType, out var blockInfobaseUrl);

            if(!string.IsNullOrEmpty(blockInfobaseUrl))
            {
                if(blockInfobaseUrl.Contains(CoinMetaData.BlockHeightPH))
                    infoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                else if(blockInfobaseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                    infoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
            }
        }

        messageBus.SendMessage(new BlockConfirmationProgressNotification
        {
            PoolId = poolId,
            BlockHeight = block.BlockHeight,
            Symbol = coin.Symbol,
            Name = coin.CanonicalName ?? coin.Name,
            Effort = block.Effort,
            Progress = block.ConfirmationProgress,
            Reward = block.Reward,
            // Public WS broadcast — mask the payout address before it goes out (does not mutate block.Miner itself).
            Miner = block.Miner.MaskAddress(),
            Created = block.Created,
            // Status is already set on block at classification time — no extra DB query.
            Status = block.Status.ToString().ToLower(),
            InfoLink = infoLink,
        });
    }

    public static void NotifyChainHeight(this IMessageBus messageBus, string poolId, ulong workHeight, ulong networkBlockHeight, CoinTemplate coin, bool isFromPoolBlockFind = false)
    {
        messageBus.SendMessage(new NewChainHeightNotification
        {
            PoolId = poolId,
            BlockHeight = workHeight,
            NetworkBlockHeight = networkBlockHeight,
            Symbol = coin.Symbol,
            Name = coin.CanonicalName ?? coin.Name,
            IsFromPoolBlockFind = isFromPoolBlockFind,
        });
    }

    public static void NotifyChainHeightStats(this IMessageBus messageBus, string poolId,
        double networkHashrate, double? networkDifficulty,
        ulong blockHeight, ulong networkBlockHeight, DateTime? lastNetworkBlockTime,
        uint? totalConfirmedBlocks, uint? totalPendingBlocks, uint? totalOrphanedBlocks,
        decimal blockReward)
    {
        messageBus.SendMessage(new ChainHeightStatsNotification
        {
            PoolId = poolId,
            NetworkHashrate = networkHashrate,
            NetworkDifficulty = networkDifficulty,
            BlockHeight = blockHeight,
            NetworkBlockHeight = networkBlockHeight,
            LastNetworkBlockTime = lastNetworkBlockTime,
            TotalConfirmedBlocks = totalConfirmedBlocks,
            TotalPendingBlocks = totalPendingBlocks,
            TotalOrphanedBlocks = totalOrphanedBlocks,
            BlockReward = blockReward,
        });
    }

    public static void NotifyBlockFoundStats(this IMessageBus messageBus, string poolId,
        double networkHashrate, double? networkDifficulty,
        ulong blockHeight, ulong networkBlockHeight, DateTime? lastNetworkBlockTime,
        DateTime? lastPoolBlockTime, uint? blocks24h, uint? totalBlocks,
        uint? totalConfirmedBlocks, uint? totalPendingBlocks, uint? totalOrphanedBlocks,
        decimal blockReward)
    {
        messageBus.SendMessage(new BlockFoundStatsNotification
        {
            PoolId = poolId,
            NetworkHashrate = networkHashrate,
            NetworkDifficulty = networkDifficulty,
            BlockHeight = blockHeight,
            NetworkBlockHeight = networkBlockHeight,
            LastNetworkBlockTime = lastNetworkBlockTime,
            LastPoolBlockTime = lastPoolBlockTime,
            Blocks24h = blocks24h,
            TotalBlocks = totalBlocks,
            TotalConfirmedBlocks = totalConfirmedBlocks,
            TotalPendingBlocks = totalPendingBlocks,
            TotalOrphanedBlocks = totalOrphanedBlocks,
            BlockReward = blockReward,
        });
    }

    public static void NotifyCycleStats(this IMessageBus messageBus, string poolId,
        double poolHashrate, int connectedMiners, double sharesPerSecond,
        int? connectedPeers, double? poolEffort)
    {
        messageBus.SendMessage(new CycleStatsNotification
        {
            PoolId = poolId,
            PoolHashrate = poolHashrate,
            ConnectedMiners = connectedMiners,
            SharesPerSecond = sharesPerSecond,
            ConnectedPeers = connectedPeers,
            PoolEffort = poolEffort,
        });
    }

    public static void NotifyPoolStatus(this IMessageBus messageBus, IMiningPool pool, PoolStatus status)
    {
        messageBus.SendMessage(new PoolStatusNotification
        {
            Pool = pool,
            Status = status
        });
    }

    public static void SendTelemetry(this IMessageBus messageBus, string groupId, TelemetryCategory cat, TimeSpan elapsed,
        bool? success = null, string error = null, int? total = null)
    {
        messageBus.SendMessage(new TelemetryEvent(groupId, cat, elapsed, success, error)
        {
            Total = total ?? 0,
        });
    }

    public static void SendTelemetry(this IMessageBus messageBus, string groupId, TelemetryCategory cat, string info, TimeSpan elapsed,
        bool? success = null, string error = null, int? total = null)
    {
        messageBus.SendMessage(new TelemetryEvent(groupId, cat, info, elapsed, success, error)
        {
            Total = total ?? 0,
        });
    }
}
