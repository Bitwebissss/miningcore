#!/bin/bash

OutDir=$1

AES=$(../Native/check_cpu.sh aes && echo -maes || echo)
SSE2=$(../Native/check_cpu.sh sse2 && echo -msse2 || echo)
SSE3=$(../Native/check_cpu.sh sse3 && echo -msse3 || echo)
SSSE3=$(../Native/check_cpu.sh ssse3 && echo -mssse3 || echo)
PCLMUL=$(../Native/check_cpu.sh pclmul && echo -mpclmul || echo)
AVX=$(../Native/check_cpu.sh avx && echo -mavx || echo)
AVX2=$(../Native/check_cpu.sh avx2 && echo -mavx2 || echo)
AVX512F=$(../Native/check_cpu.sh avx512f && echo -mavx512f || echo)

export CPU_FLAGS="$AES $SSE2 $SSE3 $SSSE3 $PCLMUL $AVX $AVX2 $AVX512F"

HAVE_AES=$(../Native/check_cpu.sh aes && echo -D__AES__ || echo)
HAVE_SSE2=$(../Native/check_cpu.sh sse2 && echo -DHAVE_SSE2 || echo)
HAVE_SSE3=$(../Native/check_cpu.sh sse3 && echo -DHAVE_SSE3 || echo)
HAVE_SSSE3=$(../Native/check_cpu.sh ssse3 && echo -DHAVE_SSSE3 || echo)
HAVE_PCLMUL=$(../Native/check_cpu.sh pclmul && echo -DHAVE_PCLMUL || echo)
HAVE_AVX=$(../Native/check_cpu.sh avx && echo -DHAVE_AVX || echo)
HAVE_AVX2=$(../Native/check_cpu.sh avx2 && echo -DHAVE_AVX2 || echo)
HAVE_AVX512F=$(../Native/check_cpu.sh avx512f && echo -DHAVE_AVX512F || echo)

export HAVE_FEATURE="$HAVE_AES $HAVE_SSE2 $HAVE_SSE3 $HAVE_SSSE3 $HAVE_PCLMUL $HAVE_AVX $HAVE_AVX2 $HAVE_AVX512F"

(cd ../Native/libmultihash && make clean && make) && mv ../Native/libmultihash/libmultihash.so "$OutDir"
