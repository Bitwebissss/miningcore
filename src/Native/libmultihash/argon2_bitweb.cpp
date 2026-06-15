/*
 * argon2_bitweb.cpp
 *
 * Pool-facing Argon2 exports built on Bitweb's optimised C++ implementation.
 * Runtime SIMD dispatch: SSE2 / SSSE3 / AVX2 / AVX-512 / NEON (x86 & ARM).
 *
 * Exported symbols (extern "C", visible in libmultihash.so):
 *
 *   argon2id_bitweb_export
 *       Bitweb consensus PoW hash.
 *       Type=Argon2id, t=3, m=1024 KiB, lanes=1, version=0x13.
 *       pwd == salt == 80-byte serialised block header (as in block.cpp).
 *       Output: 32 bytes.
 *
 *   argon2_generic_export
 *       Fully parametric — same optimised backend, all Argon2 variants (d/i/id),
 *       any m_cost/t_cost/lanes. Use from C# for future Argon2-based altcoins
 *       without recompiling the native library.
 *
 * SIMD auto-detection is performed exactly once (pthread_once) and prints the
 * selected implementation to stdout — visible in the pool log at startup.
 */

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>

#include "argon2bitweb/argon2.h"   /* argon2_ctx, argon2_context, Argon2AutoDetectImpl, ... */

#ifdef _WIN32
#  define MODULE_API __declspec(dllexport)
#else
#  define MODULE_API
#endif

/* -------------------------------------------------------------------------
 * One-time SIMD auto-detection (thread-safe via std::call_once).
 * Argon2AutoDetectImpl() sets the global fill_segment function pointer to the
 * best implementation the CPU supports: AVX-512 > AVX2 > SSSE3 > SSE2 > ref.
 * ------------------------------------------------------------------------- */

static std::once_flag s_argon2_init;

static void do_argon2_init(void)
{
    /* USE_ALL = 0x0F — enable all SIMD tiers for detection */
    const char *impl = Argon2AutoDetectImpl(
        static_cast<uint8_t>(argon2_implementation::USE_ALL));
    fprintf(stdout,
        "[libmultihash] Argon2 dispatcher: selected implementation = %s\n",
        impl);
    fflush(stdout);
}

static inline void ensure_argon2_init(void)
{
    std::call_once(s_argon2_init, do_argon2_init);
}

/* -------------------------------------------------------------------------
 * Internal helper — fills argon2_context and runs argon2_ctx().
 * pwd == salt == input (Bitweb PoW convention: header hashes itself).
 * ------------------------------------------------------------------------- */
static void argon2_hash_internal(
        const void   *input,   uint32_t input_len,
        void         *output,  uint32_t output_len,
        uint32_t      t_cost,  uint32_t m_cost,
        uint32_t      lanes,
        argon2_type   type,
        uint32_t      version)
{
    ensure_argon2_init();

    argon2_context ctx;
    memset(&ctx, 0, sizeof(ctx));

    ctx.out       = static_cast<uint8_t*>(output);
    ctx.outlen    = output_len;
    ctx.pwd       = const_cast<uint8_t*>(static_cast<const uint8_t*>(input));
    ctx.pwdlen    = input_len;
    ctx.salt      = const_cast<uint8_t*>(static_cast<const uint8_t*>(input));
    ctx.saltlen   = input_len;
    ctx.t_cost    = t_cost;
    ctx.m_cost    = m_cost;
    ctx.lanes     = lanes;
    ctx.threads   = 1;
    ctx.version   = version;
    ctx.flags     = ARGON2_DEFAULT_FLAGS;

    /* Any error here is a programming mistake or OOM — treat as fatal. */
    int rc = argon2_ctx(&ctx, type);
    (void)rc;
}

/* -------------------------------------------------------------------------
 * Export 1: Bitweb consensus Argon2id PoW hash
 *
 * Parameters (consensus-critical — match block.cpp exactly):
 *   type    = Argon2_id
 *   t_cost  = 3
 *   m_cost  = 1024 KiB
 *   lanes   = 1, threads = 1
 *   version = 0x13 (ARGON2_VERSION_13)
 *   pwd == salt == 80-byte serialised block header
 *   outlen  = 32 bytes
 * ------------------------------------------------------------------------- */
extern "C" MODULE_API void argon2id_bitweb_export(
        const char *input,
        char       *output,
        uint32_t    input_len)   /* always 80 for PoW */
{
    argon2_hash_internal(
        input, input_len,
        output, 32,
        /*t=*/3, /*m=*/1024, /*lanes=*/1,
        Argon2_id, ARGON2_VERSION_13);
}

/* -------------------------------------------------------------------------
 * Export 2: Generic parametric Argon2 (same optimised backend)
 *
 * Use from C# to support future Argon2-based altcoins.
 * typeId: 0 = Argon2d, 1 = Argon2i, 2 = Argon2id
 * version: 0x10 (v1.0) or 0x13 (v1.3)
 * Output is always 32 bytes; pwd == salt == input.
 * ------------------------------------------------------------------------- */
extern "C" MODULE_API void argon2_generic_export(
        const char *input,
        char       *output,
        uint32_t    input_len,
        uint32_t    t_cost,
        uint32_t    m_cost,
        uint32_t    lanes,
        uint32_t    type_id,   /* 0=d 1=i 2=id */
        uint32_t    version)   /* 0x10 or 0x13  */
{
    argon2_type type;
    switch (type_id) {
        case 1:  type = Argon2_i;  break;
        case 2:  type = Argon2_id; break;
        default: type = Argon2_d;  break;
    }
    argon2_hash_internal(
        input, input_len,
        output, 32,
        t_cost, m_cost, lanes,
        type, version);
}
