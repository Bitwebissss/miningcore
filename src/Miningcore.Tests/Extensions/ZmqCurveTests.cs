using System.Linq;
using System.Threading.Tasks;
using Miningcore.Extensions;
using NetMQ;
using NetMQ.Sockets;
using NLog;
using Xunit;

namespace Miningcore.Tests.Extensions;

public class ZmqCurveTests
{
    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

    [Fact]
    public void GenerateCurveKeypair_Returns32ByteNonZeroKeys()
    {
        var (pub, sec) = ZmqExtensions.GenerateCurveKeypair();

        Assert.Equal(32, pub.Length);
        Assert.Equal(32, sec.Length);
        Assert.False(pub.All(b => b == 0), "Public key is all zeros – X25519 param order wrong");
        Assert.False(sec.All(b => b == 0), "Secret key is all zeros");
    }

    [Fact]
    public void GenerateCurveKeypair_TwoCalls_ProduceDifferentKeys()
    {
        var (pub1, _) = ZmqExtensions.GenerateCurveKeypair();
        var (pub2, _) = ZmqExtensions.GenerateCurveKeypair();

        Assert.False(pub1.SequenceEqual(pub2), "Two keypair calls produced identical keys");
    }

    [Fact]
    public void SetupCurveTlsServer_EmptyKey_ReturnsNull()
    {
        using var socket = new PublisherSocket();
        var result = socket.SetupCurveTlsServer("", logger);

        Assert.Null(result);
        Assert.False(socket.Options.CurveServer);
    }

    [Fact]
    public void SetupCurveTlsServer_NullKey_ReturnsNull()
    {
        using var socket = new PublisherSocket();
        var result = socket.SetupCurveTlsServer(null, logger);

        Assert.Null(result);
        Assert.False(socket.Options.CurveServer);
    }

    [Fact]
    public void SetupCurveTlsServer_WithKey_ReturnsPubKey()
    {
        using var socket = new PublisherSocket();
        var pubKey = socket.SetupCurveTlsServer("testpassword", logger);

        Assert.NotNull(pubKey);
        Assert.Equal(32, pubKey.Length);
        Assert.True(socket.Options.CurveServer);
        Assert.False(pubKey.All(b => b == 0), "Returned public key is all zeros");
    }

    [Fact]
    public void SetupCurveTlsServer_SamePassword_ReturnsSameKey()
    {
        using var s1 = new PublisherSocket();
        using var s2 = new PublisherSocket();

        var key1 = s1.SetupCurveTlsServer("same-pw-test-abc", logger);
        var key2 = s2.SetupCurveTlsServer("same-pw-test-abc", logger);

        Assert.NotNull(key1);
        Assert.NotNull(key2);
        Assert.False(key1.All(b => b == 0), "Key is all zeros");
        Assert.True(key1.SequenceEqual(key2), "Same password must produce same key");
    }

    [Fact]
    public void SetupCurveTlsServer_DifferentPasswords_ReturnDifferentKeys()
    {
        using var s1 = new PublisherSocket();
        using var s2 = new PublisherSocket();

        var key1 = s1.SetupCurveTlsServer("unique-pw-alpha-111", logger);
        var key2 = s2.SetupCurveTlsServer("unique-pw-beta-222", logger);

        Assert.NotNull(key1);
        Assert.NotNull(key2);
        Assert.False(key1.All(b => b == 0), "Key1 is all zeros");
        Assert.False(key2.All(b => b == 0), "Key2 is all zeros");
        Assert.False(key1.SequenceEqual(key2), "Different passwords must produce different keys");
    }

    [Fact]
    public void SetupCurveTlsClient_EmptyKey_DoesNotSetCurve()
    {
        using var socket = new SubscriberSocket();
        socket.SetupCurveTlsClient("", logger);

        var serverKey = socket.Options.CurveServerKey;
        Assert.True(serverKey == null || serverKey.All(b => b == 0));
    }

    [Fact]
    public void SetupCurveTlsClient_WithKey_SetsCurveServerKey()
    {
        using var socket = new SubscriberSocket();
        socket.SetupCurveTlsClient("unique-client-pw-xyz", logger);

        var serverKey = socket.Options.CurveServerKey;
        Assert.NotNull(serverKey);
        Assert.Equal(32, serverKey.Length);
        Assert.False(serverKey.All(b => b == 0), "CurveServerKey is all zeros");
        Assert.False(socket.Options.CurveServer);
    }

    [Fact]
    public void CurveTls_ServerClientKeyMatch_SamePassword()
    {
        // Server public key must match what the client sets as CurveServerKey –
        // this is the core handshake requirement for relay-to-relay Curve encryption.
        using var server = new PublisherSocket();
        using var client = new SubscriberSocket();

        var serverPubKey = server.SetupCurveTlsServer("relay-handshake-pw", logger);
        client.SetupCurveTlsClient("relay-handshake-pw", logger);

        Assert.NotNull(serverPubKey);
        Assert.False(serverPubKey.All(b => b == 0), "Server public key is all zeros – crypto broken");

        var clientServerKey = client.Options.CurveServerKey;
        Assert.NotNull(clientServerKey);
        Assert.True(serverPubKey.SequenceEqual(clientServerKey),
            "Client CurveServerKey must equal server public key – handshake will fail otherwise");
    }

    [Fact]
    public async Task CurveTls_PubSubRoundTrip_WithEncryption()
    {
        // Full pool relay simulation: encrypted pub/sub over loopback.
        const string password = "pool-relay-integration-test";
        const string endpoint = "tcp://127.0.0.1:15765";

        using var pub = new PublisherSocket();
        using var sub = new SubscriberSocket();

        pub.SetupCurveTlsServer(password, logger);
        sub.SetupCurveTlsClient(password, logger);

        pub.Bind(endpoint);
        sub.Connect(endpoint);
        sub.SubscribeToAnyTopic();

        // Retry-send until subscriber receives — avoids flakiness caused by CURVE handshake
        // taking longer than a fixed delay under load. PubSub silently drops messages sent
        // before the handshake completes, so we keep sending until the subscriber confirms.
        var msg = new NetMQMessage();
        bool received = false;

        for(var attempt = 0; attempt < 10 && !received; attempt++)
        {
            await Task.Delay(300);
            pub.SendMoreFrame("poolid").SendFrame("share-data");
            received = sub.TryReceiveMultipartMessage(System.TimeSpan.FromMilliseconds(500), ref msg, 2);
        }

        Assert.True(received, "Encrypted relay message not received – Curve handshake or key derivation failed");
        Assert.Equal("poolid", msg[0].ConvertToString());
        Assert.Equal("share-data", msg[1].ConvertToString());
    }

    [Fact]
    public async Task CurveTls_PubSubRoundTrip_WithoutEncryption()
    {
        // Baseline: plain pub/sub must work (relay without SharedEncryptionKey in config).
        const string endpoint = "tcp://127.0.0.1:15766";

        using var pub = new PublisherSocket();
        using var sub = new SubscriberSocket();

        pub.Bind(endpoint);
        sub.Connect(endpoint);
        sub.SubscribeToAnyTopic();

        await Task.Delay(200);

        pub.SendMoreFrame("poolid").SendFrame("share-data");

        var msg = new NetMQMessage();
        var received = sub.TryReceiveMultipartMessage(System.TimeSpan.FromSeconds(4), ref msg, 2);

        Assert.True(received, "Plain relay message not received");
        Assert.Equal("poolid", msg[0].ConvertToString());
        Assert.Equal("share-data", msg[1].ConvertToString());
    }
}
