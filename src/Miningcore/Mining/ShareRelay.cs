using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Microsoft.Extensions.Hosting;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Extensions;
using Miningcore.Messaging;
using NetMQ;
using NetMQ.Sockets;
using NLog;
using ProtoBuf;

namespace Miningcore.Mining;

public class ShareRelay : IHostedService
{
    public ShareRelay(ClusterConfig clusterConfig, IMessageBus messageBus)
    {
        Contract.RequiresNonNull(messageBus);

        this.clusterConfig = clusterConfig;
        this.messageBus = messageBus;
    }

    private readonly IMessageBus messageBus;
    private readonly ClusterConfig clusterConfig;
    private readonly BlockingCollection<Share> queue = new();
    private IDisposable queueSub;
    private readonly int QueueSizeWarningThreshold = 1024;
    private bool hasWarnedAboutBacklogSize;
    private PublisherSocket pubSocket;

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

    [Flags]
    public enum WireFormat
    {
        Json = 1,
        ProtocolBuffers = 2
    }

    public const int WireFormatMask = 0xF;

    private void InitializeQueue()
    {
        queueSub = queue.GetConsumingEnumerable()
            .ToObservable(TaskPoolScheduler.Default)
            .Do(_ => CheckQueueBacklog())
            .Subscribe(share =>
            {
                share.Source = clusterConfig.ClusterName;
                share.BlockRewardDouble = (double) share.BlockReward;

                try
                {
                    const int flags = (int) WireFormat.ProtocolBuffers;

                    var msg = new NetMQMessage();
                    msg.Append(share.PoolId);
                    msg.Append(BitConverter.GetBytes(flags));

                    using(var stream = new MemoryStream())
                    {
                        Serializer.Serialize(stream, share);
                        msg.Append(stream.ToArray());
                    }

                    pubSocket.SendMultipartMessage(msg);
                }

                catch(Exception ex)
                {
                    logger.Error(ex);
                }
            });
    }

    private void CheckQueueBacklog()
    {
        if(queue.Count > QueueSizeWarningThreshold)
        {
            if(!hasWarnedAboutBacklogSize)
            {
                logger.Warn(() => $"Share relay queue backlog has crossed {QueueSizeWarningThreshold}");
                hasWarnedAboutBacklogSize = true;
            }
        }

        else if(hasWarnedAboutBacklogSize && queue.Count <= QueueSizeWarningThreshold / 2)
        {
            hasWarnedAboutBacklogSize = false;
        }
    }

    public Task StartAsync(CancellationToken ct)
    {
        messageBus.Listen<Share>().Subscribe(x => queue.Add(x, ct));

        pubSocket = new PublisherSocket();

        if(!clusterConfig.ShareRelay.Connect)
        {
            var serverPubKey = pubSocket.SetupCurveTlsServer(clusterConfig.ShareRelay.SharedEncryptionKey, logger);

            pubSocket.Bind(clusterConfig.ShareRelay.PublishUrl);

            if(serverPubKey != null)
                logger.Info(() => $"Bound to {clusterConfig.ShareRelay.PublishUrl} using key {serverPubKey.ToHexString()}");
            else
                logger.Info(() => $"Bound to {clusterConfig.ShareRelay.PublishUrl}");
        }

        else
        {
            if(!string.IsNullOrEmpty(clusterConfig.ShareRelay.SharedEncryptionKey?.Trim()))
                throw new PoolStartupException("ZeroMQ Curve is not supported in ShareRelay Connect-Mode");

            pubSocket.Connect(clusterConfig.ShareRelay.PublishUrl);

            logger.Info(() => $"Connected to {clusterConfig.ShareRelay.PublishUrl}");
        }

        InitializeQueue();

        logger.Info(() => "Online");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        // FIX: Stop the queue consumer BEFORE disposing the socket.
        // Previous order (socket first, then queueSub) caused ObjectDisposedException
        // when in-flight shares tried to call pubSocket.SendMultipartMessage after disposal.
        // With correct order: once queueSub is disposed the Rx subscription detaches,
        // no new SendMultipartMessage calls will start, and the socket can be
        // closed safely afterwards.
        queueSub?.Dispose();
        queueSub = null;

        pubSocket.Dispose();

        return Task.CompletedTask;
    }
}
