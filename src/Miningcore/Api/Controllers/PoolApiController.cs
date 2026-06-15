using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Net;
using Autofac;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Miningcore.Api.Extensions;
using Miningcore.Api.Responses;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Mining;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Model.Projections;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using NLog;

namespace Miningcore.Api.Controllers;

[Route("api/pools")]
[ApiController]
public class PoolApiController : ApiControllerBase
{
    public PoolApiController(IComponentContext ctx, IActionDescriptorCollectionProvider _adcp, IMemoryCache memoryCache) : base(ctx)
    {
        statsRepo = ctx.Resolve<IStatsRepository>();
        blocksRepo = ctx.Resolve<IBlockRepository>();
        minerRepo = ctx.Resolve<IMinerRepository>();
        shareRepo = ctx.Resolve<IShareRepository>();
        paymentsRepo = ctx.Resolve<IPaymentRepository>();
        clock = ctx.Resolve<IMasterClock>();
        pools = ctx.Resolve<ConcurrentDictionary<string, IMiningPool>>();
        adcp = _adcp;
        cache = memoryCache;
    }

    private readonly IStatsRepository statsRepo;
    private readonly IBlockRepository blocksRepo;
    private readonly IPaymentRepository paymentsRepo;
    private readonly IMinerRepository minerRepo;
    private readonly IShareRepository shareRepo;
    private readonly IMasterClock clock;
    private readonly IActionDescriptorCollectionProvider adcp;
    private readonly ConcurrentDictionary<string, IMiningPool> pools;
    private readonly IMemoryCache cache;

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

    // TTL is half the StatsRecorder update interval (default 120 s).
    // Data is never older than 60 s, and DB load drops from N queries/req
    // to 1 batch per minute regardless of concurrent HTTP traffic.
    private static readonly TimeSpan CachePoolDetailTtl = TimeSpan.FromSeconds(60);

    private MemoryCacheEntryOptions PoolDetailCacheOptions() => new MemoryCacheEntryOptions()
        .SetAbsoluteExpiration(CachePoolDetailTtl)
        .SetPriority(CacheItemPriority.High)
        .SetSize(1)
        .RegisterPostEvictionCallback((key, _, reason, _) =>
            logger.Debug(() => $"Cache evicted [{key}]: {reason}"));

    private static string CacheKeyPoolDetail(string poolId) => $"api:pools:detail:{poolId}";

    #region Actions

    [HttpGet("/api/pools-list")]
    public GetPoolsResponse Get()
    {
        // Pure config read — no DB queries.
        // Returns only id + coin so the frontend can populate the pool selector.
        // Full stats are at GET /api/pools/{id}.
        return new GetPoolsResponse
        {
            Pools = clusterConfig.Pools
                .Where(x => x.Enabled)
                .Select(config => new PoolListItem
                {
                    Id   = config.Id,
                    Coin = mapper.Map<ApiCoinConfig>(config.Template)
                })
                .ToArray()
        };
    }

    [HttpGet("/api/help")]
    public ActionResult GetHelp()
    {
        var tmp = adcp.ActionDescriptors.Items
            .Where(x => x.AttributeRouteInfo != null)
            .Select(x =>
            {
                // Get and pad http method
                var method = x.ActionConstraints?.OfType<HttpMethodActionConstraint>().FirstOrDefault()?.HttpMethods.First();
                method = $"{method,-5}";

                return $"{method} -> {x.AttributeRouteInfo.Template}";
            });

        // convert curly braces
        var result = string.Join("\n", tmp).Replace("{", "<").Replace("}", ">") + "\n";

        return Content(result);
    }

    [HttpGet("/api/health-check")]
    public ActionResult GetHealthCheck()
    {
        return Content("👍");
    }

    [HttpGet("{poolId}")]
    public async Task<GetPoolResponse> GetPoolInfoAsync(string poolId, CancellationToken ct)
    {
        var pool = GetPool(poolId);

        var cacheKey = CacheKeyPoolDetail(pool.Id);

        if(cache.TryGetValue(cacheKey, out GetPoolResponse cached))
            return cached;

        // load stats
        var stats = await cf.Run(con => statsRepo.GetLastPoolStatsAsync(con, pool.Id, ct));

        // get pool
        pools.TryGetValue(pool.Id, out var poolInstance);

        var response = new GetPoolResponse
        {
            Pool = pool.ToPoolInfo(mapper, stats, poolInstance)
        };

        // enrich
        response.Pool.TotalPaid = await cf.Run(con => statsRepo.GetTotalPoolPaymentsAsync(con, pool.Id, ct));
        response.Pool.TotalBlocks = await cf.Run(con => blocksRepo.GetPoolBlockCountAsync(con, pool.Id, ct));
        response.Pool.TotalConfirmedBlocks = await cf.Run(con => blocksRepo.GetTotalConfirmedBlocksAsync(con, pool.Id, ct));
        response.Pool.TotalPendingBlocks = await cf.Run(con => blocksRepo.GetTotalPendingBlocksAsync(con, pool.Id, ct));
        response.Pool.TotalOrphanedBlocks = await cf.Run(con => blocksRepo.GetTotalOrphanedBlocksAsync(con, pool.Id, ct));
        response.Pool.BlockReward = await cf.Run(con => blocksRepo.GetLastBlockRewardAsync(con, pool.Id, ct));
        var lastBlockTime = await cf.Run(con => blocksRepo.GetLastPoolBlockTimeAsync(con, pool.Id, ct));
        response.Pool.LastPoolBlockTime = lastBlockTime;

        var payoutConfig = pool.PaymentProcessing;
        response.Pool.PaymentProcessing.PayoutSchemeConfig = payoutConfig?.PayoutSchemeConfig?.ToObject<ApiPoolPayoutSchemeConfig>() ?? new ApiPoolPayoutSchemeConfig();

        // strip fields that don't apply to the active payout scheme:
        // - BlockFinderPercentage only applies to PPLNSBF
        // - Factor only applies to PPLNS/PPLNSBF
        var schemeConfig = response.Pool.PaymentProcessing.PayoutSchemeConfig;

        switch(payoutConfig?.PayoutScheme)
        {
            case PayoutScheme.PPLNSBF:
                break;

            case PayoutScheme.PPLNS:
                schemeConfig.BlockFinderPercentage = null;
                break;

            default:
                schemeConfig.BlockFinderPercentage = null;
                schemeConfig.Factor = null;
                break;
        }

        // new stats fields
        response.Pool.PaymentProcessing.PaymentIntervalSeconds = clusterConfig.PaymentProcessing?.Interval ?? 0;
        response.Pool.TotalPaymentsCount = await cf.Run(con => paymentsRepo.GetTotalPoolPaymentsCountAsync(con, pool.Id, ct));
        response.Pool.LastPaymentTime = await cf.Run(con => paymentsRepo.GetLastPoolPaymentTimeAsync(con, pool.Id, ct));
        response.Pool.Blocks24h = await cf.Run(con => blocksRepo.GetPoolBlockCountSinceAsync(con, pool.Id, clock.Now.AddHours(-24), ct));

        // workers online = distinct workers active in last 30 min
        // workers offline = distinct workers active in last 24h but not in last 30 min
        var workersOnline = await cf.Run(con => statsRepo.GetPoolWorkerCountAsync(con, pool.Id, clock.Now.AddMinutes(-30), ct));
        var workersTotal24h = await cf.Run(con => statsRepo.GetPoolWorkerCountAsync(con, pool.Id, clock.Now.AddHours(-24), ct));
        response.Pool.WorkersOnline = workersOnline;
        response.Pool.WorkersOffline = Math.Max(0, workersTotal24h - workersOnline);

        if(lastBlockTime.HasValue)
        {
            var startTime = lastBlockTime.Value;
            var poolEffort = await cf.Run(con => shareRepo.GetEffortBetweenCreatedAsync(con, pool.Id, poolInstance?.ShareMultiplier ?? 1, startTime, clock.Now, ct));

            if(poolEffort.HasValue)
                response.Pool.PoolEffort = poolEffort.Value;
        }

        if(!ct.IsCancellationRequested)
            cache.Set(cacheKey, response, PoolDetailCacheOptions());

        return response;
    }

    [HttpGet("{poolId}/performance")]
    public async Task<GetPoolStatsResponse> GetPoolPerformanceAsync(string poolId)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        // Always: last 24 hours, hourly buckets.
        var end   = clock.Now;
        var start = end.AddDays(-1);

        var stats = await cf.Run(con => statsRepo.GetPoolPerformanceBetweenAsync(con, pool.Id, SampleInterval.Hour, start, end, ct));

        return new GetPoolStatsResponse
        {
            Stats = stats.Select(mapper.Map<AggregatedPoolStats>).ToArray()
        };
    }

    [HttpGet("{poolId}/blocks")]
    public async Task<Responses.Block[]> PagePoolBlocksAsync(
        string poolId, [FromQuery] BlockStatus[] state = null)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        const int page = 0;
        const int pageSize = 100;

        var blockStates = state is { Length: > 0 } ?
            state :
            new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

        var blocks = (await cf.Run(con => blocksRepo.PageBlocksAsync(con, pool.Id, blockStates, page, pageSize, ct)))
            .Select(mapper.Map<Responses.Block>)
            .ToArray();

        // enrich blocks
        var blockInfobaseDict = pool.Template.ExplorerBlockLinks;

        foreach(var block in blocks)
        {
            // Public endpoint, no auth — mask payout address before it leaves the API.
            // block here is the freshly-mapped Responses.Block DTO, not the persistence entity,
            // so mutating it does not affect the stored block record.
            block.Miner = block.Miner.MaskAddress();

            // compute infoLink
            if(blockInfobaseDict != null)
            {
                blockInfobaseDict.TryGetValue(!string.IsNullOrEmpty(block.Type) ? block.Type : "block", out var blockInfobaseUrl);

                if(!string.IsNullOrEmpty(blockInfobaseUrl))
                {
                    if(blockInfobaseUrl.Contains(CoinMetaData.BlockHeightPH))
                        block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                    else if(blockInfobaseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                        block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
                }
            }
        }

        return blocks;
    }

    [HttpGet("/api/v2/pools/{poolId}/blocks")]
    public async Task<PagedResultResponse<Responses.Block[]>> PagePoolBlocksV2Async(
        string poolId, [FromQuery] BlockStatus[] state = null)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        const int page = 0;
        const int pageSize = 100;

        var blockStates = state is { Length: > 0 } ?
            state :
            new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

        uint itemCount = await cf.Run(con => blocksRepo.GetPoolBlockCountAsync(con, poolId, ct));
        const uint pageCount = 1;

        var blocks = (await cf.Run(con => blocksRepo.PageBlocksAsync(con, pool.Id, blockStates, page, pageSize, ct)))
            .Select(mapper.Map<Responses.Block>)
            .ToArray();

        // enrich blocks
        var blockInfobaseDict = pool.Template.ExplorerBlockLinks;

        foreach(var block in blocks)
        {
            // Public endpoint, no auth — mask payout address before it leaves the API.
            // block here is the freshly-mapped Responses.Block DTO, not the persistence entity,
            // so mutating it does not affect the stored block record.
            block.Miner = block.Miner.MaskAddress();

            // compute infoLink
            if(blockInfobaseDict != null)
            {
                blockInfobaseDict.TryGetValue(!string.IsNullOrEmpty(block.Type) ? block.Type : "block", out var blockInfobaseUrl);

                if(!string.IsNullOrEmpty(blockInfobaseUrl))
                {
                    if(blockInfobaseUrl.Contains(CoinMetaData.BlockHeightPH))
                        block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                    else if(blockInfobaseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                        block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
                }
            }
        }

        var response = new PagedResultResponse<Responses.Block[]>(blocks, itemCount, pageCount);
        return response;
    }

    [HttpGet("{poolId}/miners/{address}")]
    public async Task<Responses.MinerStats> GetMinerInfoAsync(
        string poolId, string address)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        var statsResult = await cf.RunTx((con, tx) =>
            statsRepo.GetMinerStatsAsync(con, tx, pool.Id, address, ct), true, IsolationLevel.Serializable);

        Responses.MinerStats stats = null;

        if(statsResult != null)
        {
            stats = mapper.Map<Responses.MinerStats>(statsResult);

            // pre-multiply pending shares to cause less confusion with users
            if(pool.Template.Family == CoinFamily.Bitcoin)
                stats.PendingShares *= pool.Template.As<BitcoinTemplate>().ShareMultiplier;

            // optional fields
            if(statsResult.LastPayment != null)
            {
                // Set timestamp of last payment
                stats.LastPayment = statsResult.LastPayment.Created;

                // Compute info link
                var baseUrl = pool.Template.ExplorerTxLink;
                if(!string.IsNullOrEmpty(baseUrl))
                    stats.LastPaymentLink = string.Format(baseUrl, statsResult.LastPayment.TransactionConfirmationData);
            }

            var lastBlockTime = await cf.Run(con => blocksRepo.GetLastMinerBlockTimeAsync(con, pool.Id, address, ct));
            if(lastBlockTime.HasValue)
            {
                var startTime = lastBlockTime.Value;
                var minerEffort = await cf.Run(con => shareRepo.GetMinerEffortBetweenCreatedAsync(con, pool.Id, address, startTime, clock.Now, ct));

                if(minerEffort.HasValue)
                    stats.MinerEffort = minerEffort.Value;
            }

            stats.TotalConfirmedBlocks = await cf.Run(con => statsRepo.GetMinerTotalConfirmedBlocksAsync(con, pool.Id, address, ct));
            stats.TotalPendingBlocks = await cf.Run(con => statsRepo.GetMinerTotalPendingBlocksAsync(con, pool.Id, address, ct));
            stats.TotalOrphanedBlocks = await cf.Run(con => statsRepo.GetMinerTotalOrphanedBlocksAsync(con, pool.Id, address, ct));

            // workers online = distinct workers active in last 30 min
            // workers offline = distinct workers active in last 24h but not in last 30 min
            var minerWorkersOnline = await cf.Run(con => statsRepo.GetMinerWorkerCountAsync(con, pool.Id, address, clock.Now.AddMinutes(-30), ct));
            var minerWorkersTotal24h = await cf.Run(con => statsRepo.GetMinerWorkerCountAsync(con, pool.Id, address, clock.Now.AddHours(-24), ct));
            stats.WorkersOnline = minerWorkersOnline;
            stats.WorkersOffline = Math.Max(0, minerWorkersTotal24h - minerWorkersOnline);
        }

        return stats;
    }

    [HttpGet("{poolId}/miners/{address}/blocks")]
    public async Task<Responses.Block[]> PageMinerBlocksAsync(
        string poolId, string address, [FromQuery] BlockStatus[] state = null)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        const int page = 0;
        const int pageSize = 20;

        var blockStates = state is { Length: > 0 } ?
            state :
            new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

        var blocks = (await cf.Run(con => blocksRepo.PageMinerBlocksAsync(con, pool.Id, address, blockStates, page, pageSize, ct)))
            .Select(mapper.Map<Responses.Block>)
            .ToArray();

        // enrich blocks
        var blockInfobaseDict = pool.Template.ExplorerBlockLinks;

        foreach(var block in blocks)
        {
            // compute infoLink
            if(blockInfobaseDict != null)
            {
                blockInfobaseDict.TryGetValue(!string.IsNullOrEmpty(block.Type) ? block.Type : "block", out var blockInfobaseUrl);

                if(!string.IsNullOrEmpty(blockInfobaseUrl))
                {
                    if(blockInfobaseUrl.Contains(CoinMetaData.BlockHeightPH))
                        block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                    else if(blockInfobaseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                        block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
                }
            }
        }

        return blocks;
    }

    [HttpGet("/api/v2/pools/{poolId}/miners/{address}/blocks")]
    public async Task<PagedResultResponse<Responses.Block[]>> PageMinerBlocksV2Async(
        string poolId, string address, [FromQuery] BlockStatus[] state = null)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        const int page = 0;
        const int pageSize = 20;
        const uint pageCount = 1;

        var blockStates = state is { Length: > 0 } ?
            state :
            new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

        uint itemCount = await cf.Run(con => blocksRepo.GetMinerBlockCountAsync(con, poolId, address, ct));

        var blocks = (await cf.Run(con => blocksRepo.PageMinerBlocksAsync(con, pool.Id, address, blockStates, page, pageSize, ct)))
            .Select(mapper.Map<Responses.Block>)
            .ToArray();

        // enrich blocks
        var blockInfobaseDict = pool.Template.ExplorerBlockLinks;

        foreach(var block in blocks)
        {
            // compute infoLink
            if(blockInfobaseDict != null)
            {
                blockInfobaseDict.TryGetValue(!string.IsNullOrEmpty(block.Type) ? block.Type : "block", out var blockInfobaseUrl);

                if(!string.IsNullOrEmpty(blockInfobaseUrl))
                {
                    if(blockInfobaseUrl.Contains(CoinMetaData.BlockHeightPH))
                        block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                    else if(blockInfobaseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                        block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
                }
            }
        }

        var response = new PagedResultResponse<Responses.Block[]>(blocks, itemCount, pageCount);
        return response;
    }

    [HttpGet("{poolId}/miners/{address}/payments")]
    public async Task<Responses.Payment[]> PageMinerPaymentsAsync(
        string poolId, string address)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        const int page = 0;
        const int pageSize = 20;

        var payments = (await cf.Run(con => paymentsRepo.PagePaymentsAsync(
                con, pool.Id, address, page, pageSize, ct)))
            .Select(mapper.Map<Responses.Payment>)
            .ToArray();

        // enrich payments
        var txInfobaseUrl = pool.Template.ExplorerTxLink;
        var addressInfobaseUrl = pool.Template.ExplorerAccountLink;

        foreach(var payment in payments)
        {
            // compute transaction infoLink
            if(!string.IsNullOrEmpty(txInfobaseUrl))
                payment.TransactionInfoLink = string.Format(txInfobaseUrl, payment.TransactionConfirmationData);

            // pool wallet link
            if(!string.IsNullOrEmpty(addressInfobaseUrl))
                payment.AddressInfoLink = string.Format(addressInfobaseUrl, payment.Address);
        }

        return payments;
    }

    [HttpGet("/api/v2/pools/{poolId}/miners/{address}/payments")]
    public async Task<PagedResultResponse<Responses.Payment[]>> PageMinerPaymentsV2Async(
        string poolId, string address)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        const int page = 0;
        const int pageSize = 20;
        const uint pageCount = 1;

        uint itemCount = await cf.Run(con => paymentsRepo.GetPaymentsCountAsync(con, poolId, address, ct));

        var payments = (await cf.Run(con => paymentsRepo.PagePaymentsAsync(
                con, pool.Id, address, page, pageSize, ct)))
            .Select(mapper.Map<Responses.Payment>)
            .ToArray();

        // enrich payments
        var txInfobaseUrl = pool.Template.ExplorerTxLink;
        var addressInfobaseUrl = pool.Template.ExplorerAccountLink;

        foreach(var payment in payments)
        {
            // compute transaction infoLink
            if(!string.IsNullOrEmpty(txInfobaseUrl))
                payment.TransactionInfoLink = string.Format(txInfobaseUrl, payment.TransactionConfirmationData);

            // pool wallet link
            if(!string.IsNullOrEmpty(addressInfobaseUrl))
                payment.AddressInfoLink = string.Format(addressInfobaseUrl, payment.Address);
        }

        var response = new PagedResultResponse<Responses.Payment[]>(payments, itemCount, pageCount);
        return response;
    }

    [HttpGet("{poolId}/miners/{address}/settings")]
    public async Task<Responses.MinerSettings> GetMinerSettingsAsync(string poolId, string address)
    {
        var pool = GetPool(poolId);

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);


        var result = await cf.Run(con => minerRepo.GetSettingsAsync(con, null, pool.Id, address));

        if(result == null)
            return new Responses.MinerSettings { PaymentThreshold = 0 };

        return mapper.Map<Responses.MinerSettings>(result);
    }

    [HttpPost("{poolId}/miners/{address}/settings")]
    public async Task<Responses.MinerSettings> SetMinerSettingsAsync(string poolId, string address,
        [FromBody] Requests.UpdateMinerSettingsRequest request, CancellationToken ct)
    {
        var pool = GetPool(poolId);

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);


        if(request?.Settings == null)
            throw new ApiException("Invalid or missing settings", HttpStatusCode.BadRequest);

        if(string.IsNullOrEmpty(request.Password))
            throw new ApiException("Invalid or missing password", HttpStatusCode.BadRequest);

        if(!System.Text.RegularExpressions.Regex.IsMatch(request.Password, @"^[A-Za-z0-9!@#$%^&*_.\-]{1,64}$"))
            throw new ApiException("Password contains invalid characters", HttpStatusCode.BadRequest);

        var passwords = await cf.Run(con => shareRepo.GetRecentlyUsedPasswordsAsync(con, null, poolId, address, ct));

        if(passwords == null || passwords.Length == 0)
            throw new ApiException("Address not recently used for mining or no password set", HttpStatusCode.NotFound);

        if(!passwords.Any(x => x == request.Password))
            throw new ApiException("Password does not match", HttpStatusCode.Forbidden);

        // map settings
        var mapped = mapper.Map<Persistence.Model.MinerSettings>(request.Settings);

        // clamp limit
        if(pool.PaymentProcessing != null)
            mapped.PaymentThreshold = Math.Max(mapped.PaymentThreshold, pool.PaymentProcessing.MinimumPayment);

        mapped.PoolId = pool.Id;
        mapped.Address = address;

        // finally update the settings
        return await cf.RunTx(async (con, tx) =>
        {
            await minerRepo.UpdateSettingsAsync(con, tx, mapped);

            logger.Info(() => $"Updated settings for pool {pool.Id}, miner {address}");

            var result = await minerRepo.GetSettingsAsync(con, tx, mapped.PoolId, mapped.Address);
            return mapper.Map<Responses.MinerSettings>(result);
        });
    }

    #endregion // Actions

}
