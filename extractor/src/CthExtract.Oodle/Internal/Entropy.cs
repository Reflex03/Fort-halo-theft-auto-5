// C# port of ooz (https://github.com/powzix/ooz, commit 05038060, GPL-3.0-or-later).
// Copyright (C) 2016, Powzix (original C++); C# port (C) 2026 Craft Theft Halo contributors.
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General
// Public License as published by the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. See extractor/vendor/ooz/LICENSE and VENDORED.md.
//
// Entropy layer: Kraken_DecodeBytes and the coders it dispatches to (raw copy, Huffman with the old and new code
// length formats, tANS, RLE, recursive, multi-array). Changes against ooz are marked [CTH] and only reject states
// that valid streams never reach (bounded over-reads, table overflows, unbounded recursion).

using System.Numerics;
using System.Runtime.CompilerServices;

namespace CthExtract.Oodle.Internal;

internal sealed unsafe partial class OozDecoder
{
    struct HuffRange
    {
        public ushort Symbol;
        public ushort Num;
    }

    struct TansLutEnt
    {
        public uint X;
        public byte BitsX;
        public byte Symbol;
        public ushort W;
    }

    static readonly uint[] CodePrefixOrg = { 0x0, 0x0, 0x2, 0x6, 0xE, 0x1E, 0x3E, 0x7E, 0xFE, 0x1FE, 0x2FE, 0x3FE };

    static readonly uint[] BitMasks =
    {
        0x1, 0x3, 0x7, 0xf, 0x1f, 0x3f, 0x7f, 0xff,
        0x1ff, 0x3ff, 0x7ff, 0xfff, 0x1fff, 0x3fff, 0x7fff, 0xffff,
        0x1ffff, 0x3ffff, 0x7ffff, 0xfffff, 0x1fffff, 0x3fffff, 0x7fffff,
        0xffffff, 0x1ffffff, 0x3ffffff, 0x7ffffff, 0xfffffff, 0x1fffffff, 0x3fffffff, 0x7fffffff, 0xffffffff,
    };

    /// <summary>Rev11[k] = k with its 11 bits reversed. ooz's ReverseBitsArray2048 is an SSE2 transpose that computes
    /// output[k] = input[Rev11[k]] (checked exhaustively against the C++ routine).</summary>
    static readonly ushort[] Rev11 = BuildRev11();

    static ushort[] BuildRev11()
    {
        var t = new ushort[2048];
        for (int k = 0; k < 2048; k++)
        {
            int r = 0;
            for (int b = 0; b < 11; b++)
                if ((k & (1 << b)) != 0)
                    r |= 1 << (10 - b);
            t[k] = (ushort)r;
        }
        return t;
    }

    static readonly uint[] RiceCodeBits2Value =
    {
        0x80000000, 0x00000007, 0x10000006, 0x00000006, 0x20000005, 0x00000105, 0x10000005, 0x00000005,
        0x30000004, 0x00000204, 0x10000104, 0x00000104, 0x20000004, 0x00010004, 0x10000004, 0x00000004,
        0x40000003, 0x00000303, 0x10000203, 0x00000203, 0x20000103, 0x00010103, 0x10000103, 0x00000103,
        0x30000003, 0x00020003, 0x10010003, 0x00010003, 0x20000003, 0x01000003, 0x10000003, 0x00000003,
        0x50000002, 0x00000402, 0x10000302, 0x00000302, 0x20000202, 0x00010202, 0x10000202, 0x00000202,
        0x30000102, 0x00020102, 0x10010102, 0x00010102, 0x20000102, 0x01000102, 0x10000102, 0x00000102,
        0x40000002, 0x00030002, 0x10020002, 0x00020002, 0x20010002, 0x01010002, 0x10010002, 0x00010002,
        0x30000002, 0x02000002, 0x11000002, 0x01000002, 0x20000002, 0x00000012, 0x10000002, 0x00000002,
        0x60000001, 0x00000501, 0x10000401, 0x00000401, 0x20000301, 0x00010301, 0x10000301, 0x00000301,
        0x30000201, 0x00020201, 0x10010201, 0x00010201, 0x20000201, 0x01000201, 0x10000201, 0x00000201,
        0x40000101, 0x00030101, 0x10020101, 0x00020101, 0x20010101, 0x01010101, 0x10010101, 0x00010101,
        0x30000101, 0x02000101, 0x11000101, 0x01000101, 0x20000101, 0x00000111, 0x10000101, 0x00000101,
        0x50000001, 0x00040001, 0x10030001, 0x00030001, 0x20020001, 0x01020001, 0x10020001, 0x00020001,
        0x30010001, 0x02010001, 0x11010001, 0x01010001, 0x20010001, 0x00010011, 0x10010001, 0x00010001,
        0x40000001, 0x03000001, 0x12000001, 0x02000001, 0x21000001, 0x01000011, 0x11000001, 0x01000001,
        0x30000001, 0x00000021, 0x10000011, 0x00000011, 0x20000001, 0x00001001, 0x10000001, 0x00000001,
        0x70000000, 0x00000600, 0x10000500, 0x00000500, 0x20000400, 0x00010400, 0x10000400, 0x00000400,
        0x30000300, 0x00020300, 0x10010300, 0x00010300, 0x20000300, 0x01000300, 0x10000300, 0x00000300,
        0x40000200, 0x00030200, 0x10020200, 0x00020200, 0x20010200, 0x01010200, 0x10010200, 0x00010200,
        0x30000200, 0x02000200, 0x11000200, 0x01000200, 0x20000200, 0x00000210, 0x10000200, 0x00000200,
        0x50000100, 0x00040100, 0x10030100, 0x00030100, 0x20020100, 0x01020100, 0x10020100, 0x00020100,
        0x30010100, 0x02010100, 0x11010100, 0x01010100, 0x20010100, 0x00010110, 0x10010100, 0x00010100,
        0x40000100, 0x03000100, 0x12000100, 0x02000100, 0x21000100, 0x01000110, 0x11000100, 0x01000100,
        0x30000100, 0x00000120, 0x10000110, 0x00000110, 0x20000100, 0x00001100, 0x10000100, 0x00000100,
        0x60000000, 0x00050000, 0x10040000, 0x00040000, 0x20030000, 0x01030000, 0x10030000, 0x00030000,
        0x30020000, 0x02020000, 0x11020000, 0x01020000, 0x20020000, 0x00020010, 0x10020000, 0x00020000,
        0x40010000, 0x03010000, 0x12010000, 0x02010000, 0x21010000, 0x01010010, 0x11010000, 0x01010000,
        0x30010000, 0x00010020, 0x10010010, 0x00010010, 0x20010000, 0x00011000, 0x10010000, 0x00010000,
        0x50000000, 0x04000000, 0x13000000, 0x03000000, 0x22000000, 0x02000010, 0x12000000, 0x02000000,
        0x31000000, 0x01000020, 0x11000010, 0x01000010, 0x21000000, 0x01001000, 0x11000000, 0x01000000,
        0x40000000, 0x00000030, 0x10000020, 0x00000020, 0x20000010, 0x00001010, 0x10000010, 0x00000010,
        0x30000000, 0x00002000, 0x10001000, 0x00001000, 0x20000000, 0x00100000, 0x10000000, 0x00000000,
    };

    static readonly byte[] RiceCodeBits2Len =
    {
        0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4, 1, 2, 2, 3, 2, 3, 3, 4, 2, 3, 3, 4, 3, 4, 4, 5,
        1, 2, 2, 3, 2, 3, 3, 4, 2, 3, 3, 4, 3, 4, 4, 5, 2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6,
        1, 2, 2, 3, 2, 3, 3, 4, 2, 3, 3, 4, 3, 4, 4, 5, 2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6,
        2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6, 3, 4, 4, 5, 4, 5, 5, 6, 4, 5, 5, 6, 5, 6, 6, 7,
        1, 2, 2, 3, 2, 3, 3, 4, 2, 3, 3, 4, 3, 4, 4, 5, 2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6,
        2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6, 3, 4, 4, 5, 4, 5, 5, 6, 4, 5, 5, 6, 5, 6, 6, 7,
        2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6, 3, 4, 4, 5, 4, 5, 5, 6, 4, 5, 5, 6, 5, 6, 6, 7,
        3, 4, 4, 5, 4, 5, 5, 6, 4, 5, 5, 6, 5, 6, 6, 7, 4, 5, 5, 6, 5, 6, 6, 7, 5, 6, 6, 7, 6, 7, 7, 8,
    };

    // ------------------------------------------------------------------------------------------------------------
    // Kraken_DecodeBytes

    /// <summary>ooz <c>Kraken_DecodeBytes</c>. Decodes one entropy block at src into *output (or, for a raw block
    /// with forceMemmove == false, points output into src). Returns the number of src bytes consumed or -1.</summary>
    int DecodeBytes(ref byte* output, byte* src, byte* srcEnd, out int decodedSize, long outputSize,
                    bool forceMemmove, byte* scratch, byte* scratchEnd)
    {
        decodedSize = 0;
        // [CTH] bound the nesting depth (recursive / RLE / multi-array call back into DecodeBytes).
        if (_depth >= MaxEntropyDepth)
            return -1;
        _depth++;
        int r = DecodeBytesCore(ref output, src, srcEnd, out decodedSize, outputSize, forceMemmove, scratch, scratchEnd);
        _depth--;
        return r;
    }

    int DecodeBytesCore(ref byte* output, byte* src, byte* srcEnd, out int decodedSize, long outputSize,
                        bool forceMemmove, byte* scratch, byte* scratchEnd)
    {
        decodedSize = 0;
        byte* srcOrg = src;
        int srcSize, dstSize;

        if (srcEnd - src < 2)
            return -1; // too few bytes

        int chunkType = (src[0] >> 4) & 0x7;
        if (chunkType == 0)
        {
            if (src[0] >= 0x80)
            {
                // In this mode, memcopy stores the length in the bottom 12 bits.
                srcSize = ((src[0] << 8) | src[1]) & 0xFFF;
                src += 2;
            }
            else
            {
                if (srcEnd - src < 3)
                    return -1; // too few bytes
                srcSize = (src[0] << 16) | (src[1] << 8) | src[2];
                if ((srcSize & ~0x3ffff) != 0)
                    return -1; // reserved bits must not be set
                src += 3;
            }
            if (srcSize > outputSize || srcEnd - src < srcSize)
                return -1;
            decodedSize = srcSize;
            if (forceMemmove)
                Mem.Move(output, src, srcSize);
            else
                output = src;
            return (int)(src + srcSize - srcOrg);
        }

        // In all the other modes, the initial bytes encode the src_size and the dst_size
        if (src[0] >= 0x80)
        {
            if (srcEnd - src < 3)
                return -1; // too few bytes
            // short mode, 10 bit sizes
            uint bits = (uint)((src[0] << 16) | (src[1] << 8) | src[2]);
            srcSize = (int)(bits & 0x3ff);
            dstSize = srcSize + (int)((bits >> 10) & 0x3ff) + 1;
            src += 3;
        }
        else
        {
            // long mode, 18 bit sizes
            if (srcEnd - src < 5)
                return -1; // too few bytes
            uint bits = ((uint)src[1] << 24) | ((uint)src[2] << 16) | ((uint)src[3] << 8) | src[4];
            srcSize = (int)(bits & 0x3ffff);
            dstSize = (int)((((bits >> 18) | ((uint)src[0] << 14)) & 0x3FFFF) + 1);
            if (srcSize >= dstSize)
                return -1;
            src += 5;
        }
        if (srcEnd - src < srcSize || dstSize > outputSize)
            return -1;

        byte* dst = output;
        if (dst == scratch)
        {
            if (scratchEnd - scratch < dstSize)
                return -1;
            scratch += dstSize;
        }

        int srcUsed = -1;
        switch (chunkType)
        {
            case 2:
            case 4:
                srcUsed = DecodeBytesType12(src, srcSize, dst, dstSize, chunkType >> 1);
                break;
            case 5:
                srcUsed = DecodeRecursive(src, srcSize, dst, dstSize, scratch, scratchEnd);
                break;
            case 3:
                srcUsed = DecodeRle(src, srcSize, dst, dstSize, scratch, scratchEnd);
                break;
            case 1:
                srcUsed = DecodeTans(src, srcSize, dst, dstSize, scratch, scratchEnd);
                break;
        }
        if (srcUsed != srcSize)
            return -1;
        decodedSize = dstSize;
        return (int)(src + srcSize - srcOrg);
    }

    /// <summary>ooz <c>Kraken_GetBlockSize</c>.</summary>
    static int GetBlockSize(byte* src, byte* srcEnd, out int destSize, int destCapacity)
    {
        destSize = 0;
        byte* srcOrg = src;
        int srcSize, dstSize;

        if (srcEnd - src < 2)
            return -1; // too few bytes

        int chunkType = (src[0] >> 4) & 0x7;
        if (chunkType == 0)
        {
            if (src[0] >= 0x80)
            {
                srcSize = ((src[0] << 8) | src[1]) & 0xFFF;
                src += 2;
            }
            else
            {
                if (srcEnd - src < 3)
                    return -1;
                srcSize = (src[0] << 16) | (src[1] << 8) | src[2];
                if ((srcSize & ~0x3ffff) != 0)
                    return -1;
                src += 3;
            }
            if (srcSize > destCapacity || srcEnd - src < srcSize)
                return -1;
            destSize = srcSize;
            return (int)(src + srcSize - srcOrg);
        }

        if (chunkType >= 6)
            return -1;

        if (src[0] >= 0x80)
        {
            if (srcEnd - src < 3)
                return -1;
            uint bits = (uint)((src[0] << 16) | (src[1] << 8) | src[2]);
            srcSize = (int)(bits & 0x3ff);
            dstSize = srcSize + (int)((bits >> 10) & 0x3ff) + 1;
            src += 3;
        }
        else
        {
            if (srcEnd - src < 5)
                return -1;
            uint bits = ((uint)src[1] << 24) | ((uint)src[2] << 16) | ((uint)src[3] << 8) | src[4];
            srcSize = (int)(bits & 0x3ffff);
            dstSize = (int)((((bits >> 18) | ((uint)src[0] << 14)) & 0x3FFFF) + 1);
            if (srcSize >= dstSize)
                return -1;
            src += 5;
        }
        if (srcEnd - src < srcSize || dstSize > destCapacity)
            return -1;
        destSize = dstSize;
        return srcSize;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Huffman

    /// <summary>ooz <c>Kraken_DecodeBytes_Type12</c>: Huffman with 2 (type 1) or 2x3 (type 2) interleaved streams.</summary>
    int DecodeBytesType12(byte* src, nint srcSize, byte* output, int outputSize, int type)
    {
        BitReader bits;
        byte* srcEnd = src + srcSize;

        bits.BitPos = 24;
        bits.Bits = 0;
        bits.P = src;
        bits.PEnd = srcEnd;
        bits.Refill();

        uint* codePrefix = stackalloc uint[12];
        for (int i = 0; i < 12; i++)
            codePrefix[i] = CodePrefixOrg[i];
        byte* syms = stackalloc byte[1280];
        int numSyms;
        if (bits.ReadBitNoRefill() == 0)
            numSyms = HuffReadCodeLengthsOld(ref bits, syms, codePrefix);
        else if (bits.ReadBitNoRefill() == 0)
            numSyms = HuffReadCodeLengthsNew(ref bits, syms, codePrefix);
        else
            return -1;

        if (numSyms < 1)
            return -1;
        src = bits.P - ((24 - bits.BitPos) / 8);

        if (numSyms == 1)
        {
            Mem.Set(output, syms[0], outputSize);
            // ooz returns src - src_end here (never equal to src_size, so the caller rejects the block); kept as is.
            return (int)(src - srcEnd);
        }

        if (!HuffMakeLut(codePrefix, syms))
            return -1;

        // ooz builds HuffRevLut {bits2len[2048], bits2sym[2048]} with ReverseBitsArray2048; this port stores the
        // same two tables interleaved as one ushort per entry (len | sym << 8) so the hot loop does one load.
        byte* lutLen = _huffLen, lutSym = _huffSym;
        ushort* revLut = _revLut;
        fixed (ushort* rev = Rev11)
        {
            for (int k = 0; k < 2048; k++)
            {
                int j = rev[k];
                revLut[k] = (ushort)(lutLen[j] | (lutSym[j] << 8));
            }
        }

        if (type == 1)
        {
            if (src + 3 > srcEnd)
                return -1;
            uint splitMid = Mem.R16(src);
            src += 2;
            if (!HuffDecodeCore(output, output + outputSize, src, src + splitMid, srcEnd))
                return -1;
        }
        else
        {
            if (src + 6 > srcEnd)
                return -1;

            int halfOutputSize = (outputSize + 1) >> 1;
            uint splitMid = Mem.R32(src) & 0xFFFFFF;
            src += 3;
            if (splitMid > (srcEnd - src))
                return -1;
            byte* srcMid = src + splitMid;
            uint splitLeft = Mem.R16(src);
            src += 2;
            if (srcMid - src < (long)splitLeft + 2 || srcEnd - srcMid < 3)
                return -1;
            uint splitRight = Mem.R16(srcMid);
            if (srcEnd - (srcMid + 2) < (long)splitRight + 2)
                return -1;

            if (!HuffDecodeCore(output, output + halfOutputSize, src, src + splitLeft, srcMid))
                return -1;
            if (!HuffDecodeCore(output + halfOutputSize, output + outputSize, srcMid + 2, srcMid + 2 + splitRight, srcEnd))
                return -1;
        }
        return (int)srcSize;
    }

    /// <summary>ooz <c>Kraken_DecodeBytesCore</c>: three interleaved streams (src forwards, srcMid forwards, srcEnd
    /// backwards) sharing one 11-bit lookup table. All reads stay inside [src, srcEnd).</summary>
    bool HuffDecodeCore(byte* dst, byte* dstEnd, byte* src, byte* srcMid, byte* srcEnd)
    {
        byte* srcMidOrg = srcMid;
        byte* srcEndOrg = srcEnd;
        ushort* lut = _revLut;
        uint srcBits = 0, srcMidBits = 0, srcEndBits = 0;
        int srcBitpos = 0, srcMidBitpos = 0, srcEndBitpos = 0;
        uint e;
        int n;

        if (src > srcMid)
            return false;

        if (srcEndOrg - srcMid >= 4 && dstEnd - dst >= 6)
        {
            dstEnd -= 5;
            srcEnd -= 4;

            while (dst < dstEnd && src <= srcMid && srcMid <= srcEnd)
            {
                srcBits |= Mem.R32(src) << srcBitpos;
                src += (31 - srcBitpos) >> 3;

                srcEndBits |= Mem.BSwap32(Mem.R32(srcEnd)) << srcEndBitpos;
                srcEnd -= (31 - srcEndBitpos) >> 3;

                srcMidBits |= Mem.R32(srcMid) << srcMidBitpos;
                srcMid += (31 - srcMidBitpos) >> 3;

                srcBitpos |= 0x18;
                srcEndBitpos |= 0x18;
                srcMidBitpos |= 0x18;

                e = lut[srcBits & 0x7FF];
                n = (int)(e & 0xFF);
                srcBits >>= n;
                srcBitpos -= n;
                dst[0] = (byte)(e >> 8);

                e = lut[srcEndBits & 0x7FF];
                n = (int)(e & 0xFF);
                srcEndBits >>= n;
                srcEndBitpos -= n;
                dst[1] = (byte)(e >> 8);

                e = lut[srcMidBits & 0x7FF];
                n = (int)(e & 0xFF);
                srcMidBits >>= n;
                srcMidBitpos -= n;
                dst[2] = (byte)(e >> 8);

                e = lut[srcBits & 0x7FF];
                n = (int)(e & 0xFF);
                srcBits >>= n;
                srcBitpos -= n;
                dst[3] = (byte)(e >> 8);

                e = lut[srcEndBits & 0x7FF];
                n = (int)(e & 0xFF);
                srcEndBits >>= n;
                srcEndBitpos -= n;
                dst[4] = (byte)(e >> 8);

                e = lut[srcMidBits & 0x7FF];
                n = (int)(e & 0xFF);
                srcMidBits >>= n;
                srcMidBitpos -= n;
                dst[5] = (byte)(e >> 8);
                dst += 6;
            }
            dstEnd += 5;

            src -= srcBitpos >> 3;
            srcBitpos &= 7;

            srcEnd += 4 + (srcEndBitpos >> 3);
            srcEndBitpos &= 7;

            srcMid -= srcMidBitpos >> 3;
            srcMidBitpos &= 7;
        }
        for (;;)
        {
            if (dst >= dstEnd)
                break;

            if (srcMid - src <= 1)
            {
                if (srcMid - src == 1)
                    srcBits |= (uint)*src << srcBitpos;
            }
            else
            {
                srcBits |= Mem.R16(src) << srcBitpos;
            }
            e = lut[srcBits & 0x7FF];
            n = (int)(e & 0xFF);
            srcBitpos -= n;
            srcBits >>= n;
            *dst++ = (byte)(e >> 8);
            src += (7 - srcBitpos) >> 3;
            srcBitpos &= 7;

            if (dst < dstEnd)
            {
                if (srcEnd - srcMid <= 1)
                {
                    if (srcEnd - srcMid == 1)
                    {
                        srcEndBits |= (uint)*srcMid << srcEndBitpos;
                        srcMidBits |= (uint)*srcMid << srcMidBitpos;
                    }
                }
                else
                {
                    uint v = Mem.R16(srcEnd - 2);
                    srcEndBits |= (((v >> 8) | (v << 8)) & 0xffff) << srcEndBitpos;
                    srcMidBits |= Mem.R16(srcMid) << srcMidBitpos;
                }
                e = lut[srcEndBits & 0x7FF];
                n = (int)(e & 0xFF);
                *dst++ = (byte)(e >> 8);
                srcEndBitpos -= n;
                srcEndBits >>= n;
                srcEnd -= (7 - srcEndBitpos) >> 3;
                srcEndBitpos &= 7;
                if (dst < dstEnd)
                {
                    e = lut[srcMidBits & 0x7FF];
                    n = (int)(e & 0xFF);
                    *dst++ = (byte)(e >> 8);
                    srcMidBitpos -= n;
                    srcMidBits >>= n;
                    srcMid += (7 - srcMidBitpos) >> 3;
                    srcMidBitpos &= 7;
                }
            }
            if (src > srcMid || srcMid > srcEnd)
                return false;
        }
        if (src != srcMidOrg || srcEnd != srcMid)
            return false;
        return true;
    }

    /// <summary>ooz <c>Huff_ReadCodeLengthsOld</c>.</summary>
    static int HuffReadCodeLengthsOld(ref BitReader bits, byte* syms, uint* codePrefix)
    {
        if (bits.ReadBitNoRefill() != 0)
        {
            int n, sym = 0, codelen, numSymbols = 0;
            int avgBitsX4 = 32;
            int forcedBits = bits.ReadBitsNoRefill(2);

            uint thresForValidGammaBits = 1u << (31 - (int)(20u >> forcedBits));
            bool skipInitialZeros = bits.ReadBit() != 0;
            do
            {
                if (!skipInitialZeros)
                {
                    // Run of zeros
                    if ((bits.Bits & 0xff000000) == 0)
                        return -1;
                    sym += bits.ReadBitsNoRefill(2 * (BitOperations.LeadingZeroCount(bits.Bits) + 1)) - 2 + 1;
                    if (sym >= 256)
                        break;
                }
                skipInitialZeros = false;
                bits.Refill();
                // Read out the gamma value for the # of symbols
                if ((bits.Bits & 0xff000000) == 0)
                    return -1;
                n = bits.ReadBitsNoRefill(2 * (BitOperations.LeadingZeroCount(bits.Bits) + 1)) - 2 + 1;
                // Overflow?
                if (sym + n > 256)
                    return -1;
                bits.Refill();
                numSymbols += n;
                do
                {
                    if (bits.Bits < thresForValidGammaBits)
                        return -1; // too big gamma value?

                    int lz = BitOperations.LeadingZeroCount(bits.Bits);
                    int v = bits.ReadBitsNoRefill(lz + forcedBits + 1) + ((lz - 1) << forcedBits);
                    codelen = (-(v & 1) ^ (v >> 1)) + ((avgBitsX4 + 2) >> 2);
                    if (codelen < 1 || codelen > 11)
                        return -1;
                    avgBitsX4 = codelen + ((3 * avgBitsX4 + 2) >> 2);
                    bits.Refill();
                    syms[codePrefix[codelen]++] = (byte)sym++;
                } while (--n != 0);
            } while (sym != 256);
            return (sym == 256) && (numSymbols >= 2) ? numSymbols : -1;
        }
        else
        {
            // Sparse symbol encoding
            int numSymbols = bits.ReadBitsNoRefill(8);
            if (numSymbols == 0)
                return -1;
            if (numSymbols == 1)
            {
                syms[0] = (byte)bits.ReadBitsNoRefill(8);
            }
            else
            {
                int codelenBits = bits.ReadBitsNoRefill(3);
                if (codelenBits > 4)
                    return -1;
                for (int i = 0; i < numSymbols; i++)
                {
                    bits.Refill();
                    int sym = bits.ReadBitsNoRefill(8);
                    int codelen = bits.ReadBitsNoRefillZero(codelenBits) + 1;
                    if (codelen > 11)
                        return -1;
                    syms[codePrefix[codelen]++] = (byte)sym;
                }
            }
            return numSymbols;
        }
    }

    /// <summary>ooz <c>DecodeGolombRiceLengths</c>. dst must have 8 bytes of slack after dst + size.</summary>
    static bool DecodeGolombRiceLengths(byte* dst, nint size, ref BitReader2 br)
    {
        byte* p = br.P, pEnd = br.PEnd;
        byte* dstEnd = dst + size;
        if (p >= pEnd)
            return false;

        int count = -(int)br.BitPos;
        uint v = (uint)(*p++ & (255 >> (int)br.BitPos));
        for (;;)
        {
            if (v == 0)
            {
                count += 8;
            }
            else
            {
                uint x = RiceCodeBits2Value[v];
                Mem.W32(dst, (uint)count + (x & 0x0f0f0f0f));
                Mem.W32(dst + 4, (x >> 4) & 0x0f0f0f0f);
                dst += RiceCodeBits2Len[v];
                if (dst >= dstEnd)
                    break;
                count = (int)(x >> 28);
            }
            if (p >= pEnd)
                return false;
            v = *p++;
        }
        // went too far, step back
        if (dst > dstEnd)
        {
            int n = (int)(dst - dstEnd);
            do v &= v - 1; while (--n != 0);
        }
        // step back if byte not finished
        int bitpos = 0;
        if ((v & 1) == 0)
        {
            p--;
            bitpos = 8 - BitOperations.TrailingZeroCount(v);
        }
        br.P = p;
        br.BitPos = (uint)bitpos;
        return true;
    }

    /// <summary>ooz <c>DecodeGolombRiceBits</c>: appends bitcount (1..3) low bits to each of size bytes. Writes 8
    /// bytes at a time and restores the 8 bytes after dst + size, like the C++ code.</summary>
    static bool DecodeGolombRiceBits(byte* dst, uint size, uint bitcount, ref BitReader2 br)
    {
        if (bitcount == 0)
            return true;
        byte* dstEnd = dst + size;
        byte* p = br.P;
        int bitpos = (int)br.BitPos;

        uint bitsRequired = (uint)bitpos + bitcount * size;
        uint bytesRequired = (bitsRequired + 7) >> 3;
        if (bytesRequired > br.PEnd - p)
            return false;

        br.P = p + (bitsRequired >> 3);
        br.BitPos = bitsRequired & 7;

        ulong bak = Mem.R64(dstEnd);

        if (bitcount < 2)
        {
            do
            {
                // Read the next byte
                ulong bits = (byte)(Mem.BSwap32(Mem.R32(p)) >> (24 - bitpos));
                p += 1;
                // Expand each bit into each byte of the uint64.
                bits = (bits | (bits << 28)) & 0xF0000000FUL;
                bits = (bits | (bits << 14)) & 0x3000300030003UL;
                bits = (bits | (bits << 7)) & 0x0101010101010101UL;
                Mem.W64(dst, Mem.R64(dst) * 2 + Mem.BSwap64(bits));
                dst += 8;
            } while (dst < dstEnd);
        }
        else if (bitcount == 2)
        {
            do
            {
                // Read the next 2 bytes
                ulong bits = (ushort)(Mem.BSwap32(Mem.R32(p)) >> (16 - bitpos));
                p += 2;
                bits = (bits | (bits << 24)) & 0xFF000000FFUL;
                bits = (bits | (bits << 12)) & 0xF000F000F000FUL;
                bits = (bits | (bits << 6)) & 0x0303030303030303UL;
                Mem.W64(dst, Mem.R64(dst) * 4 + Mem.BSwap64(bits));
                dst += 8;
            } while (dst < dstEnd);
        }
        else
        {
            do
            {
                // Read the next 3 bytes
                ulong bits = (Mem.BSwap32(Mem.R32(p)) >> (8 - bitpos)) & 0xffffff;
                p += 3;
                bits = (bits | (bits << 20)) & 0xFFF00000FFFUL;
                bits = (bits | (bits << 10)) & 0x3F003F003F003FUL;
                bits = (bits | (bits << 5)) & 0x0707070707070707UL;
                Mem.W64(dst, Mem.R64(dst) * 8 + Mem.BSwap64(bits));
                dst += 8;
            } while (dst < dstEnd);
        }
        Mem.W64(dstEnd, bak);
        return true;
    }

    /// <summary>ooz <c>Huff_ConvertToRanges</c>.</summary>
    static int HuffConvertToRanges(HuffRange* range, int numSymbols, int P, byte* symlen, ref BitReader bits)
    {
        int numRanges = P >> 1, v, symIdx = 0;

        // Start with space?
        if ((P & 1) != 0)
        {
            bits.Refill();
            v = *symlen++;
            if (v >= 8)
                return -1;
            symIdx = bits.ReadBitsNoRefill(v + 1) + (1 << (v + 1)) - 1;
        }
        int symsUsed = 0;

        for (int i = 0; i < numRanges; i++)
        {
            bits.Refill();
            v = symlen[0];
            if (v >= 9)
                return -1;
            int num = bits.ReadBitsNoRefillZero(v) + (1 << v);
            v = symlen[1];
            if (v >= 8)
                return -1;
            int space = bits.ReadBitsNoRefill(v + 1) + (1 << (v + 1)) - 1;
            range[i].Symbol = (ushort)symIdx;
            range[i].Num = (ushort)num;
            symsUsed += num;
            symIdx += num + space;
            symlen += 2;
        }

        if (symIdx >= 256 || symsUsed >= numSymbols || symIdx + numSymbols - symsUsed > 256)
            return -1;

        range[numRanges].Symbol = (ushort)symIdx;
        range[numRanges].Num = (ushort)(numSymbols - symsUsed);

        return numRanges + 1;
    }

    /// <summary>ooz <c>Huff_ReadCodeLengthsNew</c>.</summary>
    static int HuffReadCodeLengthsNew(ref BitReader bits, byte* syms, uint* codePrefix)
    {
        int forcedBits = bits.ReadBitsNoRefill(2);
        int numSymbols = bits.ReadBitsNoRefill(8) + 1;
        int fluff = bits.ReadFluff(numSymbols);

        // ooz: uint8 code_len[512]; numSymbols + fluff <= 385, plus 8 bytes of write slack and 16 zeroed bytes.
        byte* codeLen = stackalloc byte[512 + 32];
        BitReader2 br2;
        br2.BitPos = (uint)((bits.BitPos - 24) & 7);
        br2.PEnd = bits.PEnd;
        br2.P = bits.P - (uint)((24 - bits.BitPos + 7) >> 3);

        if (!DecodeGolombRiceLengths(codeLen, numSymbols + fluff, ref br2))
            return -1;
        new Span<byte>(codeLen + (numSymbols + fluff), 16).Clear();
        if (!DecodeGolombRiceBits(codeLen, (uint)numSymbols, (uint)forcedBits, ref br2))
            return -1;

        // Reset the bits decoder.
        bits.BitPos = 24;
        bits.P = br2.P;
        bits.Bits = 0;
        bits.Refill();
        bits.Bits <<= (int)br2.BitPos;
        bits.BitPos += (int)br2.BitPos;

        uint runningSum = 0x1e;
        for (int i = 0; i < numSymbols; i++)
        {
            int v = codeLen[i];
            v = -(v & 1) ^ (v >> 1);
            byte cl = (byte)((uint)v + (runningSum >> 2) + 1);
            codeLen[i] = cl;
            if (cl < 1 || cl > 11)
                return -1;
            runningSum += (uint)v;
        }

        HuffRange* range = stackalloc HuffRange[133];
        int ranges = HuffConvertToRanges(range, numSymbols, fluff, &codeLen[numSymbols], ref bits);
        if (ranges <= 0)
            return -1;

        byte* cp = codeLen;
        for (int i = 0; i < ranges; i++)
        {
            int sym = range[i].Symbol;
            int n = range[i].Num;
            do
            {
                syms[codePrefix[*cp++]++] = (byte)sym++;
            } while (--n != 0);
        }

        return numSymbols;
    }

    /// <summary>ooz <c>Huff_MakeLut</c> into this decoder's NewHuffLut.</summary>
    bool HuffMakeLut(uint* prefixCur, byte* syms)
    {
        byte* bits2len = _huffLen, bits2sym = _huffSym;
        uint currslot = 0;
        for (int i = 1; i < 11; i++)
        {
            uint start = CodePrefixOrg[i];
            uint count = prefixCur[i] - start;
            if (count != 0)
            {
                uint stepsize = 1u << (11 - i);
                uint numToSet = count << (11 - i);
                if (currslot + numToSet > 2048)
                    return false;
                Mem.Set(&bits2len[currslot], (byte)i, (nint)numToSet);

                byte* p = &bits2sym[currslot];
                for (uint j = 0; j != count; j++, p += stepsize)
                    Mem.Set(p, syms[start + j], (nint)stepsize);
                currslot += numToSet;
            }
        }
        if (prefixCur[11] - CodePrefixOrg[11] != 0)
        {
            uint numToSet = prefixCur[11] - CodePrefixOrg[11];
            if (currslot + numToSet > 2048)
                return false;
            Mem.Set(&bits2len[currslot], 11, (nint)numToSet);
            Mem.Move(&bits2sym[currslot], &syms[CodePrefixOrg[11]], (nint)numToSet);
            currslot += numToSet;
        }
        return currslot == 2048;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Multi-array, recursive, RLE

    /// <summary>ooz <c>Kraken_DecodeMultiArray</c> (with the zao/ooz fixes: 63 entropy arrays, and the interval
    /// indexes are always copied so the input is never modified).</summary>
    int DecodeMultiArray(byte* src, byte* srcEnd, byte* dst, byte* dstEnd,
                         byte** arrayData, int* arrayLens, int arrayCount,
                         out int totalSizeOut, bool forceMemmove, byte* scratch, byte* scratchEnd)
    {
        totalSizeOut = 0;
        byte* srcOrg = src;

        if (srcEnd - src < 4)
            return -1;

        int decodedSize;
        int numArraysInFile = *src++;
        if ((numArraysInFile & 0x80) == 0)
            return -1;
        numArraysInFile &= 0x3f;

        if (dst == scratch)
        {
            // [CTH] ooz assumes at least 0xc000 bytes here; with less, the split below moves backwards.
            if (scratchEnd - scratch < 0xc000)
                return -1;
            scratch += (scratchEnd - scratch - 0xc000) >> 1;
            dstEnd = scratch;
        }

        int totalSize = 0;

        if (numArraysInFile == 0)
        {
            for (int i = 0; i < arrayCount; i++)
            {
                byte* chunkDst = dst;
                int dec = DecodeBytes(ref chunkDst, src, srcEnd, out decodedSize, dstEnd - dst, forceMemmove, scratch, scratchEnd);
                if (dec < 0)
                    return -1;
                dst += decodedSize;
                arrayLens[i] = decodedSize;
                arrayData[i] = chunkDst;
                src += dec;
                totalSize += decodedSize;
            }
            totalSizeOut = totalSize;
            return (int)(src - srcOrg); // not supported yet
        }

        byte** entropyArrayData = stackalloc byte*[64];
        uint* entropyArraySize = stackalloc uint[64];

        // First loop just decodes everything to scratch
        byte* scratchCur = scratch;

        for (int i = 0; i < numArraysInFile; i++)
        {
            byte* chunkDst = scratchCur;
            int dec = DecodeBytes(ref chunkDst, src, srcEnd, out decodedSize, scratchEnd - scratchCur, forceMemmove, scratchCur, scratchEnd);
            if (dec < 0)
                return -1;
            entropyArrayData[i] = chunkDst;
            entropyArraySize[i] = (uint)decodedSize;
            scratchCur += decodedSize;
            totalSize += decodedSize;
            src += dec;
        }
        totalSizeOut = totalSize;

        if (srcEnd - src < 3)
            return -1;

        int Q = (int)Mem.R16(src);
        src += 2;

        if (GetBlockSize(src, srcEnd, out int outSize, totalSize) < 0)
            return -1;
        int numIndexes = outSize;

        int numLens = numIndexes - arrayCount;
        if (numLens < 1)
            return -1;

        if (scratchEnd - scratchCur < numIndexes)
            return -1;
        byte* intervalLenlog2 = scratchCur;
        scratchCur += numIndexes;

        if (scratchEnd - scratchCur < numIndexes)
            return -1;
        byte* intervalIndexes = scratchCur;
        scratchCur += numIndexes;

        if ((Q & 0x8000) != 0)
        {
            // zao/ooz 02bdf0f: force a copy, the loop below writes to interval_indexes.
            int n = DecodeBytes(ref intervalIndexes, src, srcEnd, out int sizeOut, numIndexes, true, scratchCur, scratchEnd);
            if (n < 0 || sizeOut != numIndexes)
                return -1;
            src += n;

            for (int i = 0; i < numIndexes; i++)
            {
                int t = intervalIndexes[i];
                intervalLenlog2[i] = (byte)(t >> 4);
                intervalIndexes[i] = (byte)(t & 0xF);
            }

            numLens = numIndexes;
        }
        else
        {
            int lenlog2Chunksize = numIndexes - arrayCount;

            int n = DecodeBytes(ref intervalIndexes, src, srcEnd, out int sizeOut, numIndexes, false, scratchCur, scratchEnd);
            if (n < 0 || sizeOut != numIndexes)
                return -1;
            src += n;

            n = DecodeBytes(ref intervalLenlog2, src, srcEnd, out sizeOut, lenlog2Chunksize, false, scratchCur, scratchEnd);
            if (n < 0 || sizeOut != lenlog2Chunksize)
                return -1;
            src += n;

            for (int i = 0; i < lenlog2Chunksize; i++)
                if (intervalLenlog2[i] > 16)
                    return -1;
        }

        if (scratchEnd - scratchCur < 4)
            return -1;

        scratchCur = Mem.AlignPointer(scratchCur, 4);
        if (scratchEnd - scratchCur < (long)numLens * 4)
            return -1;
        uint* decodedIntervals = (uint*)scratchCur;

        int varbitsComplen = Q & 0x3FFF;
        if (srcEnd - src < varbitsComplen)
            return -1;

        byte* f = src;
        uint bitsF = 0;
        int bitposF = 24;

        byte* srcEndActual = src + varbitsComplen;

        byte* b = srcEndActual;
        uint bitsB = 0;
        int bitposB = 24;

        fixed (uint* bitmasks = BitMasks)
        {
            int i;
            for (i = 0; i + 2 <= numLens; i += 2)
            {
                // [CTH] the forward and backward readers meet in the middle; once they have crossed by more than
                // their 4-byte lookahead the data is corrupt (and further reads would leave the block).
                if (f - b > 8)
                    return -1;

                bitsF |= Mem.BSwap32(Mem.R32(f)) >> (24 - bitposF);
                f += (bitposF + 7) >> 3;

                bitsB |= Mem.R32(b - 4) >> (24 - bitposB);
                b -= (bitposB + 7) >> 3;

                int numbitsF = intervalLenlog2[i + 0];
                int numbitsB = intervalLenlog2[i + 1];

                bitsF = BitOperations.RotateLeft(bitsF | 1, numbitsF);
                bitposF += numbitsF - 8 * ((bitposF + 7) >> 3);

                bitsB = BitOperations.RotateLeft(bitsB | 1, numbitsB);
                bitposB += numbitsB - 8 * ((bitposB + 7) >> 3);

                uint valueF = bitsF & bitmasks[numbitsF];
                bitsF &= ~bitmasks[numbitsF];

                uint valueB = bitsB & bitmasks[numbitsB];
                bitsB &= ~bitmasks[numbitsB];

                decodedIntervals[i + 0] = valueF;
                decodedIntervals[i + 1] = valueB;
            }

            // read final one since above loop reads 2
            if (i < numLens)
            {
                if (f - b > 8)
                    return -1;
                bitsF |= Mem.BSwap32(Mem.R32(f)) >> (24 - bitposF);
                int numbitsF = intervalLenlog2[i];
                bitsF = BitOperations.RotateLeft(bitsF | 1, numbitsF);
                decodedIntervals[i + 0] = bitsF & bitmasks[numbitsF];
            }
        }

        if (intervalIndexes[numIndexes - 1] != 0)
            return -1;

        int indi = 0, leni = 0, source;
        int incrementLeni = (Q & 0x8000) != 0 ? 1 : 0;

        for (int arri = 0; arri < arrayCount; arri++)
        {
            arrayData[arri] = dst;
            if (indi >= numIndexes)
                return -1;

            while ((source = intervalIndexes[indi++]) != 0)
            {
                if (source > numArraysInFile)
                    return -1;
                if (leni >= numLens)
                    return -1;
                int curLen = (int)decodedIntervals[leni++];
                int bytesLeft = (int)entropyArraySize[source - 1];
                if (curLen > bytesLeft || curLen > dstEnd - dst)
                    return -1;
                byte* blksrc = entropyArrayData[source - 1];
                entropyArraySize[source - 1] -= (uint)curLen;
                entropyArrayData[source - 1] += curLen;
                byte* dstx = dst;
                dst += curLen;
                Mem.Move(dstx, blksrc, curLen);
            }
            leni += incrementLeni;
            arrayLens[arri] = (int)(dst - arrayData[arri]);
        }

        if (indi != numIndexes || leni != numLens)
            return -1;

        for (int i = 0; i < numArraysInFile; i++)
        {
            if (entropyArraySize[i] != 0)
                return -1;
        }
        return (int)(srcEndActual - srcOrg);
    }

    /// <summary>ooz <c>Krak_DecodeRecursive</c>.</summary>
    int DecodeRecursive(byte* src, nint srcSize, byte* output, int outputSize, byte* scratch, byte* scratchEnd)
    {
        byte* srcOrg = src;
        byte* outputEnd = output + outputSize;
        byte* srcEnd = src + srcSize;

        if (srcSize < 6)
            return -1;

        int n = src[0] & 0x7f;
        if (n < 2)
            return -1;

        if ((src[0] & 0x80) == 0)
        {
            src++;
            do
            {
                int dec = DecodeBytes(ref output, src, srcEnd, out int decodedSize, outputEnd - output, true, scratch, scratchEnd);
                if (dec < 0)
                    return -1;
                output += decodedSize;
                src += dec;
            } while (--n != 0);
            if (output != outputEnd)
                return -1;
            return (int)(src - srcOrg);
        }
        else
        {
            byte* arrayData;
            int arrayLen;
            int dec = DecodeMultiArray(src, srcEnd, output, outputEnd, &arrayData, &arrayLen, 1, out int decodedSize, true, scratch, scratchEnd);
            if (dec < 0)
                return -1;
            output += decodedSize;
            if (output != outputEnd)
                return -1;
            return dec;
        }
    }

    /// <summary>ooz <c>Krak_DecodeRLE</c>.</summary>
    int DecodeRle(byte* src, nint srcSize, byte* dst, int dstSize, byte* scratch, byte* scratchEnd)
    {
        if (srcSize <= 1)
        {
            if (srcSize != 1)
                return -1;
            Mem.Set(dst, src[0], dstSize);
            return 1;
        }
        byte* dstEnd = dst + dstSize;
        byte* cmdPtr = src + 1, cmdPtrEnd = src + srcSize;
        // Unpack the first X bytes of the command buffer?
        if (src[0] != 0)
        {
            byte* dstPtr = scratch;
            int n = DecodeBytes(ref dstPtr, src, src + srcSize, out int decSize, scratchEnd - scratch, true, scratch, scratchEnd);
            if (n <= 0)
                return -1;
            nint cmdLen = srcSize - n + decSize;
            if (cmdLen > scratchEnd - scratch)
                return -1;
            Mem.Move(dstPtr + decSize, src + n, srcSize - n);
            cmdPtr = dstPtr;
            cmdPtrEnd = &dstPtr[cmdLen];
        }

        byte rleByte = 0;

        while (cmdPtr < cmdPtrEnd)
        {
            uint cmd = cmdPtrEnd[-1];
            if (cmd - 1 >= 0x2f)
            {
                cmdPtrEnd--;
                uint bytesToCopy = (uint)(-1 - (int)cmd) & 0xF;
                uint bytesToRle = cmd >> 4;
                if (dstEnd - dst < (long)bytesToCopy + bytesToRle || cmdPtrEnd - cmdPtr < bytesToCopy)
                    return -1;
                Mem.Move(dst, cmdPtr, (nint)bytesToCopy);
                cmdPtr += bytesToCopy;
                dst += bytesToCopy;
                Mem.Set(dst, rleByte, (nint)bytesToRle);
                dst += bytesToRle;
            }
            else if (cmd >= 0x10)
            {
                uint data = Mem.R16(cmdPtrEnd - 2) - 4096;
                cmdPtrEnd -= 2;
                uint bytesToCopy = data & 0x3F;
                uint bytesToRle = data >> 6;
                if (dstEnd - dst < (long)bytesToCopy + bytesToRle || cmdPtrEnd - cmdPtr < bytesToCopy)
                    return -1;
                Mem.Move(dst, cmdPtr, (nint)bytesToCopy);
                cmdPtr += bytesToCopy;
                dst += bytesToCopy;
                Mem.Set(dst, rleByte, (nint)bytesToRle);
                dst += bytesToRle;
            }
            else if (cmd == 1)
            {
                rleByte = *cmdPtr++;
                cmdPtrEnd--;
            }
            else if (cmd >= 9)
            {
                uint bytesToRle = (Mem.R16(cmdPtrEnd - 2) - 0x8ff) * 128;
                cmdPtrEnd -= 2;
                if (dstEnd - dst < bytesToRle)
                    return -1;
                Mem.Set(dst, rleByte, (nint)bytesToRle);
                dst += bytesToRle;
            }
            else
            {
                uint bytesToCopy = (Mem.R16(cmdPtrEnd - 2) - 511) * 64;
                cmdPtrEnd -= 2;
                if (cmdPtrEnd - cmdPtr < bytesToCopy || dstEnd - dst < bytesToCopy)
                    return -1;
                Mem.Move(dst, cmdPtr, (nint)bytesToCopy);
                dst += bytesToCopy;
                cmdPtr += bytesToCopy;
            }
        }
        if (cmdPtrEnd != cmdPtr)
            return -1;

        if (dst != dstEnd)
            return -1;

        return (int)srcSize;
    }

    // ------------------------------------------------------------------------------------------------------------
    // tANS

    static void SimpleSort(byte* p, byte* pend)
    {
        if (p != pend)
        {
            for (byte* lp = p + 1; lp != pend; lp++)
            {
                byte t = lp[0];
                byte* rp;
                for (rp = lp; rp > p && t < rp[-1]; rp--)
                    rp[0] = rp[-1];
                rp[0] = t;
            }
        }
    }

    static void SimpleSort(uint* p, uint* pend)
    {
        if (p != pend)
        {
            for (uint* lp = p + 1; lp != pend; lp++)
            {
                uint t = lp[0];
                uint* rp;
                for (rp = lp; rp > p && t < rp[-1]; rp--)
                    rp[0] = rp[-1];
                rp[0] = t;
            }
        }
    }

    /// <summary>ooz <c>Tans_DecodeTable</c>; fills this decoder's TansData (A = weight-1 symbols, B = sym &lt;&lt; 16 |
    /// weight).</summary>
    bool TansDecodeTable(ref BitReader bits, int lBits, out int aUsed, out int bUsed)
    {
        aUsed = bUsed = 0;
        byte* tansA = _tansA;
        uint* tansB = _tansB;
        bits.Refill();
        if (bits.ReadBitNoRefill() != 0)
        {
            int Q = bits.ReadBitsNoRefill(3);
            int numSymbols = bits.ReadBitsNoRefill(8) + 1;
            if (numSymbols < 2)
                return false;
            int fluff = bits.ReadFluff(numSymbols);
            int totalRiceValues = fluff + numSymbols;
            byte* rice = stackalloc byte[512 + 32];
            BitReader2 br2;

            // another bit reader...
            br2.P = bits.P - ((uint)(24 - bits.BitPos + 7) >> 3);
            br2.PEnd = bits.PEnd;
            br2.BitPos = (uint)((bits.BitPos - 24) & 7);

            if (!DecodeGolombRiceLengths(rice, totalRiceValues, ref br2))
                return false;
            new Span<byte>(rice + totalRiceValues, 16).Clear();

            // Switch back to other bitreader impl
            bits.BitPos = 24;
            bits.P = br2.P;
            bits.Bits = 0;
            bits.Refill();
            bits.Bits <<= (int)br2.BitPos;
            bits.BitPos += (int)br2.BitPos;

            HuffRange* range = stackalloc HuffRange[133];
            fluff = HuffConvertToRanges(range, numSymbols, fluff, &rice[numSymbols], ref bits);
            if (fluff < 0)
                return false;

            bits.Refill();

            uint L = 1u << lBits;
            byte* curRicePtr = rice;
            int average = 6;
            int somesum = 0;
            byte* tanstableA = tansA;
            uint* tanstableB = tansB;

            for (int ri = 0; ri < fluff; ri++)
            {
                int symbol = range[ri].Symbol;
                int num = range[ri].Num;
                do
                {
                    bits.Refill();

                    int nextra = Q + *curRicePtr++;
                    if (nextra > 15)
                        return false;
                    int v = bits.ReadBitsNoRefillZero(nextra) + (1 << nextra) - (1 << Q);

                    int averageDiv4 = average >> 2;
                    int limit = 2 * averageDiv4;
                    if (v <= limit)
                        v = averageDiv4 + (-(v & 1) ^ (int)((uint)v >> 1));
                    if (limit > v)
                        limit = v;
                    v += 1;
                    average += limit - averageDiv4;
                    *tanstableA = (byte)symbol;
                    *tanstableB = (uint)((symbol << 16) + v);
                    if (v == 1) tanstableA++;
                    if (v >= 2) tanstableB++;
                    somesum += v;
                    symbol += 1;
                } while (--num != 0);
            }
            aUsed = (int)(tanstableA - tansA);
            bUsed = (int)(tanstableB - tansB);
            if (somesum != L)
                return false;

            return true;
        }
        else
        {
            bool* seen = stackalloc bool[256];
            new Span<bool>(seen, 256).Clear();
            uint L = 1u << lBits;

            int count = bits.ReadBitsNoRefill(3) + 1;

            int bitsPerSym = BitOperations.Log2((uint)lBits) + 1;
            int maxDeltaBits = bits.ReadBitsNoRefill(bitsPerSym);

            if (maxDeltaBits == 0 || maxDeltaBits > lBits)
                return false;

            byte* tanstableA = tansA;
            uint* tanstableB = tansB;

            int weight = 0;
            int totalWeights = 0;

            do
            {
                bits.Refill();

                int sym = bits.ReadBitsNoRefill(8);
                if (seen[sym])
                    return false;

                int delta = bits.ReadBitsNoRefill(maxDeltaBits);

                weight += delta;

                if (weight == 0)
                    return false;

                seen[sym] = true;
                if (weight == 1)
                    *tanstableA++ = (byte)sym;
                else
                    *tanstableB++ = (uint)((sym << 16) + weight);

                totalWeights += weight;
            } while (--count != 0);

            bits.Refill();

            int lastSym = bits.ReadBitsNoRefill(8);
            if (seen[lastSym])
                return false;

            // [CTH] ooz does this in unsigned arithmetic, so total weights > L wrap around, pass, and later
            // overflow the LUT. Signed here; identical for every table whose weights fit in L.
            if ((long)L - totalWeights < weight || (long)L - totalWeights <= 1)
                return false;

            *tanstableB++ = (uint)((lastSym << 16) + ((int)L - totalWeights));

            aUsed = (int)(tanstableA - tansA);
            bUsed = (int)(tanstableB - tansB);

            SimpleSort(tansA, tanstableA);
            SimpleSort(tansB, tanstableB);
            return true;
        }
    }

    /// <summary>ooz <c>Tans_InitLut</c>. [CTH] every write is bounds-checked against its quarter of the table, the
    /// table must come out completely filled, and every entry must lead to a state below L (true for every valid
    /// tANS table); otherwise false.</summary>
    bool TansInitLut(int aUsed, int bUsed, int lBits, TansLutEnt* lut)
    {
        byte* tansA = _tansA;
        uint* tansB = _tansB;
        TansLutEnt** pointers = stackalloc TansLutEnt*[4];
        TansLutEnt** ends = stackalloc TansLutEnt*[4];

        int L = 1 << lBits;
        if (aUsed > L)
            return false;

        uint slotsLeftToAlloc = (uint)(L - aUsed);

        uint sa = slotsLeftToAlloc >> 2;
        pointers[0] = lut;
        uint sb = sa + ((slotsLeftToAlloc & 3) > 0 ? 1u : 0u);
        pointers[1] = lut + sb;
        sb += sa + ((slotsLeftToAlloc & 3) > 1 ? 1u : 0u);
        pointers[2] = lut + sb;
        sb += sa + ((slotsLeftToAlloc & 3) > 2 ? 1u : 0u);
        pointers[3] = lut + sb;
        ends[0] = pointers[1];
        ends[1] = pointers[2];
        ends[2] = pointers[3];
        ends[3] = lut + slotsLeftToAlloc;

        // Setup the single entrys with weight=1
        {
            TansLutEnt* lutSingles = lut + slotsLeftToAlloc;
            TansLutEnt le;
            le.W = 0;
            le.BitsX = (byte)lBits;
            le.X = (1u << lBits) - 1;
            le.Symbol = 0;
            for (int i = 0; i < aUsed; i++)
            {
                lutSingles[i] = le;
                lutSingles[i].Symbol = tansA[i];
            }
        }

        // Setup the entrys with weight >= 2
        int weightsSum = 0;
        for (int i = 0; i < bUsed; i++)
        {
            int weight = (int)(tansB[i] & 0xffff);
            int symbol = (int)(tansB[i] >> 16);
            if (weight > 4)
            {
                int symBits = BitOperations.Log2((uint)weight);
                int Z = lBits - symBits;
                if (Z < 0)
                    return false;
                TansLutEnt le;
                le.Symbol = (byte)symbol;
                le.BitsX = (byte)Z;
                le.X = (1u << Z) - 1;
                le.W = (ushort)((L - 1) & (weight << Z));
                int whatToAdd = 1 << Z;
                int X = (1 << (symBits + 1)) - weight;

                for (int j = 0; j < 4; j++)
                {
                    TansLutEnt* dst = pointers[j];

                    int Y = (weight + ((weightsSum - j - 1) & 3)) >> 2;
                    if (ends[j] - dst < Y)
                        return false;
                    if (X >= Y)
                    {
                        for (int n = Y; n != 0; n--)
                        {
                            *dst++ = le;
                            le.W += (ushort)whatToAdd;
                        }
                        X -= Y;
                    }
                    else
                    {
                        for (int n = X; n != 0; n--)
                        {
                            *dst++ = le;
                            le.W += (ushort)whatToAdd;
                        }
                        Z--;

                        whatToAdd >>= 1;
                        le.BitsX = (byte)Z;
                        le.W = 0;
                        le.X >>= 1;
                        for (int n = Y - X; n != 0; n--)
                        {
                            *dst++ = le;
                            le.W += (ushort)whatToAdd;
                        }
                        X = weight;
                    }
                    pointers[j] = dst;
                }
            }
            else
            {
                if (weight <= 0)
                    return false;
                uint bits = ((1u << weight) - 1) << (weightsSum & 3);
                bits |= bits >> 4;
                int n = weight, ww = weight;
                do
                {
                    int idx = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    if (pointers[idx] >= ends[idx])
                        return false;
                    TansLutEnt* dst = pointers[idx]++;
                    dst->Symbol = (byte)symbol;
                    int weightBits = BitOperations.Log2((uint)ww);
                    dst->BitsX = (byte)(lBits - weightBits);
                    dst->X = (1u << (lBits - weightBits)) - 1;
                    dst->W = (ushort)((L - 1) & (ww++ << (lBits - weightBits)));
                } while (--n != 0);
            }
            weightsSum += weight;
        }

        for (int j = 0; j < 4; j++)
            if (pointers[j] != ends[j])
                return false;
        for (int i = 0; i < L; i++)
        {
            if (lut[i].BitsX > lBits || (long)lut[i].W + lut[i].X >= L)
                return false;
        }
        return true;
    }

    /// <summary>ooz <c>Krak_DecodeTans</c> + <c>Tans_Decode</c>: 5 interleaved states, 2 bit streams.</summary>
    int DecodeTans(byte* src, nint srcSize, byte* dst, int dstSize, byte* scratch, byte* scratchEnd)
    {
        if (srcSize < 8 || dstSize < 5)
            return -1;

        byte* srcEnd = src + srcSize;

        BitReader br;
        br.BitPos = 24;
        br.Bits = 0;
        br.P = src;
        br.PEnd = srcEnd;
        br.Refill();

        // reserved bit
        if (br.ReadBitNoRefill() != 0)
            return -1;

        int lBits = br.ReadBitsNoRefill(2) + 8;

        if (!TansDecodeTable(ref br, lBits, out int aUsed, out int bUsed))
            return -1;

        src = br.P - (24 - br.BitPos) / 8;

        if (src >= srcEnd)
            return -1;

        uint lutSpaceRequired = (uint)((sizeof(TansLutEnt) << lBits) + 15) & ~15u;
        if (lutSpaceRequired > (scratchEnd - scratch))
            return -1;

        byte* dstEnd = dst + dstSize - 5;

        TansLutEnt* lut = (TansLutEnt*)Mem.AlignPointer(scratch, 16);
        if (!TansInitLut(aUsed, bUsed, lBits, lut))
            return -1;

        // Read out the initial state
        uint lMask = (1u << lBits) - 1;
        uint bitsF = Mem.R32(src);
        src += 4;
        uint bitsB = Mem.BSwap32(Mem.R32(srcEnd - 4));
        srcEnd -= 4;
        int bitposF = 32, bitposB = 32;

        // Read first two.
        uint state0 = bitsF & lMask;
        uint state1 = bitsB & lMask;
        bitsF >>= lBits; bitposF -= lBits;
        bitsB >>= lBits; bitposB -= lBits;

        // Read next two.
        uint state2 = bitsF & lMask;
        uint state3 = bitsB & lMask;
        bitsF >>= lBits; bitposF -= lBits;
        bitsB >>= lBits; bitposB -= lBits;

        // Refill more bits
        bitsF |= Mem.R32(src) << bitposF;
        src += (31 - bitposF) >> 3;
        bitposF |= 24;

        // Read final state variable
        uint state4 = bitsF & lMask;
        bitsF >>= lBits; bitposF -= lBits;

        byte* ptrF = src - (bitposF >> 3);
        bitposF &= 7;
        byte* ptrB = srcEnd + (bitposB >> 3);
        bitposB &= 7;

        // ---- Tans_Decode
        if (ptrF > ptrB)
            return -1;

        TansLutEnt* e;
        if (dst < dstEnd)
        {
            for (;;)
            {
                // [CTH] the two streams meet in the middle; past their 3-byte lookahead the data is corrupt.
                if (ptrF - ptrB > 8)
                    return -1;

                bitsF |= Mem.R32(ptrF) << bitposF;
                ptrF += (31 - bitposF) >> 3;
                bitposF |= 24;

                e = &lut[state0];
                *dst++ = e->Symbol;
                bitposF -= e->BitsX;
                state0 = (bitsF & e->X) + e->W;
                bitsF >>= e->BitsX;
                if (dst >= dstEnd) break;

                e = &lut[state1];
                *dst++ = e->Symbol;
                bitposF -= e->BitsX;
                state1 = (bitsF & e->X) + e->W;
                bitsF >>= e->BitsX;
                if (dst >= dstEnd) break;

                bitsF |= Mem.R32(ptrF) << bitposF;
                ptrF += (31 - bitposF) >> 3;
                bitposF |= 24;

                e = &lut[state2];
                *dst++ = e->Symbol;
                bitposF -= e->BitsX;
                state2 = (bitsF & e->X) + e->W;
                bitsF >>= e->BitsX;
                if (dst >= dstEnd) break;

                e = &lut[state3];
                *dst++ = e->Symbol;
                bitposF -= e->BitsX;
                state3 = (bitsF & e->X) + e->W;
                bitsF >>= e->BitsX;
                if (dst >= dstEnd) break;

                bitsF |= Mem.R32(ptrF) << bitposF;
                ptrF += (31 - bitposF) >> 3;
                bitposF |= 24;

                e = &lut[state4];
                *dst++ = e->Symbol;
                bitposF -= e->BitsX;
                state4 = (bitsF & e->X) + e->W;
                bitsF >>= e->BitsX;
                if (dst >= dstEnd) break;

                bitsB |= Mem.BSwap32(Mem.R32(ptrB - 4)) << bitposB;
                ptrB -= (31 - bitposB) >> 3;
                bitposB |= 24;

                e = &lut[state0];
                *dst++ = e->Symbol;
                bitposB -= e->BitsX;
                state0 = (bitsB & e->X) + e->W;
                bitsB >>= e->BitsX;
                if (dst >= dstEnd) break;

                e = &lut[state1];
                *dst++ = e->Symbol;
                bitposB -= e->BitsX;
                state1 = (bitsB & e->X) + e->W;
                bitsB >>= e->BitsX;
                if (dst >= dstEnd) break;

                bitsB |= Mem.BSwap32(Mem.R32(ptrB - 4)) << bitposB;
                ptrB -= (31 - bitposB) >> 3;
                bitposB |= 24;

                e = &lut[state2];
                *dst++ = e->Symbol;
                bitposB -= e->BitsX;
                state2 = (bitsB & e->X) + e->W;
                bitsB >>= e->BitsX;
                if (dst >= dstEnd) break;

                e = &lut[state3];
                *dst++ = e->Symbol;
                bitposB -= e->BitsX;
                state3 = (bitsB & e->X) + e->W;
                bitsB >>= e->BitsX;
                if (dst >= dstEnd) break;

                bitsB |= Mem.BSwap32(Mem.R32(ptrB - 4)) << bitposB;
                ptrB -= (31 - bitposB) >> 3;
                bitposB |= 24;

                e = &lut[state4];
                *dst++ = e->Symbol;
                bitposB -= e->BitsX;
                state4 = (bitsB & e->X) + e->W;
                bitsB >>= e->BitsX;
                if (dst >= dstEnd) break;
            }
        }

        if (ptrB - ptrF + (bitposF >> 3) + (bitposB >> 3) != 0)
            return -1;

        uint statesOr = state0 | state1 | state2 | state3 | state4;
        if ((statesOr & ~0xFFu) != 0)
            return -1;

        dstEnd[0] = (byte)state0;
        dstEnd[1] = (byte)state1;
        dstEnd[2] = (byte)state2;
        dstEnd[3] = (byte)state3;
        dstEnd[4] = (byte)state4;
        return (int)srcSize;
    }
}
