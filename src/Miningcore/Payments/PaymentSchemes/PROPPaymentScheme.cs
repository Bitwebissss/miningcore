using System.Data;
using System.Data.Common;
using System.Net.Sockets;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Mining;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Util;
using NLog;
using Polly;
using Polly.Retry;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Payments.PaymentSchemes;

/// <summary>
/// PROP payout scheme implementation
/// </summary>
// ReSharper disable once InconsistentNaming
public class PROPPaymentScheme : IPayoutScheme
{
    public PROPPaymentScheme(
        IConnectionFactory cf,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        IBalanceRepository balanceRepo)
    {
        Contract.RequiresNonNull(cf);
        Contract.RequiresNonNull(shareRepo);
        Contract.RequiresNonNull(blockRepo);
        Contract.RequiresNonNull(balanceRepo);

        this.cf = cf;
        this.shareRepo = shareRepo;
        this.blockRepo = blockRepo;
        this.balanceRepo = balanceRepo;

        BuildFaultHandlingPolicy();
    }

    private readonly IBalanceRepository balanceRepo;
    private readonly IBlockRepository blockRepo;
    private readonly IConnectionFactory cf;
    private readonly IShareRepository shareRepo;
    private static readonly ILogger logger = LogManager.GetLogger("PROP Payment");

    private const int RetryCount = 4;
    private ResiliencePipeline shareReadFaultPolicy;

    private class Config
    {
        public decimal Factor { get; set; }
    }

    #region IPayoutScheme

    public async Task UpdateBalancesAsync(IDbConnection con, IDbTransaction tx, IMiningPool pool, IPayoutHandler payoutHandler,
        Block block, decimal blockReward, CancellationToken ct)
    {
        var poolConfig = pool.Config;
        var shares = new Dictionary<string, double>();
        var rewards = new Dictionary<string, decimal>();
        var shareCutOffDate = await CalculateRewardsAsync(pool, payoutHandler, block, blockReward, shares, rewards, ct);

        // update balances
        foreach(var address in rewards.Keys)
        {
            var amount = rewards[address];

            if(amount > 0)
            {
                logger.Info(() => $"Crediting {address} with {payoutHandler.FormatAmount(amount)} for {FormatUtil.FormatQuantity(shares[address])} ({shares[address]}) shares for block {block.BlockHeight}");
                await balanceRepo.AddAmountAsync(con, tx, poolConfig.Id, address, amount, $"Reward for {FormatUtil.FormatQuantity(shares[address])} shares for block {block.BlockHeight}");
            }
        }

        // delete discarded shares
        if(shareCutOffDate.HasValue)
        {
            var cutOffCount = await shareRepo.CountSharesBeforeAsync(con, tx, poolConfig.Id, shareCutOffDate.Value, ct);

            if(cutOffCount > 0)
            {
                await LogDiscardedSharesAsync(ct, poolConfig, block, shareCutOffDate.Value);

                logger.Info(() => $"Deleting {cutOffCount} discarded shares before {shareCutOffDate.Value:O}");
                await shareRepo.DeleteSharesBeforeAsync(con, tx, poolConfig.Id, shareCutOffDate.Value, ct);
            }
        }

        // diagnostics
        var totalShareCount = shares.Values.ToList().Sum(x => new decimal(x));
        var totalRewards = rewards.Values.ToList().Sum(x => x);

        if(totalRewards > 0)
            logger.Info(() => $"{FormatUtil.FormatQuantity((double) totalShareCount)} ({Math.Round(totalShareCount, 2)}) shares contributed to a total payout of {payoutHandler.FormatAmount(totalRewards)} ({totalRewards / blockReward * 100:0.00}% of block reward) to {rewards.Keys.Count} addresses");
    }

    private async Task LogDiscardedSharesAsync(CancellationToken ct, PoolConfig poolConfig, Block block, DateTime value)
    {
        var before = value;
        var pageSize = 100000;
        var currentPage = 0;
        var shares = new Dictionary<string, double>();

        while(true)
        {
            logger.Info(() => $"Fetching page {currentPage} of discarded shares for pool {poolConfig.Id}, block {block.BlockHeight}");

            var page = await shareReadFaultPolicy.ExecuteAsync(async _ =>
                await cf.Run(con => shareRepo.ReadSharesBeforeAsync(con, poolConfig.Id, before, false, pageSize, ct)), ct);

            currentPage++;

            for(var i = 0; i < page.Length; i++)
            {
                var share = page[i];
                var address = share.Miner;

                // record attributed shares for diagnostic purposes
                shares[address] = shares.TryGetValue(address, out var e1) ? e1 + share.Difficulty : share.Difficulty;
            }

            if(page.Length < pageSize)
                break;

            before = page[^1].Created;
        }

        if(shares.Keys.Count > 0)
        {
            // sort addresses by shares
            var addressesByShares = shares.Keys.OrderByDescending(x => shares[x]);

            logger.Info(() => $"{FormatUtil.FormatQuantity(shares.Values.Sum())} ({shares.Values.Sum()}) total discarded shares, block {block.BlockHeight}");

            foreach(var address in addressesByShares)
                logger.Info(() => $"{address} = {FormatUtil.FormatQuantity(shares[address])} ({shares[address]}) discarded shares, block {block.BlockHeight}");
        }
    }

    #endregion // IPayoutScheme

    private async Task<DateTime?> CalculateRewardsAsync(IMiningPool pool, IPayoutHandler payoutHandler, Block block, decimal blockReward,
        Dictionary<string, double> shares, Dictionary<string, decimal> rewards, CancellationToken ct)
    {
        var poolConfig = pool.Config;
        var done = false;
        var before = block.Created;
        var inclusive = true;
        var pageSize = 100000;
        var currentPage = 0;
        var accumulatedScore = 0.0m;
        var blockRewardRemaining = blockReward;
        DateTime? shareCutOffDate = null;
        var scores = new Dictionary<string, decimal>();

        while(!done && !ct.IsCancellationRequested)
        {
            logger.Info(() => $"Fetching page {currentPage} of shares for pool {poolConfig.Id}, block {block.BlockHeight}");

            var page = await shareReadFaultPolicy.ExecuteAsync(async _ =>
                await cf.Run(con => shareRepo.ReadSharesBeforeAsync(con, poolConfig.Id, before, inclusive, pageSize, ct)), ct);

            inclusive = false;
            currentPage++;

            for(var i = 0; i < page.Length; i++)
            {
                var share = page[i];
                var address = share.Miner;
                var shareDiffAdjusted = payoutHandler.AdjustShareDifficulty(share.Difficulty);

                // record attributed shares for diagnostic purposes
                shares[address] = shares.TryGetValue(address, out var e2) ? e2 + shareDiffAdjusted : shareDiffAdjusted;

                // Guard: never divide by a corrupted/zero NetworkDifficulty. Skip the
                // share rather than substitute a value — a substitute would inflate
                // this miner's score at everyone else's expense.
                if(share.NetworkDifficulty <= 0 || double.IsNaN(share.NetworkDifficulty) || double.IsInfinity(share.NetworkDifficulty))
                {
                    logger.Warn(() => $"Excluding share from {address} (pool {poolConfig.Id}, block {block.BlockHeight}) from PROP scoring: invalid NetworkDifficulty ({share.NetworkDifficulty})");
                    continue;
                }

                var score = (decimal) (shareDiffAdjusted / share.NetworkDifficulty);

                scores[address] = scores.TryGetValue(address, out var e3) ? e3 + score : score;

                accumulatedScore += score;

                // set the cutoff date to clean up old shares after a successful payout
                if(shareCutOffDate == null || share.Created > shareCutOffDate)
                    shareCutOffDate = share.Created;
            }

            if(page.Length < pageSize)
            {
                done = true;
                break;
            }

            before = page[^1].Created;
            done = page.Length <= 0;
        }

        if(accumulatedScore > 0)
        {
            var rewardPerScorePoint = blockReward / accumulatedScore;

            // build rewards for all addresses that contributed to the round
            // single O(n) pass — Dictionary keys are already unique, the previous
            // nested loop (Distinct + Where per key) was an unnecessary O(n²) scan
            foreach(var kvp in scores)
            {
                var reward = kvp.Value * rewardPerScorePoint;

                if(reward > 0)
                {
                    // accumulate miner reward
                    rewards[kvp.Key] = rewards.TryGetValue(kvp.Key, out var e4) ? e4 + reward : reward;
                }

                blockRewardRemaining -= reward;
            }
        }

        // this should never happen
        if(blockRewardRemaining <= 0 && !done)
            throw new OverflowException("blockRewardRemaining < 0");

        // accumulatedScore = Σ(shareDiff / networkDiff) for the round.
        // A value of 1.0 means miners expended exactly one expected-block's worth of work.
        // Values >1 = unlucky round, <1 = lucky round.  Multiplying by 100 and labelling
        // it "%" was misleading; use "effort" instead.
        logger.Info(() => $"Balance-calculation for pool {poolConfig.Id}, block {block.BlockHeight} completed with accumulated score {accumulatedScore:0.####} (round effort ≈ {accumulatedScore * 100:0.#}% of 1 expected block)");

        return shareCutOffDate;
    }

    private void BuildFaultHandlingPolicy()
    {
        shareReadFaultPolicy = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<DbException>()
                    .Handle<SocketException>()
                    .Handle<TimeoutException>(),
                MaxRetryAttempts = RetryCount,
                Delay = TimeSpan.Zero,
                OnRetry = args =>
                {
                    OnPolicyRetry(args.Outcome.Exception, args.AttemptNumber + 1, null);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    private static void OnPolicyRetry(Exception ex, int retry, object context)
    {
        logger.Warn(() => $"Retry {retry} due to {ex.Source}: {ex.GetType().Name} ({ex.Message})");
    }
}
