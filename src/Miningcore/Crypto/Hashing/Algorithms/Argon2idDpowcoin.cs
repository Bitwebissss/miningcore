using Miningcore.Contracts;
using Miningcore.Native;

namespace Miningcore.Crypto.Hashing.Algorithms;

/// <summary>
/// Dpowcoin PoW hasher: two-round Argon2id, chained salt.
///   salt          = SHA-512( SHA-512( 80-byte header ) )         [64 bytes]
///   Round 1: t=2, m_cost=4096  KiB, lanes=2, v=0x13 -> 32 bytes  (becomes round-2 salt)
///   Round 2: t=2, m_cost=32768 KiB, lanes=2, v=0x13 -> 32 bytes  (final PoW hash)
///   pwd (both rounds) = 80-byte serialised block header.
///
/// Uses the same optimised native C++ backend as Bitweb (runtime SIMD dispatch:
/// SSE2 / SSSE3 / AVX2 / AVX-512 / NEON) via argon2id_dpowcoin_export.
///
/// [Identifier] is the canonical miner-facing algorithm name: it is what
/// CoinTemplate.GetAlgorithmName() returns from the pool API, which the
/// miner's auto-config consumes to fill in `-a <name>`. This MUST match
/// exactly what the coin's cpuminer-opt algo-gate registers for `-a`
/// (currently "dpowcoin", e.g. `cpuminer-aes-sse42 -a dpowcoin ...`).
/// </summary>
[Identifier("dpowcoin")]
public unsafe class Argon2idDpowcoin : IHashAlgorithm
{
    public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
    {
        Contract.Requires<ArgumentException>(result.Length >= 32);

        fixed(byte* input = data)
        fixed(byte* output = result)
        {
            Multihash.argon2id_dpowcoin(input, output, (uint) data.Length);
        }
    }
}
