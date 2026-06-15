using Miningcore.Contracts;
using Miningcore.Native;

namespace Miningcore.Crypto.Hashing.Algorithms;

/// <summary>
/// Bitweb PoW hasher: Argon2id with consensus-critical parameters.
///   t_cost  = 3  (time passes)
///   m_cost  = 1024 KiB
///   lanes   = 1
///   version = 0x13 (Argon2 v1.3)
///   pwd == salt == 80-byte serialised block header → 32-byte output.
///
/// Uses Bitweb's optimised native C++ backend with runtime SIMD dispatch
/// (SSE2 / SSSE3 / AVX2 / AVX-512 / NEON), initialised once at first call.
/// </summary>
[Identifier("argon2id1024")]
public unsafe class Argon2idBitweb : IHashAlgorithm
{
    public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
    {
        Contract.Requires<ArgumentException>(result.Length >= 32);

        fixed(byte* input = data)
        fixed(byte* output = result)
        {
            Multihash.argon2id_bitweb(input, output, (uint) data.Length);
        }
    }
}
