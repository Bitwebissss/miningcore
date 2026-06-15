using Miningcore.Contracts;
using Miningcore.Native;

namespace Miningcore.Crypto.Hashing.Algorithms;

/// <summary>
/// Generic parametric Argon2 hasher backed by Bitweb's optimised native
/// C++ engine (SSE2 / SSSE3 / AVX2 / AVX-512 / NEON runtime dispatch).
///
/// Use this to support future Argon2-based altcoins without recompiling
/// the native library.  Register in coins.json with:
///
///   "headerHasher": { "hash": "argon2-generic" }
///
/// and pass parameters via the <c>extra</c> array in this order:
///   [0] uint tCost   – number of passes
///   [1] uint mCost   – memory in KiB
///   [2] uint lanes   – degree of parallelism
///   [3] uint typeId  – 0 = Argon2d, 1 = Argon2i, 2 = Argon2id
///   [4] uint version – 0x10 (v1.0) or 0x13 (v1.3)
///
/// If no extra args are supplied the method falls back to Bitweb's
/// consensus params (Argon2id, t=3, m=1024, lanes=1, v=0x13).
/// </summary>
[Identifier("argon2-generic")]
public unsafe class Argon2Generic : IHashAlgorithm
{
    // Bitweb defaults — used when no params are passed
    private const uint DefaultTCost   = 3;
    private const uint DefaultMCost   = 1024;
    private const uint DefaultLanes   = 1;
    private const uint DefaultTypeId  = 2;    // Argon2id
    private const uint DefaultVersion = 0x13; // ARGON2_VERSION_13

    public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
    {
        Contract.Requires<ArgumentException>(result.Length >= 32);

        uint tCost   = extra.Length > 0 ? Convert.ToUInt32(extra[0]) : DefaultTCost;
        uint mCost   = extra.Length > 1 ? Convert.ToUInt32(extra[1]) : DefaultMCost;
        uint lanes   = extra.Length > 2 ? Convert.ToUInt32(extra[2]) : DefaultLanes;
        uint typeId  = extra.Length > 3 ? Convert.ToUInt32(extra[3]) : DefaultTypeId;
        uint version = extra.Length > 4 ? Convert.ToUInt32(extra[4]) : DefaultVersion;

        fixed(byte* input = data)
        fixed(byte* output = result)
        {
            Multihash.argon2_generic(input, output, (uint) data.Length,
                tCost, mCost, lanes, typeId, version);
        }
    }
}
