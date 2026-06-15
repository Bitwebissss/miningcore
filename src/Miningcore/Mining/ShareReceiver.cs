using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Hosting;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Notifications.Messages;
using Miningcore.Time;
using Miningcore.Util;
using NetMQ;
using NetMQ.Sockets;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using NLog;
using ProtoBuf;

namespace Miningcore.Mining;

/// <summary>Receives external shares from relays and re-publishes for consumption.</summary>
public class ShareReceiver : BackgroundService
{
    public ShareReceiver(
        ClusterConfig clusterConfig,
        IMasterClock clock,
        IMessageBus messageBus)
    {
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(messageBus);

        this.clusterConfig = clusterConfig;
        this.clock = clock;
        this.messageBus = messageBus;
    }

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private readonly IMasterClock clock;
    private readonly IMessageBus messageBus;
    private readonly ClusterConfig clusterConfig;
    private readonly CompositeDisposable disposables = new();
    private readonly ConcurrentDictionary<string, PoolContext> pools = new();
    private readonly BufferBlock<(string Url, NetMQMessage Message)> queue = new();

    readonly JsonSerializer serializer = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    private class PoolContext
    {
        public PoolContext(IMiningPool pool, ILogger logger)
        {
            Pool = pool;
            Logger = logger;
        }

        public IMiningPool Pool { get; }
        public ILogger Logger { get; }
        public DateTime? LastBlock { get; set; }
        public long BlockHeight { get; set; }
    }

    private void AttachPool(IMiningPool pool)
    {
        var ctx = new PoolContext(pool, LogUtil.GetPoolScopedLogger(typeof(ShareRecorder), pool.Config));
        pools.TryAdd(pool.Config.Id, ctx);
    }

    private void OnPoolStatusNotification(PoolStatusNotification notification)
    {
        if(notification.Status == PoolStatus.Online)
            AttachPool(notification.Pool);
    }

    private static SubscriberSocket SetupSubSocket(ShareRelayEndpointConfig relay, bool silent = false)
    {
        var sub = new SubscriberSocket();
        sub.SetupCurveTlsClient(relay.SharedEncryptionKey, logger);
        sub.Connect(relay.Url);
        sub.SubscribeToAnyTopic();

        if(!silent)
        {
            if(sub.Options.CurveServerKey != null && sub.Options.CurveServerKey.Any(x => x != 0))
                logger.Info($"Monitoring external stratum {relay.Url} using key {sub.Options.CurveServerKey.ToHexString()}");
            else
                logger.Info($"Monitoring external stratum {relay.Url}");
        }

        return sub;
    }

    private Task StartMessageReceiver(CancellationToken ct)
    {
        var relays = clusterConfig.ShareRelays
            .DistinctBy(x => $"{x.Url}:{x.SharedEncryptionKey}")
            .ToArray();

        var tasks = relays.Select(relay => Task.Run(() =>
        {
            var reconnectTimeout = TimeSpan.FromSeconds(60);
            var receiveTimeout   = TimeSpan.FromMilliseconds(5000);

            while(!ct.IsCancellationRequested)
            {
                var lastReceived = clock.Now;

                try
                {
                    using var sub = SetupSubSocket(relay);

                    while(!ct.IsCancellationRequested)
                    {
                        var msg = new NetMQMessage();
                        if(sub.TryReceiveMultipartMessage(receiveTimeout, ref msg, 3))
                        {
                            lastReceived = clock.Now;
                            queue.Post((relay.Url, msg));
                        }
                        else if(clock.Now - lastReceived > reconnectTimeout)
                        {
                            logger.Info(() => $"Receive timeout exceeded. Re-connecting to {relay.Url} ...");
                            break;
                        }
                    }
                }

                catch(Exception ex)
                {
                    logger.Error(() => $"{nameof(ShareReceiver)}: {ex}");

                    if(!ct.IsCancellationRequested)
                        Thread.Sleep(5000);
                }
            }
        }, ct));

        return Task.WhenAll(tasks);
    }

    private Task StartMessageProcessors(CancellationToken ct)
    {
        // FIX: Enumerable.Repeat(ProcessMessages(ct), N) evaluated ProcessMessages(ct)
        // exactly ONCE and handed the same Task reference N times to Task.WhenAll.
        // Task.WhenAll on N copies of the same task is equivalent to waiting for
        // that one task — so only a single processor was running regardless of
        // Environment.ProcessorCount.
        // Correct pattern: Range + Select invokes ProcessMessages independently N times.
        var tasks = Enumerable.Range(0, Environment.ProcessorCount)
            .Select(_ => ProcessMessages(ct));
        return Task.WhenAll(tasks);
    }

    private async Task ProcessMessages(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try
            {
                var (url, msg) = await queue.ReceiveAsync(ct);
                ProcessMessage(url, msg);
            }

            // FIX: OperationCanceledException is thrown by ReceiveAsync when ct fires
            // (normal application shutdown).  Catching it with the generic handler below
            // caused a spurious Error log entry on every clean shutdown.
            // Handle it explicitly and silently.
            catch(OperationCanceledException)
            {
                // Normal shutdown – do not log as error.
            }

            catch(Exception ex)
            {
                logger.Error(ex);
            }
        }
    }

    private void ProcessMessage(string url, NetMQMessage msg)
    {
        var topic = msg[0].ConvertToString(Encoding.UTF8);
        var flags = BitConverter.ToUInt32(msg[1].ToByteArray(), 0);
        var data  = msg[2].ToByteArray();

        if(string.IsNullOrEmpty(topic) || !pools.TryGetValue(topic, out var poolContext))
        {
            logger.Warn(() => $"Received share for pool '{topic}' which is not known locally. Ignoring ...");
            return;
        }

        if(data?.Length == 0)
        {
            logger.Warn(() => $"Received empty data from {url}/{topic}. Ignoring ...");
            return;
        }

        // Wire format flags are sent as little-endian by the relay.
        // If the low nibble is zero the bytes arrived in big-endian order
        // (older relay version) – swap them before extracting the format bits.
        if((flags & ShareRelay.WireFormatMask) == 0)
            flags = BitConverter.ToUInt32(BitConverter.GetBytes(flags).ToNewReverseArray());

        var wireFormat = (ShareRelay.WireFormat) (flags & ShareRelay.WireFormatMask);

        Share share = null;

        switch(wireFormat)
        {
            case ShareRelay.WireFormat.Json:
                using(var stream = new MemoryStream(data))
                {
                    using var reader  = new StreamReader(stream, Encoding.UTF8);
                    using var jreader = new JsonTextReader(reader);
                    share = serializer.Deserialize<Share>(jreader);
                }
                break;

            case ShareRelay.WireFormat.ProtocolBuffers:
                using(var stream = new MemoryStream(data))
                {
                    share = Serializer.Deserialize<Share>(stream);
                    share.BlockReward = (decimal) share.BlockRewardDouble;
                }
                break;

            default:
                logger.Error(() => $"Unsupported wire format {wireFormat} of share received from {url}/{topic} ");
                break;
        }

        if(share == null)
        {
            logger.Error(() => $"Unable to deserialize share received from {url}/{topic}");
            return;
        }

        share.PoolId  = topic;
        share.Created = clock.Now;
        messageBus.SendMessage(share);

        // poolContext is guaranteed non-null here: the TryGetValue early-return above
        // ensures we only reach this point when the pool was found.
        var pool            = poolContext.Pool;
        var shareMultiplier = poolContext.Pool.ShareMultiplier;

        poolContext.Logger.Info(() => $"External {(!string.IsNullOrEmpty(share.Source) ? $"[{share.Source.ToUpper()}] " : string.Empty)}share accepted: D={Math.Round(share.Difficulty * shareMultiplier, 4)}");

        messageBus.SendTelemetry(share.PoolId, TelemetryCategory.Share, TimeSpan.Zero, true);

        if(pool.NetworkStats != null)
        {
            // Use BlockchainStats.SyncRoot as the shared lock — the same object that
            // BitcoinJobManager.UpdateJob (Rx Concat chain #1) and
            // UpdateNetworkStatsAsync (Rx Concat chain #2) now lock on.
            // All three execution contexts write overlapping fields of the same
            // BlockchainStats instance; they must all lock on the same monitor.
            lock(pool.NetworkStats.SyncRoot)
            {
                pool.NetworkStats.BlockHeight       = (ulong) share.BlockHeight;
                pool.NetworkStats.NetworkDifficulty = share.NetworkDifficulty;

                if(poolContext.BlockHeight != share.BlockHeight)
                {
                    pool.NetworkStats.LastNetworkBlockTime = clock.Now;
                    poolContext.BlockHeight = share.BlockHeight;
                    poolContext.LastBlock   = clock.Now;
                }

                else
                    pool.NetworkStats.LastNetworkBlockTime = poolContext.LastBlock;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(clusterConfig.ShareRelays != null)
        {
            try
            {
                disposables.Add(messageBus.Listen<PoolStatusNotification>()
                    .ObserveOn(TaskPoolScheduler.Default)
                    .Subscribe(OnPoolStatusNotification));

                await Task.WhenAll(
                    StartMessageReceiver(ct),
                    StartMessageProcessors(ct));
            }

            finally
            {
                disposables.Dispose();
            }
        }
    }
}
