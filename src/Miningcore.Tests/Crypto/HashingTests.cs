using System;
using System.Linq;
using System.Text;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Tests.Util;
using Xunit;
using Xunit.Abstractions;

namespace Miningcore.Tests.Crypto;

public class HashingTests : TestBase
{
    private static readonly byte[] testValue = Enumerable.Repeat((byte) 0x80, 32).ToArray();

    // some algos need 80 byte input buffers
    private static readonly byte[] testValue2 = Enumerable.Repeat((byte) 0x80, 80).ToArray();

    // 80-byte headers for Argon2 Bitweb tests
    private static readonly byte[] header80Zero = new byte[80];
    private static readonly byte[] header80AB   = Enumerable.Repeat((byte) 0xAB, 80).ToArray();

    [Fact]
    public void Scrypt_Hash()
    {
        var hasher = new Scrypt(1024, 1);
        var hash = new byte[32];
        hasher.Digest(testValue, hash);
        var result = hash.ToHexString();

        Assert.Equal("b546d334422ff5fff98e8ba847a55bbc06271c64bb5e21107b1b225f6579d40a", result);
    }

    [Fact]
    public void Sha256D_Hash()
    {
        var hasher = new Sha256D();
        var hash = new byte[32];
        hasher.Digest(testValue, hash);
        var result = hash.ToHexString();

        Assert.Equal("4f4eb6dbba8198745a278997e154e8309b571259e33fce4d3a31adea39dc9173", result);
    }

    [Fact]
    public void Sha256DT_Hash()
    {
        var hasher = new Sha256DT();
        var hash = new byte[32];
        hasher.Digest(testValue2, hash);
        var result = hash.ToHexString();
        Assert.Equal("bf4735b3a0feebe83727a7a2327f8223eec7484190e8dd52611ce75b045a2e75", result);
    }

    [Fact]
    public void Sha256S_Hash()
    {
        var hasher = new Sha256S();
        var hash = new byte[32];
        hasher.Digest(testValue, hash);
        var result = hash.ToHexString();

        Assert.Equal("bd75a82b9957d6d043076dea52262635042693f1fe23bcadadaecc908e1e5cc6", result);
    }

    [Fact]
    public void Sha512256D_Hash()
    {
        var hasher = new Sha512256D();
        var hash = new byte[32];
        hasher.Digest(testValue, hash);
        var result = hash.ToHexString();

        Assert.Equal("6b86ce4bf945d8e935d51db4e32589acf6dbcda58ca1cef7568d52f704c46d7f", result);
    }

    [Fact]
    public void Heavy_Hash()
    {
        var hasher = new HeavyHash();
        var hash = new byte[32];
        hasher.Digest(testValue, hash);
        var result = hash.ToHexString();

        Assert.Equal("e89c26771f3fda42e6f8ed82ca888f805fa15013d8543ab2692904095c6d3dc3", result);
    }

    [Fact]
    public void DigestReverser_Hash()
    {
        var hasher = new DigestReverser(new Sha256S());
        var hash = new byte[32];
        hasher.Digest(testValue, hash);
        var result = hash.ToHexString();

        Assert.Equal("c65c1e8e90ccaeadadbc23fef193260435262652ea6d0743d0d657992ba875bd", result);
    }

    [Fact]
    public void DummyHasher_Should_Always_Throw()
    {
        var hasher = new Null();
        Assert.Throws<InvalidOperationException>(() => hasher.Digest(new byte[23], null));
        Assert.Throws<InvalidOperationException>(() => hasher.Digest(null, null));
    }

    //   Argon2id, t=3, m=1024 KiB, lanes=1, version=0x13, pwd==salt==input.

    [Fact]
    public void Argon2idBitweb_KnownVector_AllZero()
    {
        var hasher = new Argon2idBitweb();
        var hash = new byte[32];
        hasher.Digest(header80Zero, hash);
        Assert.Equal("3bb15018af629a335077c8c15412d2830e8c67452fa9a54ac87165024a910a8f", hash.ToHexString());
    }

    [Fact]
    public void Argon2idBitweb_KnownVector_AB()
    {
        var hasher = new Argon2idBitweb();
        var hash = new byte[32];
        hasher.Digest(header80AB, hash);
        Assert.Equal("d3b994430326a33cf1fcdab01cfcb957b0322adf90a3dca22ace8f94dd4666fb", hash.ToHexString());
    }

    [Fact]
    public void Argon2idBitweb_Deterministic()
    {
        var hasher = new Argon2idBitweb();
        var hash1 = new byte[32];
        var hash2 = new byte[32];
        hasher.Digest(header80AB, hash1);
        hasher.Digest(header80AB, hash2);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void Argon2idBitweb_DifferentInput_DifferentHash()
    {
        var hasher = new Argon2idBitweb();
        var hash1 = new byte[32];
        var hash2 = new byte[32];
        hasher.Digest(header80Zero, hash1);
        hasher.Digest(header80AB,   hash2);
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void Argon2idBitweb_OutputNonZero()
    {
        var hasher = new Argon2idBitweb();
        var hash = new byte[32];
        hasher.Digest(header80Zero, hash);
        Assert.False(hash.All(b => b == 0), "Hash must not be all-zero");
    }

    [Fact]
    public void Argon2Generic_BitwebParams_EqualsArgon2idBitweb()
    {
        // argon2_generic with Bitweb consensus params must produce identical output
        var bitweb  = new Argon2idBitweb();
        var generic = new Argon2Generic();

        var expected = new byte[32];
        var actual   = new byte[32];

        bitweb.Digest(header80AB, expected);
        // extra[] order: tCost, mCost, lanes, typeId(2=Argon2id), version(0x13)
        generic.Digest(header80AB, actual, 3u, 1024u, 1u, 2u, 0x13u);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Argon2Generic_DifferentParams_DifferentHash()
    {
        var generic = new Argon2Generic();
        var hash1 = new byte[32];
        var hash2 = new byte[32];

        // Bitweb params
        generic.Digest(header80AB, hash1, 3u, 1024u, 1u, 2u, 0x13u);
        // Different: Argon2d, t=1, m=256
        generic.Digest(header80AB, hash2, 1u,  256u, 1u, 0u, 0x13u);

        Assert.NotEqual(hash1, hash2);
    }
}
