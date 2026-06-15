using System.IO.Compression;
using System.Reactive.Disposables;
using System.Text;
using Microsoft.Extensions.Hosting;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Notifications.Messages;
using Miningcore.Time;
using NetMQ;
using NetMQ.Sockets;
using NLog;

namespace Miningcore.Mining;

/// <summary>Receives ready-made block templates from GBTRelay.</summary>
public class BtStreamReceiver : BackgroundService
{
    public BtStreamReceiver(
        IMasterClock clock,
        IMessageBus messageBus,
        ClusterConfig clusterConfig)
    {
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(messageBus);

        this.clock = clock;
        this.messageBus = messageBus;
        this.clusterConfig = clusterConfig;
    }

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private readonly IMasterClock clock;
    private readonly IMessageBus messageBus;
    private readonly ClusterConfig clusterConfig;

    private static SubscriberSocket SetupSubSocket(ZmqPubSubEndpointConfig relay, bool silent = false)
    {
        var sub = new SubscriberSocket();

        if(!string.IsNullOrEmpty(relay.SharedEncryptionKey))
            sub.SetupCurveTlsClient(relay.SharedEncryptionKey, logger);

        sub.Connect(relay.Url);
        sub.SubscribeToAnyTopic();

        if(!silent)
        {
            if(sub.Options.CurveServerKey != null && sub.Options.CurveServerKey.Any(x => x != 0))
                logger.Info($"Monitoring Bt-Stream source {relay.Url} using key {sub.Options.CurveServerKey.ToHexString()}");
            else
                logger.Info($"Monitoring Bt-Stream source {relay.Url}");
        }

        return sub;
    }

    private void ProcessMessage(NetMQMessage msg)
    {
        var topic = msg[0].ConvertToString(Encoding.UTF8);
        var flags = BitConverter.ToUInt32(msg[1].ToByteArray(), 0);
        var data  = msg[2].ToByteArray();
        var sent  = DateTimeOffset.FromUnixTimeMilliseconds(
            BitConverter.ToInt64(msg[3].ToByteArray(), 0)).DateTime;

        if((flags & 1) == 1)
        {
            using var stm    = new MemoryStream(data);
            using var stmOut = new MemoryStream();
            using var ds     = new DeflateStream(stm, CompressionMode.Decompress);
            ds.CopyTo(stmOut);
            data = stmOut.ToArray();
        }

        var content = Encoding.UTF8.GetString(data);
        messageBus.SendMessage(new BtStreamMessage(topic, content, sent, DateTime.UtcNow));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var endpoints = clusterConfig.Pools
            .Select(x => x.Extra.SafeExtensionDataAs<BitcoinPoolConfigExtra>()?.BtStream)
            .Where(x => x != null)
            .DistinctBy(x => $"{x.Url}:{x.SharedEncryptionKey}")
            .ToArray();

        if(!endpoints.Any())
            return;

        var reconnectTimeout = TimeSpan.FromSeconds(300);
        var receiveTimeout   = TimeSpan.FromMilliseconds(5000);

        logger.Info(() => "Online");

        var tasks = endpoints.Select(relay => Task.Run(() =>
        {
            while(!ct.IsCancellationRequested)
            {
                var lastReceived = clock.Now;

                try
                {
                    using var sub = SetupSubSocket(relay);

                    while(!ct.IsCancellationRequested)
                    {
                        var msg = new NetMQMessage();
                        if(sub.TryReceiveMultipartMessage(receiveTimeout, ref msg, 4))
                        {
                            lastReceived = clock.Now;
                            ProcessMessage(msg);
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
                    logger.Error(() => $"{nameof(BtStreamReceiver)}: {ex}");

                    if(!ct.IsCancellationRequested)
                        Thread.Sleep(1000);
                }
            }
        }, ct));

        await Task.WhenAll(tasks);

        logger.Info(() => "Offline");
    }
}
