using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Channels;
using Autofac;
using Microsoft.AspNetCore.Http;
using Miningcore.Api.WebSocketNotifications;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Notifications.Messages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

namespace Miningcore.Api;

public class WebSocketNotificationsRelay
{
    public WebSocketNotificationsRelay(IComponentContext ctx)
    {
        messageBus = ctx.Resolve<IMessageBus>();
        var clusterConfig = ctx.Resolve<ClusterConfig>();
        pools = clusterConfig.Pools
            .Where(x => x.Enabled)
            .ToDictionary(x => x.Id, x => x);

        serializer = new Newtonsoft.Json.JsonSerializer
        {
            ContractResolver = ctx.Resolve<JsonSerializerSettings>().ContractResolver!
        };

        // blockfoundstats - fired by BlockClassifierService AFTER classification; frontend shows toast on this.
        // chainheightstats - fired by BlockClassifierService AFTER classification; updates height/counts, no toast.
        // NEVER relay BlockFoundNotification or NewChainHeightNotification here - they fire BEFORE classification.
        Relay<BlockConfirmationProgressNotification>(WsNotificationType.BlockUnlockProgress);
        Relay<PaymentNotification>(WsNotificationType.Payment);
        Relay<ChainHeightStatsNotification>(WsNotificationType.ChainHeightStats);
        Relay<BlockFoundStatsNotification>(WsNotificationType.BlockFoundStats);
        Relay<CycleStatsNotification>(WsNotificationType.CycleStats);
    }

    private readonly IMessageBus messageBus;
    private readonly Dictionary<string, PoolConfig> pools;
    private readonly Newtonsoft.Json.JsonSerializer serializer;
    private readonly ConcurrentDictionary<string, ClientConnection> clients = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ClientConnection>> rooms = new();
    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private const int ReceiveBufferSize = 4096;
    private const int SendQueueLimit = 1024;

    public async Task HandleAsync(HttpContext context)
    {
        if(!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Validate pool IDs BEFORE accepting the WebSocket upgrade.
        // Invalid or missing poolId → HTTP 400, handshake never completes,
        // no greeting is sent, no zombie connection is created.
        var subscriptions = GetRequestedPoolIds(context);
        if(subscriptions.Count == 0)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var client = new ClientConnection(socket);
        clients.TryAdd(client.Id, client);

        foreach(var poolId in subscriptions)
            JoinRoom(client, poolId);

        var greeting = ToJson(WsNotificationType.Greeting, new
        {
            Message = "Connected to Miningcore notification relay",
            Subscriptions = client.PoolIds.Keys.ToArray(),
        });

        if(!client.Enqueue(greeting))
            return;

        var writer = WriteLoopAsync(client, context.RequestAborted);

        try
        {
            await ReceiveLoopAsync(client, context.RequestAborted);
        }

        catch(OperationCanceledException)
        {
            logger.Trace(() => $"WebSocket client {client.Id} receive loop cancelled");
        }

        catch(Exception ex)
        {
            logger.Debug(ex, $"WebSocket client {client.Id} receive loop failed");
        }

        finally
        {
            RemoveClient(client);
            client.Complete();

            try
            {
                await writer;
            }
            catch(Exception ex)
            {
                logger.Debug(ex, $"WebSocket client {client.Id} write loop failed");
            }
        }
    }

    private void Relay<T>(WsNotificationType type)
    {
        messageBus.Listen<T>()
            .Select(x => Observable.FromAsync(() => BroadcastNotification(type, x)))
            .Concat()
            .Subscribe();
    }

    private async Task BroadcastNotification<T>(WsNotificationType type, T notification)
    {
        try
        {
            var poolId = GetPoolId(notification);
            if(string.IsNullOrEmpty(poolId))
                return;
            if(rooms.TryGetValue(poolId, out var room))
            {
                var json = ToJson(type, notification);
                foreach(var client in room.Values)
                {
                    if(!client.Enqueue(json))
                        await CloseSlowClient(client);
                }
            }
        }

        catch(Exception ex)
        {
            logger.Error(ex);
        }
    }

    private string ToJson<T>(WsNotificationType type, T msg)
    {
        var result = JObject.FromObject(msg, serializer);
        result["type"] = type.ToString().ToLower();

        return result.ToString(Formatting.None);
    }

    private HashSet<string> GetRequestedPoolIds(HttpContext context)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var raw = context.Request.Query["poolId"]
            .Concat(context.Request.Query["poolIds"])
            .ToArray();

        foreach(var item in raw)
        {
            foreach(var poolId in item.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if(pools.ContainsKey(poolId))
                    result.Add(poolId);
            }
        }
        return result;
    }

    private async Task ReceiveLoopAsync(ClientConnection client, CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferSize];

        while(!ct.IsCancellationRequested && client.Socket.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await client.Socket.ReceiveAsync(buffer, ct);

                if(result.MessageType == WebSocketMessageType.Close)
                    return;

                if(ms.Length + result.Count > ReceiveBufferSize)
                {
                    await client.Socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large", ct);
                    return;
                }

                ms.Write(buffer, 0, result.Count);
            } while(!result.EndOfMessage);

            if(result.MessageType == WebSocketMessageType.Text && ms.Length > 0)
                await HandleClientMessage(client, ms.ToArray());
        }
    }

    private Task HandleClientMessage(ClientConnection client, byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if(!doc.RootElement.TryGetProperty("type", out var typeElement))
                return Task.CompletedTask;

            var type = typeElement.GetString()?.ToLowerInvariant();
            var poolId = doc.RootElement.TryGetProperty("poolId", out var poolElement)
                ? poolElement.GetString()
                : null;

            if(string.IsNullOrEmpty(poolId) || !pools.ContainsKey(poolId))
                return Task.CompletedTask;

            switch(type)
            {
                case "subscribe":
                    JoinRoom(client, poolId);
                    break;

                case "unsubscribe":
                    LeaveRoom(client, poolId);
                    // If the client left all rooms it becomes a zombie — close immediately.
                    // Safe for pool switching: frontend subscribes to the new pool first,
                    // then unsubscribes from the old one, so PoolIds is never empty mid-switch.
                    if(client.PoolIds.IsEmpty)
                        _ = CloseSlowClient(client);
                    break;
            }
        }

        catch(Exception ex)
        {
            logger.Debug(ex, $"Ignoring invalid WebSocket client message from {client.Id}");
        }

        return Task.CompletedTask;
    }

    private async Task WriteLoopAsync(ClientConnection client, CancellationToken ct)
    {
        await foreach(var msg in client.Outbound.Reader.ReadAllAsync(ct))
        {
            if(client.Socket.State != WebSocketState.Open)
                return;

            await client.Socket.SendAsync(msg, ct);
        }
    }

    private void JoinRoom(ClientConnection client, string poolId)
    {
        var room = rooms.GetOrAdd(poolId, _ => new ConcurrentDictionary<string, ClientConnection>());
        room[client.Id] = client;
        client.PoolIds[poolId] = 0;
    }

    private void LeaveRoom(ClientConnection client, string poolId)
    {
        if(rooms.TryGetValue(poolId, out var room))
        {
            room.TryRemove(client.Id, out _);

            if(room.IsEmpty)
                rooms.TryRemove(poolId, out _);
        }

        client.PoolIds.TryRemove(poolId, out _);
    }

    private void RemoveClient(ClientConnection client)
    {
        clients.TryRemove(client.Id, out _);

        foreach(var poolId in client.PoolIds.Keys.ToArray())
            LeaveRoom(client, poolId);
    }

    private async Task CloseSlowClient(ClientConnection client)
    {
        RemoveClient(client);
        client.Complete();

        try
        {
            if(client.Socket.State == WebSocketState.Open)
                await client.Socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Send queue full", CancellationToken.None);
        }

        catch(Exception ex)
        {
            logger.Debug(ex, $"Failed to close slow WebSocket client {client.Id}");
        }
    }

    private static string GetPoolId<T>(T notification)
    {
        return notification switch
        {
            BlockNotification x => x.PoolId,
            PaymentNotification x => x.PoolId,
            ChainHeightStatsNotification x => x.PoolId,
            BlockFoundStatsNotification x => x.PoolId,
            CycleStatsNotification x => x.PoolId,
            _ => null,
        };
    }

    private sealed class ClientConnection
    {
        public ClientConnection(WebSocket socket)
        {
            Socket = socket;
        }

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public WebSocket Socket { get; }
        public ConcurrentDictionary<string, byte> PoolIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Channel<string> Outbound { get; } = Channel.CreateBounded<string>(new BoundedChannelOptions(SendQueueLimit)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

        public bool Enqueue(string msg)
        {
            return Outbound.Writer.TryWrite(msg);
        }

        public void Complete()
        {
            Outbound.Writer.TryComplete();
        }
    }
}
