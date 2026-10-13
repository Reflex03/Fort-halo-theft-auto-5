#!/usr/bin/env bash
# Builds the native reference decoder used to check CthExtract.Oodle bit-exact: powzix/ooz (GPL-3.0) at the commit
# the port is based on, compiled with g++ on Linux x86-64. Decoding only (ooz's -z needs Oodle's DLL; never used).
#
#   tools/build-native-ooz.sh <work-dir>      -> <work-dir>/ooz-ref           (unmodified decoder logic)
#                                             -> <work-dir>/ooz-ref-zaofix    (+ zao/ooz's Mermaid temp-size fix)
#
# Only portability shims are added (MSVC intrinsics -> GCC builtins, a timer, no DLL loading); kraken.cpp's decoder
# functions are compiled as published. Usage of the result:  ooz-ref -d -c -q file.ooz > out   (file = [u64 size][stream])
set -euo pipefail
WORK=${1:?work dir}
COMMIT=05038060aa68f9187ae9923b2388ca8db40e58d1
mkdir -p "$WORK"
if [ ! -d "$WORK/ooz/.git" ]; then
  git clone -q https://github.com/powzix/ooz.git "$WORK/ooz"
fi
git -C "$WORK/ooz" checkout -q "$COMMIT"
SRC="$WORK/src"
rm -rf "$SRC"; mkdir -p "$SRC"
cp "$WORK/ooz/kraken.cpp" "$WORK/ooz/lzna.cpp" "$WORK/ooz/bitknit.cpp" "$SRC/"
cat > "$SRC/stdafx.h" <<'H'
#pragma once
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include <stdint.h>
#include <time.h>
#include <sys/stat.h>
#include <emmintrin.h>
#define __forceinline inline
#define _byteswap_ushort(x) __builtin_bswap16((uint16_t)(x))
#define _byteswap_ulong(x) __builtin_bswap32((uint32_t)(x))
#define _byteswap_uint64(x) __builtin_bswap64((uint64_t)(x))
#define _BitScanForward(dst, x) (*(dst) = __builtin_ctz(x))
#define _BitScanReverse(dst, x) (*(dst) = (__builtin_clz(x) ^ 31))
static inline uint32_t _rotl(uint32_t x, int n) { n &= 31; return n ? (x << n) | (x >> (32 - n)) : x; }
typedef uint8_t byte; typedef uint8_t uint8; typedef uint32_t uint32; typedef uint64_t uint64; typedef int64_t int64;
typedef int32_t int32; typedef uint16_t uint16; typedef int16_t int16; typedef unsigned int uint;
typedef int64_t __int64;
typedef uint64_t LARGE_INTEGER;
static inline void QueryPerformanceCounter(LARGE_INTEGER *a) { struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts); *a = (uint64_t)ts.tv_sec * 1000000000ull + ts.tv_nsec; }
static inline void QueryPerformanceFrequency(LARGE_INTEGER *a) { *a = 1000000000ull; }
#define WINAPI
typedef void *HINSTANCE;
static inline HINSTANCE LoadLibraryA(const char *s) { (void)s; return 0; }
static inline void *GetProcAddress(HINSTANCE h, const char *s) { (void)h; (void)s; return 0; }
H
# string literals assigned to char* in the CLI parser are an error in C++11 and later
sed -i 's/^char \*verifyfolder;/const char *verifyfolder;/; s/    char \*s = argv\[i\], c;/    const char *s = argv[i]; char c;/' "$SRC/kraken.cpp"
g++ -O2 -std=c++14 -w -I"$SRC" -o "$WORK/ooz-ref" "$SRC/kraken.cpp" "$SRC/lzna.cpp" "$SRC/bitknit.cpp"
cp "$SRC/kraken.cpp" "$SRC/kraken-zaofix.cpp"
sed -i 's/int temp_usage = 2 \* dst_count + 32;/int temp_usage = 2 * dst_count + 32 + 0x4000;/' "$SRC/kraken-zaofix.cpp"
g++ -O2 -std=c++14 -w -I"$SRC" -o "$WORK/ooz-ref-zaofix" "$SRC/kraken-zaofix.cpp" "$SRC/lzna.cpp" "$SRC/bitknit.cpp"
echo "built $WORK/ooz-ref and $WORK/ooz-ref-zaofix"
