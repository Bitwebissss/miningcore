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
using NLog;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Payments;

/// <summary>
/// Coin agnostic payment processor
/// </summary>
public class PayoutManager : BackgroundService
{
    public PayoutManager(IComponentContext ctx,
        IConnectionFactory cf,
        IBalanceRepository balanceRepo,
        ClusterConfig clusterConfig,
        IMessageBus messageBus)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(cf);
        Contract.RequiresNonNull(balanceRepo);
        Contract.RequiresNonNull(messageBus);

        this.ctx = ctx;
        this.cf = cf;
        this.balanceRepo = balanceRepo;
        this.messageBus = messageBus;
        this.clusterConfig = clusterConfig;

        interval = TimeSpan.FromSeconds(clusterConfig.PaymentProcessing.Interval > 0 ?
            clusterConfig.PaymentProcessing.Interval : 600);
    }

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private readonly IBalanceRepository balanceRepo;
    private readonly IConnectionFactory cf;
    private readonly IComponentContext ctx;
    private readonly IMessageBus messageBus;
    private readonly TimeSpan interval;
    private readonly ConcurrentDictionary<string, IMiningPool> pools = new();
    private readonly ClusterConfig clusterConfig;
    private readonly CompositeDisposable disposables = new();

#if !DEBUG
    private static readonly TimeSpan initialRunDelay = TimeSpan.FromMinutes(1);
#else
    private static readonly TimeSpan initialRunDelay = TimeSpan.FromSeconds(15);
#endif

    private void AttachPool(IMiningPool pool)
    {
        pools.TryAdd(pool.Config.Id, pool);
    }

    private void OnPoolStatusNotification(PoolStatusNotification notification)
    {
        if(notification.Status == PoolStatus.Online)
            AttachPool(notification.Pool);
    }

    private async Task ProcessPoolsAsync(CancellationToken ct)
    {
        foreach(var pool in pools.Values.ToArray().Where(x => x.Config.Enabled && x.Config.PaymentProcessing.Enabled))
        {
            var poolConfig = pool.Config;

            logger.Info(() => $"Processing payments for pool {poolConfig.Id}");

            try
            {
                var family = HandleFamilyOverride(poolConfig.Template.Family, poolConfig);

                // resolve payout handler
                var handlerImpl = ctx.Resolve<IEnumerable<Meta<Lazy<IPayoutHandler, CoinFamilyAttribute>>>>()
                    .First(x => x.Value.Metadata.SupportedFamilies.Contains(family)).Value;

                var handler = handlerImpl.Value;
                await handler.ConfigureAsync(clusterConfig, poolConfig, ct);

                // Block classification and balance crediting is handled by BlockClassifierService.
                // PayoutManager only reads already-credited balances and executes sendmany.
                await PayoutPoolBalancesAsync(pool, poolConfig, handler, ct);
            }

            catch(InvalidOperationException ex)
            {
                logger.Error(ex.InnerException ?? ex, () => $"[{poolConfig.Id}] Payment processing failed");
            }

            catch(AggregateException ex)
            {
                switch(ex.InnerException)
                {
                    case HttpRequestException httpEx:
                        logger.Error(() => $"[{poolConfig.Id}] Payment processing failed: {httpEx.Message}");
                        break;

                    default:
                        logger.Error(ex.InnerException, () => $"[{poolConfig.Id}] Payment processing failed");
                        break;
                }
            }

            catch(Exception ex)
            {
                logger.Error(ex, () => $"[{poolConfig.Id}] Payment processing failed");
            }
        }
    }

    private static CoinFamily HandleFamilyOverride(CoinFamily family, PoolConfig pool) => family;


    private async Task PayoutPoolBalancesAsync(IMiningPool pool, PoolConfig config, IPayoutHandler handler, CancellationToken ct)
    {
        var poolBalancesOverMinimum = await cf.Run(con =>
            balanceRepo.GetPoolBalancesOverThresholdAsync(con, config.Id, config.PaymentProcessing.MinimumPayment));

        if(poolBalancesOverMinimum.Length > 0)
        {
            try
            {
                await handler.PayoutAsync(pool, poolBalancesOverMinimum, ct);
            }

            catch(Exception ex)
            {
                await NotifyPayoutFailureAsync(poolBalancesOverMinimum, config, ex);
                throw;
            }
        }

        else
            logger.Info(() => $"No balances over configured minimum payout for pool {config.Id}");
    }

    private Task NotifyPayoutFailureAsync(Balance[] balances, PoolConfig pool, Exception ex)
    {
        try
        {
            messageBus.SendMessage(new PaymentNotification(pool.Id, ex.Message, balances.Sum(x => x.Amount), pool.Template.Symbol));
        }
        catch(Exception notifyEx)
        {
            logger.Warn(notifyEx, $"[{pool.Id}] Failed to push payment failure notification");
        }

        return Task.CompletedTask;
    }



    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            // monitor pool lifetime
            disposables.Add(messageBus.Listen<PoolStatusNotification>()
                .ObserveOn(TaskPoolScheduler.Default)
                .Subscribe(OnPoolStatusNotification));

            logger.Info(() => "Online");

            // Allow all pools to actually come up before the first payment processing run
            await Task.Delay(initialRunDelay, ct);

            using var timer = new PeriodicTimer(interval);

            do
            {
                try
                {
                    await ProcessPoolsAsync(ct);
                }

                catch(OperationCanceledException)
                {
                    // ignored
                }

                catch(Exception ex)
                {
                    logger.Error(ex);
                }
            } while(await timer.WaitForNextTickAsync(ct));

            logger.Info(() => "Offline");
        }

        finally
        {
            disposables.Dispose();
        }
    }
}
