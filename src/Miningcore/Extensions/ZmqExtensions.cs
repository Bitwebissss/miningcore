using System.Collections.Concurrent;
using System.Security.Cryptography;
using NetMQ;
using NLog;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace Miningcore.Extensions;

public static class ZmqExtensions
{
    private record KeyData(byte[] PubKey, byte[] SecretKey);

    private static readonly ConcurrentDictionary<string, KeyData> knownKeys = new();

    // Ephemeral client keypair – generated once per process for CURVE client sockets.
    private static readonly Lazy<KeyData> ownKey = new(() =>
    {
        var (pub, sec) = GenerateCurveKeypair();
        return new KeyData(pub, sec);
    });

    private const int PasswordIterations = 5000;
    private static readonly byte[] noSalt = new byte[32];

    private static byte[] DeriveKey(string password, int length = 32)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            System.Text.Encoding.UTF8.GetBytes(password), noSalt, PasswordIterations, HashAlgorithmName.SHA256, length);
    }

    /// <summary>Generates a random X25519 keypair suitable for CurveZMQ.</summary>
    public static (byte[] PublicKey, byte[] SecretKey) GenerateCurveKeypair()
    {
        var secretKey = new byte[X25519.ScalarSize];
        RandomNumberGenerator.Fill(secretKey);

        // RFC 7748 clamping
        secretKey[0]  &= 248;
        secretKey[31] &= 127;
        secretKey[31] |= 64;

        var publicKey = new byte[X25519.PointSize];
        // ScalarMultBase(k, kOff, r, rOff): k = scalar input (secret), r = point output (public)
        X25519.ScalarMultBase(secretKey, 0, publicKey, 0);
        return (publicKey, secretKey);
    }

    private static byte[] DerivePublicKey(byte[] secretKey)
    {
        var pub = new byte[X25519.PointSize];
        // ScalarMultBase(k, kOff, r, rOff): k = scalar input (secret), r = point output (public)
        X25519.ScalarMultBase(secretKey, 0, pub, 0);
        return pub;
    }

    /// <summary>
    /// Configures server-side CurveZMQ on a NetMQ socket using a shared password.
    /// Returns the 32-byte server public key, or null when keyPlain is empty/null.
    /// </summary>
    public static byte[] SetupCurveTlsServer(this NetMQSocket socket, string keyPlain, ILogger logger)
    {
        if(keyPlain == null)
            return null;

        keyPlain = keyPlain.Trim();

        if(string.IsNullOrEmpty(keyPlain))
            return null;

        if(!knownKeys.TryGetValue(keyPlain, out var keys))
        {
            var sec = DeriveKey(keyPlain, 32);
            var pub = DerivePublicKey(sec);
            keys = new KeyData(pub, sec);
            knownKeys[keyPlain] = keys;
        }

        socket.Options.CurveServer = true;
        socket.Options.CurveCertificate = new NetMQCertificate(keys.SecretKey, keys.PubKey);
        return keys.PubKey;
    }

    /// <summary>
    /// Configures client-side CurveZMQ on a NetMQ socket using a shared password.
    /// No-op when keyPlain is empty/null.
    /// </summary>
    public static void SetupCurveTlsClient(this NetMQSocket socket, string keyPlain, ILogger logger)
    {
        if(keyPlain == null)
            return;

        keyPlain = keyPlain.Trim();

        if(string.IsNullOrEmpty(keyPlain))
            return;

        if(!knownKeys.TryGetValue(keyPlain, out var keys))
        {
            var sec = DeriveKey(keyPlain, 32);
            var pub = DerivePublicKey(sec);
            keys = new KeyData(pub, sec);
            knownKeys[keyPlain] = keys;
        }

        socket.Options.CurveServer = false;
        socket.Options.CurveServerKey = keys.PubKey;
        socket.Options.CurveCertificate = new NetMQCertificate(ownKey.Value.SecretKey, ownKey.Value.PubKey);
    }
}
