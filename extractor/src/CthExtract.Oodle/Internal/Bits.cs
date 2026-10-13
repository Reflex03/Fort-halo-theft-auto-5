// C# port of ooz (https://github.com/powzix/ooz, commit 05038060, GPL-3.0-or-later).
// Copyright (C) 2016, Powzix (original C++); C# port (C) 2026 Craft Theft Halo contributors.
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General
// Public License as published by the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. See extractor/vendor/ooz/LICENSE and VENDORED.md.

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace CthExtract.Oodle.Internal;

/// <summary>MSB-first bit reader (ooz <c>BitReader</c>). <see cref="BitPos"/> is where the next byte lands in
/// <see cref="Bits"/>; after a refill at least 24 bits are valid. Reads past <see cref="PEnd"/> yield zero bytes, so
/// this reader never touches memory outside [start, PEnd) (forward) or [PEnd, start) (backward).</summary>
internal unsafe struct BitReader
{
    public byte* P;
    public byte* PEnd;
    public uint Bits;
    public int BitPos;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Refill()
    {
        while (BitPos > 0)
        {
            Bits |= (uint)(P < PEnd ? *P : 0) << BitPos;
            BitPos -= 8;
            P++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RefillBackwards()
    {
        while (BitPos > 0)
        {
            P--;
            Bits |= (uint)(P >= PEnd ? *P : 0) << BitPos;
            BitPos -= 8;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBit()
    {
        Refill();
        int r = (int)(Bits >> 31);
        Bits <<= 1;
        BitPos += 1;
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBitNoRefill()
    {
        int r = (int)(Bits >> 31);
        Bits <<= 1;
        BitPos += 1;
        return r;
    }

    /// <summary>Reads n (1..32) bits without refilling. C# masks shift counts like x86, matching the C++ build.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBitsNoRefill(int n)
    {
        int r = (int)(Bits >> (32 - n));
        Bits <<= n;
        BitPos += n;
        return r;
    }

    /// <summary>Reads n (0..31) bits without refilling; n may be zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBitsNoRefillZero(int n)
    {
        int r = (int)(Bits >> 1 >> (31 - n));
        Bits <<= n;
        BitPos += n;
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadMoreThan24Bits(int n)
    {
        uint rv;
        if (n <= 24)
        {
            rv = (uint)ReadBitsNoRefillZero(n);
        }
        else
        {
            rv = (uint)ReadBitsNoRefill(24) << (n - 24);
            Refill();
            rv += (uint)ReadBitsNoRefill(n - 24);
        }
        Refill();
        return rv;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadMoreThan24BitsB(int n)
    {
        uint rv;
        if (n <= 24)
        {
            rv = (uint)ReadBitsNoRefillZero(n);
        }
        else
        {
            rv = (uint)ReadBitsNoRefill(24) << (n - 24);
            RefillBackwards();
            rv += (uint)ReadBitsNoRefill(n - 24);
        }
        RefillBackwards();
        return rv;
    }

    /// <summary>Offset code parametrized by v (Kraken "old" distances), forward stream.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadDistance(uint v)
    {
        uint w, m, n, rv;
        if (v < 0xF0)
        {
            n = (v >> 4) + 4;
            w = BitOperations.RotateLeft(Bits | 1, (int)n);
            BitPos += (int)n;
            m = (2u << (int)n) - 1;
            Bits = w & ~m;
            rv = ((w & m) << 4) + (v & 0xF) - 248;
        }
        else
        {
            n = v - 0xF0 + 4;
            w = BitOperations.RotateLeft(Bits | 1, (int)n);
            BitPos += (int)n;
            m = (2u << (int)n) - 1;
            Bits = w & ~m;
            rv = 8322816 + ((w & m) << 12);
            Refill();
            rv += Bits >> 20;
            BitPos += 12;
            Bits <<= 12;
        }
        Refill();
        return rv;
    }

    /// <summary>Offset code parametrized by v, backward stream.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadDistanceB(uint v)
    {
        uint w, m, n, rv;
        if (v < 0xF0)
        {
            n = (v >> 4) + 4;
            w = BitOperations.RotateLeft(Bits | 1, (int)n);
            BitPos += (int)n;
            m = (2u << (int)n) - 1;
            Bits = w & ~m;
            rv = ((w & m) << 4) + (v & 0xF) - 248;
        }
        else
        {
            n = v - 0xF0 + 4;
            w = BitOperations.RotateLeft(Bits | 1, (int)n);
            BitPos += (int)n;
            m = (2u << (int)n) - 1;
            Bits = w & ~m;
            rv = 8322816 + ((w & m) << 12);
            RefillBackwards();
            rv += Bits >> (32 - 12);
            BitPos += 12;
            Bits <<= 12;
        }
        RefillBackwards();
        return rv;
    }

    /// <summary>Length code, forward. Returns false on an over-long prefix (also when Bits == 0, where the C++
    /// code's BitScanReverse is undefined).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadLength(out uint v)
    {
        int n = BitOperations.LeadingZeroCount(Bits);
        if (n > 12) { v = 0; return false; }
        BitPos += n;
        Bits <<= n;
        Refill();
        n += 7;
        BitPos += n;
        v = (Bits >> (32 - n)) - 64;
        Bits <<= n;
        Refill();
        return true;
    }

    /// <summary>Length code, backward.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadLengthB(out uint v)
    {
        int n = BitOperations.LeadingZeroCount(Bits);
        if (n > 12) { v = 0; return false; }
        BitPos += n;
        Bits <<= n;
        RefillBackwards();
        n += 7;
        BitPos += n;
        v = (Bits >> (32 - n)) - 64;
        Bits <<= n;
        RefillBackwards();
        return true;
    }

    /// <summary>ooz <c>BitReader_ReadFluff</c>: truncated-binary count of the "range" codes.</summary>
    public int ReadFluff(int numSymbols)
    {
        if (numSymbols == 256)
            return 0;
        int x = 257 - numSymbols;
        if (x > numSymbols)
            x = numSymbols;
        x *= 2;
        int y = BitOperations.Log2((uint)(x - 1)) + 1;
        uint v = Bits >> (32 - y);
        uint z = (1u << y) - (uint)x;
        if ((v >> 1) >= z)
        {
            Bits <<= y;
            BitPos += y;
            return (int)(v - z);
        }
        Bits <<= (y - 1);
        BitPos += y - 1;
        return (int)(v >> 1);
    }
}

/// <summary>Byte-granular reader used by the Golomb-Rice code-length decoders (ooz <c>BitReader2</c>).</summary>
internal unsafe struct BitReader2
{
    public byte* P;
    public byte* PEnd;
    public uint BitPos;
}

internal static unsafe class Mem
{
    public const ulong Lo7 = 0x7F7F7F7F7F7F7F7FUL;
    public const ulong Hi1 = 0x8080808080808080UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint R16(byte* p) => Unsafe.ReadUnaligned<ushort>(p);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint R32(byte* p) => Unsafe.ReadUnaligned<uint>(p);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong R64(byte* p) => Unsafe.ReadUnaligned<ulong>(p);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void W32(byte* p, uint v) => Unsafe.WriteUnaligned(p, v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void W64(byte* p, ulong v) => Unsafe.WriteUnaligned(p, v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint BSwap32(uint v) => BinaryPrimitives.ReverseEndianness(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong BSwap64(ulong v) => BinaryPrimitives.ReverseEndianness(v);

    /// <summary>COPY_64: one 8-byte load, then one 8-byte store (same order as the C++ macro, which matters for
    /// overlapping match copies).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Copy64(byte* d, byte* s) => Unsafe.WriteUnaligned(d, Unsafe.ReadUnaligned<ulong>(s));

    /// <summary>COPY_64_ADD: d[i] = s[i] + t[i] (mod 256) for 8 bytes; both loads happen before the store.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Copy64Add(byte* d, byte* s, byte* t)
    {
        ulong a = Unsafe.ReadUnaligned<ulong>(s);
        ulong b = Unsafe.ReadUnaligned<ulong>(t);
        if (Vector128.IsHardwareAccelerated)
        {
            // paddb on the low 8 bytes, like ooz's _mm_add_epi8 on _mm_loadl_epi64
            Vector128<byte> sum = Vector128.CreateScalarUnsafe(a).AsByte() + Vector128.CreateScalarUnsafe(b).AsByte();
            Unsafe.WriteUnaligned(d, sum.AsUInt64().ToScalar());
        }
        else
        {
            Unsafe.WriteUnaligned(d, ((a & Lo7) + (b & Lo7)) ^ ((a ^ b) & Hi1));
        }
    }

    /// <summary>memcpy/memmove (Buffer.MemoryCopy is overlap-safe).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Move(byte* dst, byte* src, nint len)
    {
        if (len > 0)
            Buffer.MemoryCopy(src, dst, len, len);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Set(byte* dst, byte value, nint len)
    {
        if (len > 0)
            Unsafe.InitBlockUnaligned(dst, value, (uint)len);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte* AlignPointer(byte* p, nint align) => (byte*)(((nint)p + (align - 1)) & ~(align - 1));
}
