using Autofac;
using MapsterMapper;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Notifications.Messages;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.Time;
using Miningcore.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Block = Miningcore.Persistence.Model.Block;
using Contract = Miningcore.Contracts.Contract;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.Bitcoin;

[CoinFamily(CoinFamily.Bitcoin)]
public class BitcoinPayoutHandler : PayoutHandlerBase,
    IPayoutHandler
{
    public BitcoinPayoutHandler(
        IComponentContext ctx,
        IConnectionFactory cf,
        IMapper mapper,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        IBalanceRepository balanceRepo,
        IPaymentRepository paymentRepo,
        IStatsRepository statsRepo,
        IMasterClock clock,
        IMessageBus messageBus) :
        base(cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(balanceRepo);
        Contract.RequiresNonNull(paymentRepo);

        this.ctx = ctx;
        this.statsRepo = statsRepo;
    }

    protected readonly IComponentContext ctx;
    private readonly IStatsRepository statsRepo;
    protected RpcClient rpcClient;
    protected BitcoinPoolConfigExtra extraPoolConfig;
    protected BitcoinDaemonEndpointConfigExtra extraPoolEndpointConfig;
    protected BitcoinPoolPaymentProcessingConfigExtra extraPoolPaymentProcessingConfig;

    private int payoutDecimalPlaces = 8;
    private CoinTemplate coin;
    private int minConfirmations;
    private int payoutMinConfirmations;
    private ExtendedMaturityConfig extendedMaturity;

    protected override string LogCategory => "Bitcoin Payout Handler";

    private int GetEffectiveMinConfirmations(ulong blockHeight)
    {
        if(extendedMaturity != null &&
           blockHeight >= extendedMaturity.StartHeight &&
           blockHeight < extendedMaturity.EndHeight)
        {
            return (int)(extendedMaturity.EndHeight - blockHeight) + minConfirmations;
        }
        return minConfirmations;
    }

    #region IPayoutHandler

    public virtual Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct)
    {
        Contract.RequiresNonNull(pc);

        poolConfig = pc;
        clusterConfig = cc;

        extraPoolConfig = pc.Extra.SafeExtensionDataAs<BitcoinPoolConfigExtra>();
        extraPoolEndpointConfig = pc.Extra.SafeExtensionDataAs<BitcoinDaemonEndpointConfigExtra>();
        extraPoolPaymentProcessingConfig = pc.PaymentProcessing.Extra.SafeExtensionDataAs<BitcoinPoolPaymentProcessingConfigExtra>();

        coin = poolConfig.Template.As<CoinTemplate>();
        if(coin is BitcoinTemplate bitcoinTemplate)
        {
            minConfirmations = extraPoolEndpointConfig?.MinimumConfirmations ?? bitcoinTemplate.CoinbaseMinConfirmations ?? BitcoinConstants.CoinbaseMinConfirmations;
            payoutDecimalPlaces = bitcoinTemplate.PayoutDecimalPlaces ?? 8;
            extendedMaturity = bitcoinTemplate.ExtendedMaturity;
        }
        else
            minConfirmations = extraPoolEndpointConfig?.MinimumConfirmations ?? BitcoinConstants.CoinbaseMinConfirmations;

        // PayoutMinConfirmations: explicit override in paymentProcessing.extra, otherwise falls back to
        // the same resolved minConfirmations (which itself follows: endpoint override → coin template → BitcoinConstants.CoinbaseMinConfirmations=102).
        // Only applied when the coin/pool opts in via payoutMinConfirmationsEnabled = true.
        payoutMinConfirmations = extraPoolPaymentProcessingConfig?.PayoutMinConfirmations ?? minConfirmations;

        logger = LogUtil.GetPoolScopedLogger(typeof(BitcoinPayoutHandler), pc);

        var jsonSerializerSettings = ctx.Resolve<JsonSerializerSettings>();
        rpcClient = new RpcClient(pc.Daemons.First(), jsonSerializerSettings, messageBus, pc.Id);

        return Task.CompletedTask;
    }

    protected override void NotifyPayoutSuccess(string poolId, Balance[] balances, string[] txHashes, decimal? txFee)
    {
        var coin = poolConfig.Template.As<CoinTemplate>();
        var explorerLinks = !string.IsNullOrEmpty(coin.ExplorerTxLink) ?
            txHashes.Select(x => string.Format(coin.ExplorerTxLink, x)).ToArray() :
            Array.Empty<string>();

        // fire-and-forget: query totalPaid then send enriched notification
        _ = Task.Run(async () =>
        {
            decimal? totalPaid = null;
            try
            {
                if(statsRepo != null)
                    totalPaid = await cf.Run(con => statsRepo.GetTotalPoolPaymentsAsync(con, poolId, CancellationToken.None));
            }
            catch(Exception ex)
            {
                logger.Warn(ex, $"[{LogCategory}] Failed to fetch totalPaid for payment notification");
            }

            try
            {
                messageBus.SendMessage(new PaymentNotification(poolId, null, balances.Sum(x => x.Amount), coin.Symbol, balances.Length, txHashes, explorerLinks, txFee)
                {
                    TotalPaid = totalPaid
                });
            }
            catch(Exception ex)
            {
                logger.Warn(ex, $"[{LogCategory}] Failed to push payment success notification");
            }
        });
    }

    public virtual async Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct)
    {
        Contract.RequiresNonNull(poolConfig);
        Contract.RequiresNonNull(blocks);

        var pageSize = 100;
        var pageCount = (int) Math.Ceiling(blocks.Length / (double) pageSize);
        var result = new List<Block>();

        for(var i = 0; i < pageCount; i++)
        {
            // get a page full of blocks
            var page = blocks
                .Skip(i * pageSize)
                .Take(pageSize)
                .ToArray();

            // build command batch (block.TransactionConfirmationData is the hash of the blocks coinbase transaction)
            var batch = page.Select(block => new RpcRequest(BitcoinCommands.GetTransaction,
                new[] { block.TransactionConfirmationData })).ToArray();

            // execute batch
            var results = await rpcClient.ExecuteBatchAsync(logger, ct, batch);

            for(var j = 0; j < results.Length; j++)
            {
                var cmdResult = results[j];

                var transactionInfo = cmdResult.Response?.ToObject<Transaction>();
                var block = page[j];

                // check error
                if(cmdResult.Error != null)
                {
                    // Code -5 interpreted as "orphaned"
                    if(cmdResult.Error.Code == -5)
                    {
                        block.Status = BlockStatus.Orphaned;
                        block.Reward = 0;
                        result.Add(block);

                        logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned due to daemon error {cmdResult.Error.Code}");
                    }

                    else
                        logger.Warn(() => $"[{LogCategory}] Daemon reports error '{cmdResult.Error.Message}' (Code {cmdResult.Error.Code}) for transaction {page[j].TransactionConfirmationData}");
                }

                // missing transaction details are interpreted as "orphaned"
                else if(transactionInfo?.Details == null || transactionInfo.Details.Length == 0)
                {
                    block.Status = BlockStatus.Orphaned;
                    block.Reward = 0;
                    result.Add(block);

                    logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned due to missing tx details");
                }

                else
                {
                    switch(transactionInfo.Details[0].Category)
                    {
                        case "immature":
                            // update progress
                            block.ConfirmationProgress = Math.Min(1.0d, (double) transactionInfo.Confirmations / GetEffectiveMinConfirmations(block.BlockHeight));
                            block.Reward = transactionInfo.Amount != 0 ? transactionInfo.Amount : transactionInfo.Details.Sum(d => d.Amount);  // fallback: some nodes return 0 in top-level amount for immature coinbase
                            result.Add(block);

                            break;

                        case "generate":
                            // Node reports coinbase as mature and spendable.
                            // When PayoutMinConfirmationsEnabled, we require an additional confirmation
                            // buffer (payoutMinConfirmations) on top of consensus maturity before
                            // crediting balances. This guards against 1-2 block reorgs at the exact
                            // maturity boundary. Extended maturity progress bar logic is intentionally
                            // separate — this guard is a flat check on raw confirmation count only.
                            if(extraPoolPaymentProcessingConfig?.PayoutMinConfirmationsEnabled == true &&
                               transactionInfo.Confirmations < payoutMinConfirmations)
                            {
                                block.ConfirmationProgress = Math.Min(1.0d, (double) transactionInfo.Confirmations / payoutMinConfirmations);
                                block.Reward = transactionInfo.Amount != 0 ? transactionInfo.Amount : transactionInfo.Details.Sum(d => d.Amount);
                                result.Add(block);

                                logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} is mature (generate) but waiting for payout guard: {transactionInfo.Confirmations}/{payoutMinConfirmations} confirmations");

                                break;
                            }

                            block.Status = BlockStatus.Confirmed;
                            block.ConfirmationProgress = 1;
                            block.Reward = transactionInfo.Amount != 0 ? transactionInfo.Amount : transactionInfo.Details.Sum(d => d.Amount);  // fallback: some nodes return 0 in top-level amount for immature coinbase
                            result.Add(block);

                            logger.Info(() => $"[{LogCategory}] Unlocked block {block.BlockHeight} worth {FormatAmount(block.Reward)}");
                            break;

                        default:
                            logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned. Category: {transactionInfo.Details[0].Category}");

                            block.Status = BlockStatus.Orphaned;
                            block.Reward = 0;
                            result.Add(block);
                            break;
                    }
                }
            }
        }

        return result.ToArray();
    }

    public virtual async Task PayoutAsync(IMiningPool pool, Balance[] balances, CancellationToken ct)
    {
        Contract.RequiresNonNull(balances);

        if(extraPoolPaymentProcessingConfig?.PayoutMinConfirmationsEnabled == true)
            logger.Info(() => $"[{LogCategory}] Payout guard active: wallet sendmany minconf = {payoutMinConfirmations} confirmations");

        // build args
        var amounts = balances
            .Where(x => x.Amount > 0)
            .ToDictionary(x => x.Address, x => Math.Round(x.Amount, payoutDecimalPlaces));

        if(amounts.Count == 0)
            return;

        logger.Info(() => $"[{LogCategory}] Paying {FormatAmount(balances.Sum(x => x.Amount))} to {balances.Length} addresses");

        object[] args;

        var identifier = !string.IsNullOrEmpty(clusterConfig.PaymentProcessing?.CoinbaseString) ?
            clusterConfig.PaymentProcessing.CoinbaseString.Trim() : "Miningcore";

        var comment = $"{identifier} Payment";

        if(!(extraPoolConfig?.HasBrokenSendMany == true || poolConfig.Template is BitcoinTemplate { HasBrokenSendMany: true }))
        {
            if(extraPoolPaymentProcessingConfig?.MinersPayTxFees == true)
            {
                var subtractFeesFrom = amounts.Keys.ToArray();

                args = new object[]
                {
                    string.Empty, // default account
                    amounts, // addresses and associated amounts
                    1, // only spend funds covered by this many confirmations
                    comment, // tx comment
                    subtractFeesFrom, // distribute transaction fee equally over all recipients
                };
            }

            else
            {
                args = new object[]
                {
                    string.Empty, // default account
                    amounts, // addresses and associated amounts
                };
            }

            var didUnlockWallet = false;

            // send command
            tryTransfer:
            var result = await rpcClient.ExecuteAsync<string>(logger, BitcoinCommands.SendMany, ct, args);

            if(result.Error == null)
            {
                if(didUnlockWallet)
                {
                    // lock wallet
                    logger.Info(() => $"[{LogCategory}] Locking wallet");
                    await rpcClient.ExecuteAsync<JToken>(logger, BitcoinCommands.WalletLock, ct);
                }

                // check result
                var txId = result.Response;

                if(string.IsNullOrEmpty(txId))
                    logger.Error(() => $"[{LogCategory}] {BitcoinCommands.SendMany} did not return a transaction id!");
                else
                    logger.Info(() => $"[{LogCategory}] Payment transaction id: {txId}");

                await PersistPaymentsAsync(balances, txId);

                NotifyPayoutSuccess(poolConfig.Id, balances, new[]
                {
                    txId
                }, null);
            }

            else
            {
                if(result.Error.Code == (int) BitcoinRPCErrorCode.RPC_WALLET_UNLOCK_NEEDED && !didUnlockWallet)
                {
                    if(!string.IsNullOrEmpty(extraPoolPaymentProcessingConfig?.WalletPassword))
                    {
                        logger.Info(() => $"[{LogCategory}] Unlocking wallet");

                        var unlockResult = await rpcClient.ExecuteAsync<JToken>(logger, BitcoinCommands.WalletPassphrase, ct, new[]
                        {
                            extraPoolPaymentProcessingConfig.WalletPassword,
                            (object) 5 // unlock for N seconds
                        });

                        if(unlockResult.Error == null)
                        {
                            didUnlockWallet = true;
                            goto tryTransfer;
                        }

                        else
                            logger.Error(() => $"[{LogCategory}] {BitcoinCommands.WalletPassphrase} returned error: {unlockResult.Error.Message} code {unlockResult.Error.Code}");
                    }

                    else
                        logger.Error(() => $"[{LogCategory}] Wallet is locked but walletPassword was not configured. Unable to send funds.");
                }

                else
                {
                    logger.Error(() => $"[{LogCategory}] {BitcoinCommands.SendMany} returned error: {result.Error.Message} code {result.Error.Code}");

                    NotifyPayoutFailure(poolConfig.Id, balances, $"{BitcoinCommands.SendMany} returned error: {result.Error.Message} code {result.Error.Code}", null);
                }
            }
        }

        else
        {
            var txFailures = new List<Tuple<KeyValuePair<string, decimal>, Exception>>();
            var successBalances = new Dictionary<Balance, string>();

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 8,
                CancellationToken = ct
            };

            await Parallel.ForEachAsync(amounts, parallelOptions, async (x, _ct) =>
            {
                var (address, amount) = x;

                await Guard(async () =>
                {
                    // use a common id for all log entries related to this transfer
                    var transferId = CorrelationIdGenerator.GetNextId();

                    logger.Info(()=> $"[{LogCategory}] [{transferId}] Sending {FormatAmount(amount)} to {address}");

                    var result = await rpcClient.ExecuteAsync<string>(logger, BitcoinCommands.SendToAddress, _ct, new object[]
                    {
                        address,
                        amount,
                    });

                    // check result
                    var txId = result.Response;

                    if(result.Error != null)
                        throw new Exception($"[{transferId}] {BitcoinCommands.SendToAddress} returned error: {result.Error.Message} code {result.Error.Code}");

                    if(string.IsNullOrEmpty(txId))
                        throw new Exception($"[{transferId}] {BitcoinCommands.SendToAddress} did not return a transaction id!");
                    else
                        logger.Info(() => $"[{LogCategory}] [{transferId}] Payment transaction id: {txId}");

                    successBalances.Add(new Balance
                    {
                        PoolId = poolConfig.Id,
                        Address = address,
                        Amount = amount,
                    }, txId);
                }, ex =>
                {
                    txFailures.Add(Tuple.Create(x, ex));
                });
            });

            if(successBalances.Any())
            {
                await PersistPaymentsAsync(successBalances);

                NotifyPayoutSuccess(poolConfig.Id, successBalances.Keys.ToArray(), successBalances.Values.ToArray(), null);
            }

            if(txFailures.Any())
            {
                var failureBalances = txFailures.Select(x=> new Balance { Amount = x.Item1.Value }).ToArray();
                var error = string.Join(", ", txFailures.Select(x => $"{x.Item1.Key} {FormatAmount(x.Item1.Value)}: {x.Item2.Message}"));

                logger.Error(()=> $"[{LogCategory}] Failed to transfer the following balances: {error}");

                NotifyPayoutFailure(poolConfig.Id, failureBalances, error, null);
            }
        }
    }

    public double AdjustBlockEffort(double effort)
    {
        return effort;
    }

    #endregion // IPayoutHandler
}
