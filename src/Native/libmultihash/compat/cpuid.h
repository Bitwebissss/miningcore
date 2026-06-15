// Copyright (c) 2017-2022 The Bitcoin Core developers
// Distributed under the MIT software license, see the accompanying
// file COPYING or http://www.opensource.org/licenses/mit-license.php.

#ifndef BITCOIN_COMPAT_CPUID_H
#define BITCOIN_COMPAT_CPUID_H

// x86/x64 detection: GCC/Clang use __x86_64__ / __i386__,
// MSVC uses _M_X64 / _M_AMD64 / _M_IX86.
#if defined(__x86_64__) || defined(__amd64__) || defined(__i386__) || \
    defined(_M_X64)    || defined(_M_AMD64)   || defined(_M_IX86)
#define HAVE_GETCPUID

#include <cstdint>

#if defined(_MSC_VER)
// MSVC: use __cpuidex intrinsic from <intrin.h>
#include <intrin.h>

static inline void GetCPUID(uint32_t leaf, uint32_t subleaf,
                             uint32_t& a, uint32_t& b,
                             uint32_t& c, uint32_t& d)
{
    int info[4];
    __cpuidex(info, static_cast<int>(leaf), static_cast<int>(subleaf));
    a = static_cast<uint32_t>(info[0]);
    b = static_cast<uint32_t>(info[1]);
    c = static_cast<uint32_t>(info[2]);
    d = static_cast<uint32_t>(info[3]);
}

#else
// GCC / Clang: use <cpuid.h> __cpuid_count (supports subleafs).
#include <cpuid.h>

static inline void GetCPUID(uint32_t leaf, uint32_t subleaf,
                             uint32_t& a, uint32_t& b,
                             uint32_t& c, uint32_t& d)
{
#ifdef __GNUC__
    __cpuid_count(leaf, subleaf, a, b, c, d);
#else
    __asm__ ("cpuid" : "=a"(a), "=b"(b), "=c"(c), "=d"(d) : "0"(leaf), "2"(subleaf));
#endif
}

#endif // _MSC_VER

#endif // x86 detection
#endif // BITCOIN_COMPAT_CPUID_H
