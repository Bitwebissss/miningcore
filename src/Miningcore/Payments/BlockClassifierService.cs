using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Autofac;
using Autofac.Features.Metadata;
using Microsoft.Extensions.Hosting;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using NLog;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Payments;

/// <summary>
/// Classifies pending blocks (confirmed / orphaned / still immature) independently from payment processing.
///
/// -- TWO TRIGGER PATHS — completely different frontend behaviour --
///
///   PATH 1 — BlockFoundNotification  (OUR pool successfully submitted a block)
///     ShareRecorder commits the block to DB, then fires BlockFoundNotification.
///     This service picks it up → runs ClassifyPoolBlocksAsync → queries DB for fresh counters →
///     fires blockfoundstats WS message. That message IS the "block found" notification for the frontend.
///     NO separate notification — blockfoundstats is the single, post-classification signal.
///
///   PATH 2 — NewChainHeightNotification  (network produced a new block, not necessarily ours)
///     Each new network block may advance confirmation count for OUR pending blocks.
///     Classifier runs → pending blocks transition Pending→Confirmed or Pending→Orphaned →
///     fires chainheightstats WS message with fresh counts. No toast — we did not find that block.
///
///     IMPORTANT: when NewChainHeightNotification.IsFromPoolBlockFind == true the notification
///     was produced by UpdateJob reacting to our own block submission (JobRefreshBy.BlockFound).
///     In that case PATH 2 is SKIPPED — the block is not yet in the database (ShareRecorder
///     flushes asynchronously) so any classification would see stale data.  PATH 1 is already
///     on its way (BlockFoundNotification fires after the DB commit) and will do the right thing.
///     This eliminates the double-classification that previously occurred for every pool-found block.
///
/// -- CONCURRENCY MODEL — per-pool serialised queue --
///
///   • At most ONE classification run executes per pool at any time.
///   • A trigger that arrives while a run is active is QUEUED (never dropped).
///   • After a run completes the service checks for pending work and re-runs immediately
///     if any trigger is waiting — no trigger is ever lost.
///   • OurBlock events (BlockFoundNotification) accumulate per-pool in the pending queue.
///   • Network triggers (NewChainHeightNotification) are coalesced — multiple pending
///     network triggers count as one, because classification is idempotent.
/// </summary>
public class BlockClassifierService : BackgroundService
{
    public BlockClassifierService(
        IComponentContext ctx,
        IConnectionFactory cf,
        IBlockRepository blockRepo,
        IShareRepository shareRepo,
        ClusterConfig clusterConfig,
        IMessageBus messageBus,
        IMasterClock clock)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(cf);
        Contract.RequiresNonNull(blockRepo);
        Contract.RequiresNonNull(shareRepo);
        Contract.RequiresNonNull(messageBus);
        Contract.RequiresNonNull(clock);

        this.ctx = ctx;
        this.cf = cf;
        this.blockRepo = blockRepo;
        this.shareRepo = shareRepo;
        this.clusterConfig = clusterConfig;
        this.messageBus = messageBus;
        this.clock = clock;
    }

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

    private readonly IComponentContext ctx;
    private readonly IConnectionFactory cf;
    private readonly IBlockRepository blockRepo;
    private readonly IShareRepository shareRepo;
    private readonly ClusterConfig clusterConfig;
    private readonly IMessageBus messageBus;
    private readonly IMasterClock clock;
    private readonly CompositeDisposable disposables = new();
    private readonly ConcurrentDictionary<string, IMiningPool> pools = new();

    // -- Per-pool queue state --

    /// <summary>
    /// Thread-safe per-pool state machine for serialised classification with pending-retry.
    ///
    /// States (informal):
    ///   idle        — no run in progress, no pending work
    ///   running     — one run executing, nothing queued
    ///   running+Q   — one run executing, one logical pending run queued
    ///                 (may carry ≥0 OurBlock heights and/or a network-only flag)
    ///
    /// The "pending slot" is a single logical run, not an unbounded queue.
    /// Multiple arriving triggers are merged into that one slot; OurBlock heights
    /// accumulate so every submitted block is reflected in the next post-classification message.
    /// </summary>
    private sealed class PoolRunState
    {
        private readonly object sync = new();
        private bool running;

        private readonly List<BlockFoundNotification> pendingOurBlocks = [];
        private bool pendingNetworkRun;

        /// <summary>
        /// Attempt to become the exclusive runner for this pool.
        /// Returns true + the initial batch of OurBlock notifications if the caller is now the runner.
        /// Returns false if already running; the trigger is safely queued instead.
        /// </summary>
        public bool TryBeginRun(BlockFoundNotification ourBlock, out List<BlockFoundNotification> currentOurBlocks)
        {
            lock(sync)
            {
                if(running)
                {
                    if(ourBlock != null) pendingOurBlocks.Add(ourBlock);
                    else pendingNetworkRun = true;

                    currentOurBlocks = [];
                    return false;
                }

                running = true;
                currentOurBlocks = ourBlock != null ? [ourBlock] : [];
                return true;
            }
        }

        /// <summary>
        /// Called after each run completes.
        /// Returns true + the next OurBlock batch if pending work exists (caller must run again).
        /// Returns false when the queue is empty — the runner slot is released back to idle.
        /// </summary>
        public bool TryContinueRun(out List<BlockFoundNotification> nextOurBlocks)
        {
            lock(sync)
            {
                if(pendingOurBlocks.Count == 0 && !pendingNetworkRun)
                {
                    running = false;
                    nextOurBlocks = [];
                    return false;
                }

                nextOurBlocks = [..pendingOurBlocks];
                pendingOurBlocks.Clear();
                pendingNetworkRun = false;
                return true;
            }
        }
    }

    private readonly ConcurrentDictionary<string, PoolRunState> poolRunStates = new();

    // -- Trigger entry points --

    private void OnBlockFound(BlockFoundNotification notification)
    {
        if(!pools.TryGetValue(notification.PoolId, out var pool))
            return;

        Schedule(notification.PoolId, pool, ourBlock: notification);
    }

    private void OnNewChainHeight(NewChainHeightNotification notification)
    {
        if(!pools.TryGetValue(notification.PoolId, out var pool))
            return;

        // When OUR pool submitted the block that caused this height change, skip the network-path
        // run entirely.  The ShareRecorder will fire BlockFoundNotification after committing the
        // block to the database, and that notification will trigger the pool-path run (PATH 1)
        // with accurate data.
        //
        // Without this guard the classifier would run twice per pool-found block:
        //   • once here (network path) — block not yet in DB → stale / empty results
        //   • once via BlockFoundNotification (pool path) — block in DB → correct results
        if(notification.IsFromPoolBlockFind)
        {
            logger.Debug(() => $"[{notification.PoolId}] Skipping network-path classification for height {notification.BlockHeight} — triggered by our own block submission; BlockFoundNotification will follow");
            return;
        }

        Schedule(notification.PoolId, pool, ourBlock: null);
    }

    private void Schedule(string poolId, IMiningPool pool, BlockFoundNotification ourBlock)
    {
        var state = poolRunStates.GetOrAdd(poolId, _ => new PoolRunState());

        if(state.TryBeginRun(ourBlock, out var currentOurBlocks))
            _ = RunLoopAsync(pool, state, currentOurBlocks);
    }

    // -- Classification run loop --

    /// <summary>
    /// Runs the classify → notify cycle for one pool, consuming the pending queue until empty.
    /// Never runs two instances concurrently for the same pool.
    /// Sends exactly one WS message per iteration, AFTER classification completes.
    /// </summary>
    private async Task RunLoopAsync(IMiningPool pool, PoolRunState state, List<BlockFoundNotification> ourBlocks)
    {
        var poolId = pool.Config.Id;
        var currentOurBlocks = ourBlocks;

        while(true)
        {
            try
            {
                await ClassifyPoolBlocksAsync(pool, CancellationToken.None);

                // Send WS update AFTER classification — DB counts are fresh at this point.
                // PATH 1 (pool found block): blockfoundstats — this IS the block-found notification.
                // PATH 2 (network block):    chainheightstats — updates height/confirmations, no toast.
                if(currentOurBlocks.Count > 0)
                    await NotifyBlockFoundStatsAsync(poolId, pool);
                else
                    await NotifyChainHeightStatsAsync(poolId, pool);
            }
            catch(Exception ex)
            {
                logger.Error(ex, () => $"[{poolId}] Block classification failed");
            }

            if(!state.TryContinueRun(out currentOurBlocks))
                break;
        }
    }

    // -- Post-classification WS notifications --

    /// <summary>
    /// Sent when our pool finds a block. Fired AFTER ClassifyPoolBlocksAsync so all counts are accurate.
    /// The frontend shows the block-found toast on receipt of this message.
    /// </summary>
    private async Task NotifyBlockFoundStatsAsync(string poolId, IMiningPool pool)
    {
        try
        {
            var since24h   = clock.Now.AddHours(-24);
            var tTotal     = cf.Run(con => blockRepo.GetPoolBlockCountAsync(con, poolId, CancellationToken.None));
            var tConfirmed = cf.Run(con => blockRepo.GetTotalConfirmedBlocksAsync(con, poolId, CancellationToken.None));
            var tPending   = cf.Run(con => blockRepo.GetTotalPendingBlocksAsync(con, poolId, CancellationToken.None));
            var tOrphaned  = cf.Run(con => blockRepo.GetTotalOrphanedBlocksAsync(con, poolId, CancellationToken.None));
            var tLastTime  = cf.Run(con => blockRepo.GetLastPoolBlockTimeAsync(con, poolId, CancellationToken.None));
            var tBlocks24h = cf.Run(con => blockRepo.GetPoolBlockCountSinceAsync(con, poolId, since24h, CancellationToken.None));
            var tReward    = cf.Run(con => blockRepo.GetLastBlockRewardAsync(con, poolId, CancellationToken.None));
            await Task.WhenAll(tTotal, tConfirmed, tPending, tOrphaned, tLastTime, tBlocks24h, tReward);

            messageBus.NotifyBlockFoundStats(
                poolId,
                pool.NetworkStats.NetworkHashrate,
                pool.NetworkStats.NetworkDifficulty,
                pool.NetworkStats.BlockHeight,
                pool.NetworkStats.NetworkBlockHeight,
                pool.NetworkStats.LastNetworkBlockTime,
                tLastTime.Result,
                tBlocks24h.Result,
                tTotal.Result,
                tConfirmed.Result,
                tPending.Result,
                tOrphaned.Result,
                tReward.Result);
        }
        catch(Exception ex)
        {
            logger.Warn(ex, $"[{poolId}] Failed to push blockfoundstats");
        }
    }

    /// <summary>
    /// Sent when a network block arrives. Fired AFTER ClassifyPoolBlocksAsync so confirmation counts are accurate.
    /// </summary>
    private async Task NotifyChainHeightStatsAsync(string poolId, IMiningPool pool)
    {
        try
        {
            var tConfirmed = cf.Run(con => blockRepo.GetTotalConfirmedBlocksAsync(con, poolId, CancellationToken.None));
            var tPending   = cf.Run(con => blockRepo.GetTotalPendingBlocksAsync(con, poolId, CancellationToken.None));
            var tOrphaned  = cf.Run(con => blockRepo.GetTotalOrphanedBlocksAsync(con, poolId, CancellationToken.None));
            var tReward    = cf.Run(con => blockRepo.GetLastBlockRewardAsync(con, poolId, CancellationToken.None));
            await Task.WhenAll(tConfirmed, tPending, tOrphaned, tReward);

            messageBus.NotifyChainHeightStats(
                poolId,
                pool.NetworkStats.NetworkHashrate,
                pool.NetworkStats.NetworkDifficulty,
                pool.NetworkStats.BlockHeight,
                pool.NetworkStats.NetworkBlockHeight,
                pool.NetworkStats.LastNetworkBlockTime,
                tConfirmed.Result,
                tPending.Result,
                tOrphaned.Result,
                tReward.Result);
        }
        catch(Exception ex)
        {
            logger.Warn(ex, $"[{poolId}] Failed to push chainheightstats");
        }
    }

    // -- Pool lifecycle --

    private void AttachPool(IMiningPool pool)
    {
        pools.TryAdd(pool.Config.Id, pool);
    }

    private void OnPoolStatusNotification(PoolStatusNotification notification)
    {
        if(notification.Status == PoolStatus.Online)
            AttachPool(notification.Pool);
    }

    // -- Block classification logic --

    private async Task ClassifyPoolBlocksAsync(IMiningPool pool, CancellationToken ct)
    {
        var poolConfig = pool.Config;

        if(!poolConfig.Enabled || poolConfig.PaymentProcessing?.Enabled != true)
            return;

        var pendingBlocks = await cf.Run(con => blockRepo.GetPendingBlocksForPoolAsync(con, poolConfig.Id));

        if(pendingBlocks.Length == 0)
            return;

        logger.Info(() => $"[{poolConfig.Id}] Classifying {pendingBlocks.Length} pending block(s) at height {pool.NetworkStats?.BlockHeight}");

        var family = HandleFamilyOverride(poolConfig.Template.Family, poolConfig);

        var handlerImpl = ctx.Resolve<IEnumerable<Meta<Lazy<IPayoutHandler, CoinFamilyAttribute>>>>()
            .First(x => x.Value.Metadata.SupportedFamilies.Contains(family)).Value;

        var handler = handlerImpl.Value;
        await handler.ConfigureAsync(clusterConfig, poolConfig, ct);

        var scheme = ctx.ResolveKeyed<IPayoutScheme>(poolConfig.PaymentProcessing.PayoutScheme);

        var updatedBlocks = await handler.ClassifyBlocksAsync(pool, pendingBlocks, ct);

        if(!updatedBlocks.Any())
        {
            logger.Info(() => $"[{poolConfig.Id}] No block status changes");
            return;
        }

        foreach(var block in updatedBlocks.OrderBy(x => x.Created))
        {
            logger.Info(() => $"[{poolConfig.Id}] Block {block.BlockHeight} → {block.Status}");

            await cf.RunTx(async (con, tx) =>
            {
                if(!block.Effort.HasValue)
                    await CalculateBlockEffortAsync(pool, poolConfig, block, handler, ct);

                if(!block.MinerEffort.HasValue)
                    await CalculateMinerEffortAsync(pool, poolConfig, block, handler, ct);

                switch(block.Status)
                {
                    case BlockStatus.Confirmed:
                        var blockReward = await handler.UpdateBlockRewardBalancesAsync(con, tx, pool, block, ct);
                        await scheme.UpdateBalancesAsync(con, tx, pool, handler, block, blockReward, ct);
                        await blockRepo.UpdateBlockAsync(con, tx, block);
                        break;

                    case BlockStatus.Orphaned:
                    case BlockStatus.Pending:
                        await blockRepo.UpdateBlockAsync(con, tx, block);
                        break;
                }
            });

        }

        // WS notifications: only the 100 most recent blocks by height.
        // Classification and DB updates above are untouched — all blocks still processed.
        foreach(var block in updatedBlocks.OrderByDescending(x => x.BlockHeight).Take(100))
        {
            try
            {
                messageBus.NotifyBlockConfirmationProgress(poolConfig.Id, block, poolConfig.Template);
            }
            catch(Exception ex)
            {
                logger.Warn(ex, $"[{poolConfig.Id}] Failed to push block confirmation progress for block {block.BlockHeight}");
            }
        }
    }

    private static CoinFamily HandleFamilyOverride(CoinFamily family, PoolConfig pool) => family;

    private async Task CalculateBlockEffortAsync(IMiningPool pool, PoolConfig poolConfig, Block block, IPayoutHandler handler, CancellationToken ct)
    {
        var from = DateTime.MinValue;
        var to = block.Created;

        var lastBlock = await cf.Run(con => blockRepo.GetBlockBeforeAsync(con, poolConfig.Id, new[]
        {
            BlockStatus.Confirmed,
            BlockStatus.Orphaned,
            BlockStatus.Pending,
        }, block.Created));

        if(lastBlock != null)
            from = lastBlock.Created;

        block.Effort = await cf.Run(con =>
            shareRepo.GetEffectiveAccumulatedShareDifficultyBetweenAsync(con, pool.Config.Id, from, to, ct));

        if(block.Effort.HasValue)
            block.Effort = handler.AdjustBlockEffort(block.Effort.Value);
    }

    private async Task CalculateMinerEffortAsync(IMiningPool pool, PoolConfig poolConfig, Block block, IPayoutHandler handler, CancellationToken ct)
    {
        var from = DateTime.MinValue;
        var to = block.Created;
        var miner = block.Miner;

        var lastBlock = await cf.Run(con => blockRepo.GetBlockBeforeAsync(con, poolConfig.Id, new[]
        {
            BlockStatus.Confirmed,
            BlockStatus.Orphaned,
            BlockStatus.Pending,
        }, block.Created));

        if(lastBlock != null)
            from = lastBlock.Created;

        block.MinerEffort = await cf.Run(con =>
            shareRepo.GetMinerShareDifficultyBetweenAsync(con, pool.Config.Id, miner, from, to, ct));

        if(block.MinerEffort.HasValue)
            block.MinerEffort = handler.AdjustBlockEffort(block.MinerEffort.Value);
    }

    // -- Service lifecycle --

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            disposables.Add(messageBus.Listen<PoolStatusNotification>()
                .ObserveOn(TaskPoolScheduler.Default)
                .Subscribe(OnPoolStatusNotification));

            // PATH 1: our pool found a block — classify then send blockfoundstats
            disposables.Add(messageBus.Listen<BlockFoundNotification>()
                .ObserveOn(TaskPoolScheduler.Default)
                .Subscribe(OnBlockFound));

            // PATH 2: network produced a new block — classify then send chainheightstats
            disposables.Add(messageBus.Listen<NewChainHeightNotification>()
                .ObserveOn(TaskPoolScheduler.Default)
                .Subscribe(OnNewChainHeight));

            logger.Info(() => "Online");

            await Task.Delay(Timeout.Infinite, ct);

            logger.Info(() => "Offline");
        }
        catch(OperationCanceledException)
        {
            // normal shutdown
        }
        finally
        {
            disposables.Dispose();
        }
    }
}
