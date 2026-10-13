// C# port of ooz (https://github.com/powzix/ooz, commit 05038060, GPL-3.0-or-later).
// Copyright (C) 2016, Powzix (original C++); C# port (C) 2026 Craft Theft Halo contributors.
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General
// Public License as published by the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. See extractor/vendor/ooz/LICENSE and VENDORED.md.
//
// Kraken LZ: phase 1 (Kraken_ReadLzTable / Kraken_UnpackOffsets) entropy-decodes the literal, command, offset and
// length streams into scratch; phase 2 (Kraken_ProcessLzRuns) runs the copy loop. [CTH] marks checks added for fuzz
// safety; each one only rejects states a valid stream never reaches (ooz's own end-of-chunk checks would reject them
// later, after reading or writing out of bounds).

namespace CthExtract.Oodle.Internal;

internal sealed unsafe partial class OozDecoder
{
    /// <summary>ooz <c>KrakenLzTable</c>.</summary>
    struct KrakenLzTable
    {
        public byte* CmdStream;
        public int CmdStreamSize;
        public int* OffsStream;
        public int OffsStreamSize;
        public byte* LitStream;
        public int LitStreamSize;
        public int* LenStream;
        public int LenStreamSize;
    }

    /// <summary>sizeof(KrakenLzTable) in the x64 C++ build; ooz places the table at the start of the scratch.</summary>
    const int KrakenLzTableNativeSize = 64;

    /// <summary>ooz <c>Kraken_DecodeQuantum</c>: one 256 KiB quantum = up to two 128 KiB chunks with shared history.</summary>
    int KrakenDecodeQuantum(byte* dst, byte* dstEnd, byte* dstStart, byte* src, byte* srcEnd, byte* scratch, byte* scratchEnd)
    {
        byte* srcIn = src;
        int mode, chunkhdr, dstCount, srcUsed;

        while (dstEnd - dst != 0)
        {
            dstCount = (int)(dstEnd - dst);
            if (dstCount > 0x20000) dstCount = 0x20000;
            if (srcEnd - src < 4)
                return -1;
            chunkhdr = src[2] | src[1] << 8 | src[0] << 16;
            if ((chunkhdr & 0x800000) == 0)
            {
                // Stored as entropy without any match copying.
                byte* outp = dst;
                srcUsed = DecodeBytes(ref outp, src, srcEnd, out int writtenBytes, dstCount, false, scratch, scratchEnd);
                if (srcUsed < 0 || writtenBytes != dstCount)
                    return -1;
                // [CTH] ooz leaves a raw (type 0) block in src and never copies it to dst here.
                if (outp != dst)
                    Mem.Move(dst, outp, dstCount);
            }
            else
            {
                src += 3;
                srcUsed = chunkhdr & 0x7FFFF;
                mode = (chunkhdr >> 19) & 0xF;
                if (srcEnd - src < srcUsed)
                    return -1;
                if (srcUsed < dstCount)
                {
                    long scratchUsage = Math.Min(Math.Min(3 * dstCount + 32 + 0xd000, 0x6C000), scratchEnd - scratch);
                    if (scratchUsage < KrakenLzTableNativeSize)
                        return -1;
                    KrakenLzTable lz;
                    if (!KrakenReadLzTable(mode, src, src + srcUsed, dst, dstCount, (int)(dst - dstStart),
                                           scratch + KrakenLzTableNativeSize, scratch + scratchUsage, &lz))
                        return -1;
                    if (!KrakenProcessLzRuns(mode, dst, dstCount, (int)(dst - dstStart), &lz))
                        return -1;
                }
                else if (srcUsed > dstCount || mode != 0)
                {
                    return -1;
                }
                else
                {
                    Mem.Move(dst, src, dstCount);
                }
            }
            src += srcUsed;
            dst += dstCount;
        }
        return (int)(src - srcIn);
    }

    /// <summary>ooz <c>Kraken_ReadLzTable</c>.</summary>
    bool KrakenReadLzTable(int mode, byte* src, byte* srcEnd, byte* dst, int dstSize, int offset,
                           byte* scratch, byte* scratchEnd, KrakenLzTable* lztable)
    {
        byte* outp;
        int decodeCount, n;
        byte* packedOffsStream, packedLenStream;

        if (mode > 1)
            return false;

        if (srcEnd - src < 13)
            return false;

        if (offset == 0)
        {
            Mem.Copy64(dst, src);
            dst += 8;
            src += 8;
        }

        if ((*src & 0x80) != 0)
        {
            // flag & 0xc0 != 0x80: reserved flag set; otherwise "excess bytes", which ooz does not support either.
            return false;
        }

        // Disable no copy optimization if source and dest overlap
        bool forceCopy = dst <= srcEnd && src <= dst + dstSize;

        // Decode lit stream, bounded by dst_size
        outp = scratch;
        n = DecodeBytes(ref outp, src, srcEnd, out decodeCount, Math.Min(scratchEnd - scratch, dstSize), forceCopy, scratch, scratchEnd);
        if (n < 0)
            return false;
        src += n;
        lztable->LitStream = outp;
        lztable->LitStreamSize = decodeCount;
        scratch += decodeCount;

        // Decode command stream, bounded by dst_size
        outp = scratch;
        n = DecodeBytes(ref outp, src, srcEnd, out decodeCount, Math.Min(scratchEnd - scratch, dstSize), forceCopy, scratch, scratchEnd);
        if (n < 0)
            return false;
        src += n;
        lztable->CmdStream = outp;
        lztable->CmdStreamSize = decodeCount;
        scratch += decodeCount;

        // Check if to decode the multistuff crap
        if (srcEnd - src < 3)
            return false;

        int offsScaling = 0;
        byte* packedOffsStreamExtra = null;

        if ((src[0] & 0x80) != 0)
        {
            // uses the mode where distances are coded with 2 tables
            offsScaling = src[0] - 127;
            src++;

            packedOffsStream = scratch;
            n = DecodeBytes(ref packedOffsStream, src, srcEnd, out lztable->OffsStreamSize,
                            Math.Min(scratchEnd - scratch, lztable->CmdStreamSize), false, scratch, scratchEnd);
            if (n < 0)
                return false;
            src += n;
            scratch += lztable->OffsStreamSize;

            if (offsScaling != 1)
            {
                packedOffsStreamExtra = scratch;
                n = DecodeBytes(ref packedOffsStreamExtra, src, srcEnd, out decodeCount,
                                Math.Min(scratchEnd - scratch, lztable->OffsStreamSize), false, scratch, scratchEnd);
                if (n < 0 || decodeCount != lztable->OffsStreamSize)
                    return false;
                src += n;
                scratch += decodeCount;
            }
        }
        else
        {
            // Decode packed offset stream, it's bounded by the command length.
            packedOffsStream = scratch;
            n = DecodeBytes(ref packedOffsStream, src, srcEnd, out lztable->OffsStreamSize,
                            Math.Min(scratchEnd - scratch, lztable->CmdStreamSize), false, scratch, scratchEnd);
            if (n < 0)
                return false;
            src += n;
            scratch += lztable->OffsStreamSize;
        }

        // Decode packed litlen stream. It's bounded by 1/4 of dst_size.
        packedLenStream = scratch;
        n = DecodeBytes(ref packedLenStream, src, srcEnd, out lztable->LenStreamSize,
                        Math.Min(scratchEnd - scratch, dstSize >> 2), false, scratch, scratchEnd);
        if (n < 0)
            return false;
        src += n;
        scratch += lztable->LenStreamSize;

        // Reserve memory for final dist stream
        scratch = Mem.AlignPointer(scratch, 16);
        lztable->OffsStream = (int*)scratch;
        scratch += lztable->OffsStreamSize * 4;

        // Reserve memory for final len stream
        scratch = Mem.AlignPointer(scratch, 16);
        lztable->LenStream = (int*)scratch;
        scratch += lztable->LenStreamSize * 4;

        if (scratch + 64 > scratchEnd)
            return false;

        return UnpackOffsets(src, srcEnd, packedOffsStream, packedOffsStreamExtra,
                             lztable->OffsStreamSize, offsScaling,
                             packedLenStream, lztable->LenStreamSize,
                             lztable->OffsStream, lztable->LenStream, false);
    }

    /// <summary>ooz <c>Kraken_UnpackOffsets</c>: unpacks the 8-bit packed offsets and lengths into 32 bit using
    /// a forward and a backward bit stream that must meet exactly.</summary>
    static bool UnpackOffsets(byte* src, byte* srcEnd,
                              byte* packedOffsStream, byte* packedOffsStreamExtra, int packedOffsStreamSize,
                              int multiDistScale,
                              byte* packedLitlenStream, int packedLitlenStreamSize,
                              int* offsStream, int* lenStream, bool excessFlag)
    {
        BitReader bitsA, bitsB;
        int n, i;
        int u32LenStreamSize = 0;

        bitsA.BitPos = 24;
        bitsA.Bits = 0;
        bitsA.P = src;
        bitsA.PEnd = srcEnd;
        bitsA.Refill();

        bitsB.BitPos = 24;
        bitsB.Bits = 0;
        bitsB.P = srcEnd;
        bitsB.PEnd = src;
        bitsB.RefillBackwards();

        if (!excessFlag)
        {
            if (bitsB.Bits < 0x2000)
                return false;
            n = System.Numerics.BitOperations.LeadingZeroCount(bitsB.Bits);
            bitsB.BitPos += n;
            bitsB.Bits <<= n;
            bitsB.RefillBackwards();
            n++;
            u32LenStreamSize = (int)(bitsB.Bits >> (32 - n)) - 1;
            bitsB.BitPos += n;
            bitsB.Bits <<= n;
            bitsB.RefillBackwards();
        }

        if (multiDistScale == 0)
        {
            // Traditional way of coding offsets
            byte* packedOffsStreamEnd = packedOffsStream + packedOffsStreamSize;
            while (packedOffsStream != packedOffsStreamEnd)
            {
                *offsStream++ = -(int)bitsA.ReadDistance(*packedOffsStream++);
                if (packedOffsStream == packedOffsStreamEnd)
                    break;
                *offsStream++ = -(int)bitsB.ReadDistanceB(*packedOffsStream++);
            }
        }
        else
        {
            // New way of coding offsets
            int* offsStreamOrg = offsStream;
            byte* packedOffsStreamEnd = packedOffsStream + packedOffsStreamSize;
            uint cmd, offs;
            while (packedOffsStream != packedOffsStreamEnd)
            {
                cmd = *packedOffsStream++;
                if ((cmd >> 3) > 26)
                    return false;
                offs = ((8 + (cmd & 7)) << (int)(cmd >> 3)) | bitsA.ReadMoreThan24Bits((int)(cmd >> 3));
                *offsStream++ = 8 - (int)offs;
                if (packedOffsStream == packedOffsStreamEnd)
                    break;
                cmd = *packedOffsStream++;
                if ((cmd >> 3) > 26)
                    return false;
                offs = ((8 + (cmd & 7)) << (int)(cmd >> 3)) | bitsB.ReadMoreThan24BitsB((int)(cmd >> 3));
                *offsStream++ = 8 - (int)offs;
            }
            if (multiDistScale != 1)
            {
                // CombineScaledOffsetArrays
                nint count = (nint)(offsStream - offsStreamOrg);
                for (nint k = 0; k != count; k++)
                    offsStreamOrg[k] = multiDistScale * offsStreamOrg[k] - packedOffsStreamExtra[k];
            }
        }

        // max count is 128kb / 256 = 512
        if (u32LenStreamSize > 512)
            return false;
        uint* u32LenStream = stackalloc uint[512];
        uint* u32LenStreamEnd = u32LenStream + u32LenStreamSize;
        for (i = 0; i + 1 < u32LenStreamSize; i += 2)
        {
            if (!bitsA.ReadLength(out u32LenStream[i + 0]))
                return false;
            if (!bitsB.ReadLengthB(out u32LenStream[i + 1]))
                return false;
        }
        if (i < u32LenStreamSize)
        {
            if (!bitsA.ReadLength(out u32LenStream[i + 0]))
                return false;
        }

        bitsA.P -= (24 - bitsA.BitPos) >> 3;
        bitsB.P += (24 - bitsB.BitPos) >> 3;

        if (bitsA.P != bitsB.P)
            return false;

        uint* u32Cur = u32LenStream;
        for (i = 0; i < packedLitlenStreamSize; i++)
        {
            uint v = packedLitlenStream[i];
            if (v == 255)
            {
                // [CTH] ooz reads past the 512-entry stack buffer when the packed stream has too many 255s.
                if (u32Cur >= u32LenStreamEnd)
                    return false;
                v = *u32Cur++ + 255;
            }
            lenStream[i] = (int)(v + 3);
        }
        if (u32Cur != u32LenStreamEnd)
            return false;

        return true;
    }

    /// <summary>ooz <c>Kraken_ProcessLzRuns</c>.</summary>
    static bool KrakenProcessLzRuns(int mode, byte* dst, int dstSize, int offset, KrakenLzTable* lztable)
    {
        byte* dstEnd = dst + dstSize;
        // [CTH] a first chunk shorter than its 8 raw bytes can never decode (ooz fails later, after the loop).
        if (offset == 0 && dstSize < 8)
            return false;
        if (mode == 1)
            return KrakenProcessLzRunsType1(lztable, dst + (offset == 0 ? 8 : 0), dstEnd, dst - offset);
        if (mode == 0)
            return KrakenProcessLzRunsType0(lztable, dst + (offset == 0 ? 8 : 0), dstEnd, dst - offset);
        return false;
    }

    /// <summary>ooz <c>Kraken_ProcessLzRuns_Type0</c>: literals are stored as deltas against the byte at the last
    /// match offset.</summary>
    static bool KrakenProcessLzRunsType0(KrakenLzTable* lzt, byte* dst, byte* dstEnd, byte* dstStart)
    {
        byte* cmdStream = lzt->CmdStream;
        byte* cmdStreamEnd = cmdStream + lzt->CmdStreamSize;
        int* lenStream = lzt->LenStream;
        int* lenStreamEnd = lzt->LenStream + lzt->LenStreamSize;
        byte* litStream = lzt->LitStream;
        byte* litStreamEnd = lzt->LitStream + lzt->LitStreamSize;
        int* offsStream = lzt->OffsStream;
        int* offsStreamEnd = lzt->OffsStream + lzt->OffsStreamSize;
        byte* copyfrom;
        uint finalLen;
        int offset;
        int* recentOffs = stackalloc int[7];
        new Span<int>(recentOffs, 7).Clear();
        int lastOffset;

        recentOffs[3] = -8;
        recentOffs[4] = -8;
        recentOffs[5] = -8;
        lastOffset = -8;

        while (cmdStream < cmdStreamEnd)
        {
            uint f = *cmdStream++;
            uint litlen = f & 3;
            uint offsIndex = f >> 6;
            uint matchlen = (f >> 2) & 0xF;

            uint nextLongLength = (uint)*lenStream;
            int* nextLenStream = lenStream + 1;

            lenStream = (litlen == 3) ? nextLenStream : lenStream;
            litlen = (litlen == 3) ? nextLongLength : litlen;
            recentOffs[6] = *offsStream;

            Mem.Copy64Add(dst, litStream, &dst[lastOffset]);
            if (litlen > 8)
            {
                Mem.Copy64Add(dst + 8, litStream + 8, &dst[lastOffset + 8]);
                if (litlen > 16)
                {
                    Mem.Copy64Add(dst + 16, litStream + 16, &dst[lastOffset + 16]);
                    if (litlen > 24)
                    {
                        // [CTH] long literal runs must fit in the chunk and in the literal stream
                        if (litlen > dstEnd - dst || litlen > litStreamEnd - litStream)
                            return false;
                        do
                        {
                            Mem.Copy64Add(dst + 24, litStream + 24, &dst[lastOffset + 24]);
                            litlen -= 8;
                            dst += 8;
                            litStream += 8;
                        } while (litlen > 24);
                    }
                }
            }
            dst += litlen;
            litStream += litlen;

            {
                int* r = recentOffs + offsIndex;
                offset = r[3];
                r[3] = r[2];
                r[2] = r[1];
                r[1] = r[0];
                recentOffs[3] = offset;
            }
            lastOffset = offset;

            offsStream += (offsIndex + 1) >> 2; // advances one entry when offs_index == 3

            if ((nuint)(nint)offset < (nuint)(dstStart - dst))
                return false; // offset out of bounds

            copyfrom = dst + offset;
            if (matchlen != 15)
            {
                Mem.Copy64(dst, copyfrom);
                Mem.Copy64(dst + 8, copyfrom + 8);
                dst += matchlen + 2;
            }
            else
            {
                // [CTH] the length must come from the stream (ooz reads one past its end here on corrupt data);
                // a stale value below 17 would wrap the unsigned "matchlen -= 8" loop below.
                if (lenStream >= lenStreamEnd)
                    return false;
                matchlen = 14 + (uint)*lenStream++;
                // [CTH] signed: dst may already be up to 24 bytes past dstEnd after a short literal run, where
                // ooz's unsigned compare would let an arbitrary length through.
                if (matchlen < 17 || matchlen > dstEnd - dst)
                    return false; // copy length out of bounds
                Mem.Copy64(dst, copyfrom);
                Mem.Copy64(dst + 8, copyfrom + 8);
                Mem.Copy64(dst + 16, copyfrom + 16);
                do
                {
                    Mem.Copy64(dst + 24, copyfrom + 24);
                    matchlen -= 8;
                    dst += 8;
                    copyfrom += 8;
                } while (matchlen > 24);
                dst += matchlen;
            }

            // [CTH] keep every cursor inside its buffer (ooz only checks them after the loop)
            if (dst > dstEnd || litStream > litStreamEnd || offsStream > offsStreamEnd || lenStream > lenStreamEnd)
                return false;
        }

        // check for incorrect input
        if (offsStream != offsStreamEnd || lenStream != lenStreamEnd)
            return false;

        finalLen = (uint)(dstEnd - dst);
        if (finalLen != litStreamEnd - litStream)
            return false;

        if (finalLen >= 8)
        {
            do
            {
                Mem.Copy64Add(dst, litStream, &dst[lastOffset]);
                dst += 8; litStream += 8; finalLen -= 8;
            } while (finalLen >= 8);
        }
        if (finalLen > 0)
        {
            do
            {
                *dst = (byte)(*litStream++ + dst[lastOffset]);
                dst++;
            } while (--finalLen != 0);
        }
        return true;
    }

    /// <summary>ooz <c>Kraken_ProcessLzRuns_Type1</c>: raw literals.</summary>
    static bool KrakenProcessLzRunsType1(KrakenLzTable* lzt, byte* dst, byte* dstEnd, byte* dstStart)
    {
        byte* cmdStream = lzt->CmdStream;
        byte* cmdStreamEnd = cmdStream + lzt->CmdStreamSize;
        int* lenStream = lzt->LenStream;
        int* lenStreamEnd = lzt->LenStream + lzt->LenStreamSize;
        byte* litStream = lzt->LitStream;
        byte* litStreamEnd = lzt->LitStream + lzt->LitStreamSize;
        int* offsStream = lzt->OffsStream;
        int* offsStreamEnd = lzt->OffsStream + lzt->OffsStreamSize;
        byte* copyfrom;
        uint finalLen;
        int offset;
        int* recentOffs = stackalloc int[7];
        new Span<int>(recentOffs, 7).Clear();

        recentOffs[3] = -8;
        recentOffs[4] = -8;
        recentOffs[5] = -8;

        while (cmdStream < cmdStreamEnd)
        {
            uint f = *cmdStream++;
            uint litlen = f & 3;
            uint offsIndex = f >> 6;
            uint matchlen = (f >> 2) & 0xF;

            uint nextLongLength = (uint)*lenStream;
            int* nextLenStream = lenStream + 1;

            lenStream = (litlen == 3) ? nextLenStream : lenStream;
            litlen = (litlen == 3) ? nextLongLength : litlen;
            recentOffs[6] = *offsStream;

            Mem.Copy64(dst, litStream);
            if (litlen > 8)
            {
                Mem.Copy64(dst + 8, litStream + 8);
                if (litlen > 16)
                {
                    Mem.Copy64(dst + 16, litStream + 16);
                    if (litlen > 24)
                    {
                        // [CTH] long literal runs must fit in the chunk and in the literal stream
                        if (litlen > dstEnd - dst || litlen > litStreamEnd - litStream)
                            return false;
                        do
                        {
                            Mem.Copy64(dst + 24, litStream + 24);
                            litlen -= 8;
                            dst += 8;
                            litStream += 8;
                        } while (litlen > 24);
                    }
                }
            }
            dst += litlen;
            litStream += litlen;

            {
                int* r = recentOffs + offsIndex;
                offset = r[3];
                r[3] = r[2];
                r[2] = r[1];
                r[1] = r[0];
                recentOffs[3] = offset;
            }

            offsStream += (offsIndex + 1) >> 2; // advances one entry when offs_index == 3

            if ((nuint)(nint)offset < (nuint)(dstStart - dst))
                return false; // offset out of bounds

            copyfrom = dst + offset;
            if (matchlen != 15)
            {
                Mem.Copy64(dst, copyfrom);
                Mem.Copy64(dst + 8, copyfrom + 8);
                dst += matchlen + 2;
            }
            else
            {
                // [CTH] the length must come from the stream (ooz reads one past its end here on corrupt data);
                // a stale value below 17 would wrap the unsigned "matchlen -= 8" loop below.
                if (lenStream >= lenStreamEnd)
                    return false;
                matchlen = 14 + (uint)*lenStream++;
                // [CTH] signed: dst may already be up to 24 bytes past dstEnd after a short literal run, where
                // ooz's unsigned compare would let an arbitrary length through.
                if (matchlen < 17 || matchlen > dstEnd - dst)
                    return false; // copy length out of bounds
                Mem.Copy64(dst, copyfrom);
                Mem.Copy64(dst + 8, copyfrom + 8);
                Mem.Copy64(dst + 16, copyfrom + 16);
                do
                {
                    Mem.Copy64(dst + 24, copyfrom + 24);
                    matchlen -= 8;
                    dst += 8;
                    copyfrom += 8;
                } while (matchlen > 24);
                dst += matchlen;
            }

            // [CTH] keep every cursor inside its buffer (ooz only checks them after the loop)
            if (dst > dstEnd || litStream > litStreamEnd || offsStream > offsStreamEnd || lenStream > lenStreamEnd)
                return false;
        }

        // check for incorrect input
        if (offsStream != offsStreamEnd || lenStream != lenStreamEnd)
            return false;

        finalLen = (uint)(dstEnd - dst);
        if (finalLen != litStreamEnd - litStream)
            return false;

        // literal stream and output never overlap (separate buffers), so this is the 64/8/1-byte copy of ooz
        Mem.Move(dst, litStream, (nint)finalLen);
        return true;
    }
}
