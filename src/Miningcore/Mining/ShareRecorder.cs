using System.Data.Common;
using System.Net.Sockets;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using MapsterMapper;
using Microsoft.Extensions.Hosting;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Newtonsoft.Json;
using NLog;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Contract = Miningcore.Contracts.Contract;
using Share = Miningcore.Blockchain.Share;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Mining;

/// <summary>
/// Asynchronously persist shares produced by all pools for processing by coin-specific payment processor(s)
/// </summary>
public class ShareRecorder : BackgroundService
{
    public ShareRecorder(IConnectionFactory cf,
        IMapper mapper,
        JsonSerializerSettings jsonSerializerSettings,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        ClusterConfig clusterConfig,
        IMessageBus messageBus)
    {
        Contract.RequiresNonNull(cf);
        Contract.RequiresNonNull(mapper);
        Contract.RequiresNonNull(shareRepo);
        Contract.RequiresNonNull(blockRepo);
        Contract.RequiresNonNull(jsonSerializerSettings);
        Contract.RequiresNonNull(messageBus);

        this.cf = cf;
        this.mapper = mapper;
        this.jsonSerializerSettings = jsonSerializerSettings;
        this.messageBus = messageBus;
        this.clusterConfig = clusterConfig;

        this.shareRepo = shareRepo;
        this.blockRepo = blockRepo;

        pools = clusterConfig.Pools.ToDictionary(x => x.Id, x => x);

        BuildFaultHandlingPolicy();
        ConfigureRecovery();
    }

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private readonly IShareRepository shareRepo;
    private readonly IBlockRepository blockRepo;
    private readonly IConnectionFactory cf;
    private readonly JsonSerializerSettings jsonSerializerSettings;
    private readonly IMessageBus messageBus;
    private readonly ClusterConfig clusterConfig;
    private readonly Dictionary<string, PoolConfig> pools;
    private readonly IMapper mapper;

    private ResiliencePipeline faultPipeline;
    private bool hasLoggedPolicyFallbackFailure;
    private string recoveryFilename;
    private const int RetryCount = 3;
    private bool notifiedAdminOnPolicyFallback = false;

    private async Task PersistSharesAsync(IList<Share> shares)
    {
        try
        {
            await faultPipeline.ExecuteAsync(async _ => await PersistSharesCoreAsync(shares));
        }
        catch(Exception ex)
        {
            await OnFallbackAsync(ex, shares);
        }
    }

    private async Task PersistSharesCoreAsync(IList<Share> shares)
    {
        // Collect block notifications to fire AFTER the transaction commits.
        // Firing inside RunTx (before CommitAsync) creates a race: a thread-pool subscriber
        // could query the DB before the block is visible (READ COMMITTED), find 0 pending
        // blocks and bail out early.  Post-commit the block is guaranteed visible.
        var pendingNotifications = new List<(string poolId, Block block, CoinTemplate template)>();

        await cf.RunTx(async (con, tx) =>
        {
            // Insert shares
            var mapped = shares.Select(mapper.Map<Persistence.Model.Share>).ToArray();
            await shareRepo.BatchInsertAsync(con, tx, mapped, CancellationToken.None);

            // Insert blocks
            foreach(var share in shares)
            {
                if(!share.IsBlockCandidate)
                    continue;

                var blockEntity = mapper.Map<Block>(share);
                blockEntity.Status = BlockStatus.Pending;
                await blockRepo.InsertAsync(con, tx, blockEntity);

                if(pools.TryGetValue(share.PoolId, out var poolConfig))
                    pendingNotifications.Add((share.PoolId, blockEntity, poolConfig.Template));
                else
                    logger.Warn(()=> $"Block found for unknown pool {share.PoolId}");
            }
        });

        // Transaction committed — block is now visible to all connections.
        // Fire notifications here so classifier and stats recorder see the block.
        foreach(var (poolId, block, template) in pendingNotifications)
        {
            try
            {
                messageBus.NotifyBlockFound(poolId, block, template);
            }
            catch(Exception ex)
            {
                logger.Warn(ex, $"[{poolId}] Failed to push block found notification for block {block.BlockHeight}");
            }
        }
    }

    private async Task OnFallbackAsync(Exception ex, IList<Share> shares)
    {
        logger.Warn(() => $"Fallback due to {ex.Source}: {ex.GetType().Name} ({ex.Message})");

        try
        {
            await using var stream = new FileStream(recoveryFilename, FileMode.Append, FileAccess.Write);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

            if(stream.Length == 0)
                WriteRecoveryFileheader(writer);

            foreach(var share in shares)
            {
                var json = JsonConvert.SerializeObject(share, jsonSerializerSettings);
                await writer.WriteLineAsync(json);
            }

            NotifyAdminOnPolicyFallback();
        }

        catch(Exception fallbackEx)
        {
            if(!hasLoggedPolicyFallbackFailure)
            {
                logger.Fatal(fallbackEx, "Fatal error during policy fallback execution. Share(s) will be lost!");
                hasLoggedPolicyFallbackFailure = true;
            }
        }
    }

    private static void WriteRecoveryFileheader(TextWriter writer)
    {
        writer.WriteLine("# The existence of this file means shares could not be committed to the database.");
        writer.WriteLine("# You should stop the pool cluster and run the following command:");
        writer.WriteLine("# miningcore -c <path-to-config> -rs <path-to-this-file>\n");
    }

    public async Task RecoverSharesAsync(string filename)
    {
        logger.Info(() => $"Recovering shares using {filename} ...");

        try
        {
            var successCount = 0;
            var failCount = 0;
            const int bufferSize = 100;

            await using(var stream = new FileStream(filename, FileMode.Open, FileAccess.Read))
            {
                using(var reader = new StreamReader(stream, new UTF8Encoding(false)))
                {
                    var shares = new List<Share>();
                    var lastProgressUpdate = DateTime.UtcNow;

                    while(true)
                    {
                        var line = await reader.ReadLineAsync();

                        if(line == null)
                            break;

                        if(string.IsNullOrEmpty(line))
                            continue;

                        // skip blank lines
                        line = line.Trim();

                        if(line.Length == 0)
                            continue;

                        // skip comments
                        if(line.StartsWith("#"))
                            continue;

                        // parse
                        try
                        {
                            var share = JsonConvert.DeserializeObject<Share>(line, jsonSerializerSettings);
                            shares.Add(share);
                        }

                        catch(JsonException ex)
                        {
                            logger.Error(ex, () => $"Unable to parse share record: {line}");
                            failCount++;
                        }

                        // import
                        try
                        {
                            if(shares.Count == bufferSize)
                            {
                                await PersistSharesCoreAsync(shares);

                                successCount += shares.Count;
                                shares.Clear();
                            }
                        }

                        catch(Exception ex)
                        {
                            logger.Error(ex, () => "Unable to import shares");
                            failCount++;
                        }

                        // progress
                        var now = DateTime.UtcNow;

                        if(now - lastProgressUpdate > TimeSpan.FromSeconds(10))
                        {
                            logger.Info($"{successCount} shares imported");
                            lastProgressUpdate = now;
                        }
                    }

                    // import remaining shares
                    try
                    {
                        if(shares.Count > 0)
                        {
                            await PersistSharesCoreAsync(shares);

                            successCount += shares.Count;
                        }
                    }

                    catch(Exception ex)
                    {
                        logger.Error(ex, () => "Unable to import shares");
                        failCount++;
                    }
                }
            }

            if(failCount == 0)
                logger.Info(() => $"Successfully imported {successCount} shares");
            else
                logger.Warn(() => $"Successfully imported {successCount} shares with {failCount} failures");
        }

        catch(FileNotFoundException)
        {
            logger.Error(() => $"Recovery file {filename} was not found");
        }
    }

    private void NotifyAdminOnPolicyFallback()
    {
        // After the first clause evaluates to true, Notifications and Admin are both non-null.
        // The second clause can therefore drop the redundant ?. chains.
        if(clusterConfig.Notifications?.Admin?.Enabled == true &&
           clusterConfig.Notifications.Admin.NotifyPaymentSuccess &&
           !notifiedAdminOnPolicyFallback)
        {
            notifiedAdminOnPolicyFallback = true;

            messageBus.SendMessage(new AdminNotification("Share Recorder Policy Fallback",
                $"The Share Recorder's Policy Fallback has been engaged. Check share recovery file {recoveryFilename}."));
        }
    }

    private void ConfigureRecovery()
    {
        recoveryFilename = !string.IsNullOrEmpty(clusterConfig.ShareRecoveryFile)
            ? clusterConfig.ShareRecoveryFile
            : "recovered-shares.txt";
    }

    private void BuildFaultHandlingPolicy()
    {
        faultPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<DbException>()
                    .Handle<SocketException>()
                    .Handle<TimeoutException>(),
                MaxRetryAttempts = RetryCount,
                // exponential back-off: 2s, 4s, 8s — same as old WaitAndRetryAsync
                DelayGenerator = args => new ValueTask<TimeSpan?>(
                    TimeSpan.FromSeconds(Math.Pow(2, args.AttemptNumber + 1))),
                OnRetry = args =>
                {
                    logger.Warn(() => $"Retry {args.AttemptNumber + 1} in {args.RetryDelay} due to " +
                        $"{args.Outcome.Exception?.Source}: {args.Outcome.Exception?.GetType().Name} ({args.Outcome.Exception?.Message})");
                    return default;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<DbException>()
                    .Handle<SocketException>()
                    .Handle<TimeoutException>(),
                // open after 2 consecutive failures, stay open for 1 minute
                FailureRatio = 1.0,
                MinimumThroughput = 2,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromMinutes(1)
            })
            .Build();
    }

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        logger.Info(() => "Online");

        return messageBus.Listen<Share>()
            .ObserveOn(TaskPoolScheduler.Default)
            .Where(x => x != null)
            .Select(x => x)
            .Buffer(TimeSpan.FromSeconds(5), 250)
            .Where(shares => shares.Any())
            .Select(shares => Observable.FromAsync(() =>
                Guard(() =>
                        PersistSharesAsync(shares),
                    ex => logger.Error(ex))))
            .Concat()
            .ToTask(ct)
            .ContinueWith(task =>
            {
                if(task.IsFaulted)
                    logger.Fatal(() => $"Terminated due to error {task.Exception?.InnerException ?? task.Exception}");
                else
                    logger.Info(() => "Offline");
            }, ct);
    }
}
