/*
Copyright 2017 Coin Foundry (coinfoundry.org)
Authors: Oliver Weichhold (oliver@weichhold.com)
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:
The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

#include "scryptn.h"
#include "sha256dt.h"
#include "sha512_256.h"
#include "heavyhash/heavyhash.h"
#include "yespower/yespower.h"

#ifdef _WIN32
#define MODULE_API __declspec(dllexport)
#else
#define MODULE_API
#endif

extern "C" MODULE_API void scrypt_export(const char* input, char* output, uint32_t N, uint32_t R, uint32_t input_len)
{
    scrypt_N_R_1_256(input, output, N, R, input_len);
}

extern "C" MODULE_API void sha512_256_export(const unsigned char* input, unsigned char* output, uint32_t input_len)
{
    sha512_256(input, input_len, output);
}

extern "C" MODULE_API void sha256dt_export(const char* input, char* output)
{
    sha256dt_hash(input, output);
}

extern "C" MODULE_API void heavyhash_export(const char* input, char* output, uint32_t input_len)
{
    heavyhash_hash(input, output, input_len);
}

extern "C" MODULE_API void yescryptR8_export(const char *input, char *output, uint32_t input_len)
{
    yescryptR8_hash(input, output, input_len);
}

extern "C" MODULE_API void yescryptR16_export(const char *input, char *output, uint32_t input_len)
{
    yescryptR16_hash(input, output, input_len);
}

extern "C" MODULE_API void yescryptR32_export(const char *input, char *output, uint32_t input_len)
{
    yescryptR32_hash(input, output, input_len);
}

extern "C" MODULE_API void yespower_export(const char *input, char *output, uint32_t input_len)
{
    yespower_hash(input, output, input_len);
}

extern "C" MODULE_API void yespowerR16_export(const char *input, char *output, uint32_t input_len)
{
    yespowerR16_hash(input, output, input_len);
}

/* Argon2 exports moved to argon2_bitweb.cpp:
 *   argon2id_bitweb_export  – Bitweb PoW (Argon2id, t=3, m=1024)
 *   argon2_generic_export   – parametric for future altcoins
 */
