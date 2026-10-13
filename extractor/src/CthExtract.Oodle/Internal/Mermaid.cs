// C# port of ooz (https://github.com/powzix/ooz, commit 05038060, GPL-3.0-or-later).
// Copyright (C) 2016, Powzix (original C++); C# port (C) 2026 Craft Theft Halo contributors.
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General
// Public License as published by the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. See extractor/vendor/ooz/LICENSE and VENDORED.md.
//
// Mermaid / Selkie LZ (one bitstream format, decoder type 10). [CTH] marks checks added for fuzz safety; each one
// only rejects states a valid stream never reaches.

namespace CthExtract.Oodle.Internal;

internal sealed unsafe partial class OozDecoder
{
    /// <summary>ooz <c>MermaidLzTable</c>. Flag byte format: see the comment in ooz's kraken.cpp.</summary>
    struct MermaidLzTable
    {
        public byte* CmdStream, CmdStreamEnd;
        public byte* LengthStream;
        public byte* LitStream, LitStreamEnd;
        public ushort* Off16Stream, Off16StreamEnd;
        public uint* Off32Stream, Off32StreamEnd;
        public uint* Off32Stream1, Off32Stream2;
        public uint Off32Size1, Off32Size2;
        public uint CmdStream2Offs, CmdStream2OffsEnd;
    }

    /// <summary>sizeof(MermaidLzTable) in the x64 C++ build (the table sits at the start of the temp area).</summary>
    const int MermaidLzTableNativeSize = 104;

    /// <summary>ooz <c>Mermaid_DecodeQuantum</c>.</summary>
    int MermaidDecodeQuantum(byte* dst, byte* dstEnd, byte* dstStart, byte* src, byte* srcEnd, byte* temp, byte* tempEnd)
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
                // Stored without any match copying.
                byte* outp = dst;
                srcUsed = DecodeBytes(ref outp, src, srcEnd, out int writtenBytes, dstCount, false, temp, tempEnd);
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
                    // zao/ooz dd51c63: + 0x4000 because a tANS lookup table needs up to 16 KiB of temp space; with
                    // the original 2 * dst_count + 32, small chunks using tANS fail to decode.
                    int tempUsage = 2 * dstCount + 32 + 0x4000;
                    if (tempUsage > 0x40000) tempUsage = 0x40000;
                    MermaidLzTable lz;
                    if (!MermaidReadLzTable(mode, src, src + srcUsed, dst, dstCount, dst - dstStart,
                                            temp + MermaidLzTableNativeSize, temp + tempUsage, &lz))
                        return -1;
                    if (!MermaidProcessLzRuns(mode, src, src + srcUsed, dst, dstCount, (ulong)(dst - dstStart), dstEnd, &lz))
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

    /// <summary>ooz <c>Mermaid_DecodeFarOffsets</c>: 3-byte (or 4-byte above 0xC00000) offsets, each &lt;= offset.</summary>
    static int MermaidDecodeFarOffsets(byte* src, byte* srcEnd, uint* output, nint outputSize, long offset)
    {
        byte* srcCur = src;
        nint i;
        uint off;

        if (offset < (0xC00000 - 1))
        {
            for (i = 0; i != outputSize; i++)
            {
                if (srcEnd - srcCur < 3)
                    return -1;
                off = (uint)(srcCur[0] | srcCur[1] << 8 | srcCur[2] << 16);
                srcCur += 3;
                output[i] = off;
                if (off > offset)
                    return -1;
            }
            return (int)(srcCur - src);
        }

        for (i = 0; i != outputSize; i++)
        {
            if (srcEnd - srcCur < 3)
                return -1;
            off = (uint)(srcCur[0] | srcCur[1] << 8 | srcCur[2] << 16);
            srcCur += 3;

            if (off >= 0xc00000)
            {
                if (srcCur == srcEnd)
                    return -1;
                off += (uint)(*srcCur++ << 22);
            }
            output[i] = off;
            if (off > offset)
                return -1;
        }
        return (int)(srcCur - src);
    }

    /// <summary>ooz <c>Mermaid_ReadLzTable</c>.</summary>
    bool MermaidReadLzTable(int mode, byte* src, byte* srcEnd, byte* dst, int dstSize, long offset,
                            byte* scratch, byte* scratchEnd, MermaidLzTable* lz)
    {
        byte* outp;
        int decodeCount, n;
        uint tmp, off32Size2, off32Size1;

        if (mode > 1)
            return false;

        if (srcEnd - src < 10)
            return false;

        if (offset == 0)
        {
            Mem.Copy64(dst, src);
            dst += 8;
            src += 8;
        }

        // Decode lit stream
        outp = scratch;
        n = DecodeBytes(ref outp, src, srcEnd, out decodeCount, Math.Min(scratchEnd - scratch, dstSize), false, scratch, scratchEnd);
        if (n < 0)
            return false;
        src += n;
        lz->LitStream = outp;
        lz->LitStreamEnd = outp + decodeCount;
        scratch += decodeCount;

        // Decode flag stream
        outp = scratch;
        n = DecodeBytes(ref outp, src, srcEnd, out decodeCount, Math.Min(scratchEnd - scratch, dstSize), false, scratch, scratchEnd);
        if (n < 0)
            return false;
        src += n;
        lz->CmdStream = outp;
        lz->CmdStreamEnd = outp + decodeCount;
        scratch += decodeCount;

        lz->CmdStream2OffsEnd = (uint)decodeCount;
        if (dstSize <= 0x10000)
        {
            lz->CmdStream2Offs = (uint)decodeCount;
        }
        else
        {
            if (srcEnd - src < 2)
                return false;
            lz->CmdStream2Offs = Mem.R16(src);
            src += 2;
            if (lz->CmdStream2Offs > lz->CmdStream2OffsEnd)
                return false;
        }

        if (srcEnd - src < 2)
            return false;

        int off16Count = (int)Mem.R16(src);
        if (off16Count == 0xffff)
        {
            // off16 is entropy coded
            byte* off16Lo, off16Hi;
            int off16LoCount, off16HiCount;
            src += 2;
            off16Hi = scratch;
            n = DecodeBytes(ref off16Hi, src, srcEnd, out off16HiCount, Math.Min(scratchEnd - scratch, dstSize >> 1), false, scratch, scratchEnd);
            if (n < 0)
                return false;
            src += n;
            scratch += off16HiCount;

            off16Lo = scratch;
            n = DecodeBytes(ref off16Lo, src, srcEnd, out off16LoCount, Math.Min(scratchEnd - scratch, dstSize >> 1), false, scratch, scratchEnd);
            if (n < 0)
                return false;
            src += n;
            scratch += off16LoCount;

            if (off16LoCount != off16HiCount)
                return false;
            scratch = Mem.AlignPointer(scratch, 2);
            lz->Off16Stream = (ushort*)scratch;
            if (scratch + off16LoCount * 2 > scratchEnd)
                return false;
            scratch += off16LoCount * 2;
            lz->Off16StreamEnd = (ushort*)scratch;
            // Mermaid_CombineOffs16
            ushort* o16 = lz->Off16Stream;
            for (int i = 0; i != off16LoCount; i++)
                o16[i] = (ushort)(off16Lo[i] + off16Hi[i] * 256);
        }
        else
        {
            lz->Off16Stream = (ushort*)(src + 2);
            src += 2 + off16Count * 2;
            lz->Off16StreamEnd = (ushort*)src;
        }

        if (srcEnd - src < 3)
            return false;
        tmp = (uint)(src[0] | src[1] << 8 | src[2] << 16);
        src += 3;

        if (tmp != 0)
        {
            off32Size1 = tmp >> 12;
            off32Size2 = tmp & 0xFFF;
            if (off32Size1 == 4095)
            {
                if (srcEnd - src < 2)
                    return false;
                off32Size1 = Mem.R16(src);
                src += 2;
            }
            if (off32Size2 == 4095)
            {
                if (srcEnd - src < 2)
                    return false;
                off32Size2 = Mem.R16(src);
                src += 2;
            }
            lz->Off32Size1 = off32Size1;
            lz->Off32Size2 = off32Size2;

            if (scratch + 4 * ((long)off32Size2 + off32Size1) + 64 > scratchEnd)
                return false;

            scratch = Mem.AlignPointer(scratch, 4);

            lz->Off32Stream1 = (uint*)scratch;
            scratch += off32Size1 * 4;
            // store dummy bytes after for prefetcher.
            new Span<byte>(scratch, 32).Clear();
            scratch += 32;

            lz->Off32Stream2 = (uint*)scratch;
            scratch += off32Size2 * 4;
            // store dummy bytes after for prefetcher.
            new Span<byte>(scratch, 32).Clear();
            scratch += 32;

            n = MermaidDecodeFarOffsets(src, srcEnd, lz->Off32Stream1, (nint)lz->Off32Size1, offset);
            if (n < 0)
                return false;
            src += n;

            n = MermaidDecodeFarOffsets(src, srcEnd, lz->Off32Stream2, (nint)lz->Off32Size2, offset + 0x10000);
            if (n < 0)
                return false;
            src += n;
        }
        else
        {
            if (scratchEnd - scratch < 32)
                return false;
            lz->Off32Size1 = 0;
            lz->Off32Size2 = 0;
            lz->Off32Stream1 = (uint*)scratch;
            lz->Off32Stream2 = (uint*)scratch;
            // store dummy bytes after for prefetcher.
            new Span<byte>(scratch, 32).Clear();
        }
        lz->LengthStream = src;
        return true;
    }

    /// <summary>ooz <c>Mermaid_Mode0</c> (delta literals) / <c>Mermaid_Mode1</c> (raw literals) for one 64 KiB half.
    /// Returns the new length-stream position or null.</summary>
    static byte* MermaidMode<TDelta>(byte* dst, nint dstSize, byte* windowBase, byte* srcEnd,
                                     MermaidLzTable* lz, int* savedDist, nint startoff)
        where TDelta : struct, IFlag
    {
        bool delta = default(TDelta).Value;
        byte* dstEnd = dst + dstSize;
        byte* cmdStream = lz->CmdStream;
        byte* cmdStreamEnd = lz->CmdStreamEnd;
        byte* lengthStream = lz->LengthStream;
        byte* litStream = lz->LitStream;
        byte* litStreamEnd = lz->LitStreamEnd;
        ushort* off16Stream = lz->Off16Stream;
        ushort* off16StreamEnd = lz->Off16StreamEnd;
        uint* off32Stream = lz->Off32Stream;
        uint* off32StreamEnd = lz->Off32StreamEnd;
        nint recentOffs = *savedDist;
        byte* match;
        nint length;
        byte* dstBegin = dst;

        dst += startoff;

        while (cmdStream < cmdStreamEnd)
        {
            nuint cmd = *cmdStream++;
            if (cmd >= 24)
            {
                nint newDist = *off16Stream;
                nuint useDistance = (nuint)(cmd >> 7) - 1;
                nuint litlen = cmd & 7;
                if (delta)
                    Mem.Copy64Add(dst, litStream, &dst[recentOffs]);
                else
                    Mem.Copy64(dst, litStream);
                dst += litlen;
                litStream += litlen;
                recentOffs ^= (nint)(useDistance & (nuint)(recentOffs ^ -newDist));
                off16Stream = (ushort*)((byte*)off16Stream + (useDistance & 2));
                match = dst + recentOffs;
                // [CTH] near offsets are not range-checked in ooz; near the window start they can point before it
                if (match < windowBase)
                    return null;
                Mem.Copy64(dst, match);
                Mem.Copy64(dst + 8, match + 8);
                dst += (cmd >> 3) & 0xF;
            }
            else if (cmd > 2)
            {
                length = (nint)cmd + 5;

                if (off32Stream == off32StreamEnd)
                    return null;
                match = dstBegin - *off32Stream++;
                recentOffs = (nint)(match - dst);

                if (dstEnd - dst < length)
                    return null;
                Mem.Copy64(dst, match);
                Mem.Copy64(dst + 8, match + 8);
                Mem.Copy64(dst + 16, match + 16);
                Mem.Copy64(dst + 24, match + 24);
                dst += length;
            }
            else if (cmd == 0)
            {
                if (srcEnd - lengthStream == 0)
                    return null;
                length = *lengthStream;
                if (length > 251)
                {
                    if (srcEnd - lengthStream < 3)
                        return null;
                    length += (nint)Mem.R16(lengthStream + 1) * 4;
                    lengthStream += 2;
                }
                lengthStream += 1;

                length += 64;
                if (dstEnd - dst < length || litStreamEnd - litStream < length)
                    return null;

                if (delta)
                {
                    do
                    {
                        Mem.Copy64Add(dst, litStream, &dst[recentOffs]);
                        Mem.Copy64Add(dst + 8, litStream + 8, &dst[recentOffs + 8]);
                        dst += 16;
                        litStream += 16;
                        length -= 16;
                    } while (length > 0);
                }
                else
                {
                    do
                    {
                        Mem.Copy64(dst, litStream);
                        Mem.Copy64(dst + 8, litStream + 8);
                        dst += 16;
                        litStream += 16;
                        length -= 16;
                    } while (length > 0);
                }
                dst += length;
                litStream += length;
            }
            else if (cmd == 1)
            {
                if (srcEnd - lengthStream == 0)
                    return null;
                length = *lengthStream;
                if (length > 251)
                {
                    if (srcEnd - lengthStream < 3)
                        return null;
                    length += (nint)Mem.R16(lengthStream + 1) * 4;
                    lengthStream += 2;
                }
                lengthStream += 1;
                length += 91;

                if (off16Stream == off16StreamEnd)
                    return null;
                match = dst - *off16Stream++;
                recentOffs = (nint)(match - dst);
                // [CTH] ooz checks neither the length nor the near offset of this command
                if (match < windowBase || dstEnd - dst < length)
                    return null;
                do
                {
                    Mem.Copy64(dst, match);
                    Mem.Copy64(dst + 8, match + 8);
                    dst += 16;
                    match += 16;
                    length -= 16;
                } while (length > 0);
                dst += length;
            }
            else /* flag == 2 */
            {
                if (srcEnd - lengthStream == 0)
                    return null;
                length = *lengthStream;
                if (length > 251)
                {
                    if (srcEnd - lengthStream < 3)
                        return null;
                    length += (nint)Mem.R16(lengthStream + 1) * 4;
                    lengthStream += 2;
                }
                lengthStream += 1;
                length += 29;
                if (off32Stream == off32StreamEnd)
                    return null;
                match = dstBegin - *off32Stream++;
                recentOffs = (nint)(match - dst);
                // [CTH] ooz does not check the length of this command
                if (dstEnd - dst < length)
                    return null;
                do
                {
                    Mem.Copy64(dst, match);
                    Mem.Copy64(dst + 8, match + 8);
                    dst += 16;
                    match += 16;
                    length -= 16;
                } while (length > 0);
                dst += length;
            }

            // [CTH] keep the cursors inside their buffers (ooz never checks them)
            if (dst > dstEnd || litStream > litStreamEnd || off16Stream > off16StreamEnd)
                return null;
        }

        length = (nint)(dstEnd - dst);
        // [CTH] the final literals must exist
        if (litStreamEnd - litStream < length)
            return null;
        if (delta)
        {
            if (length >= 8)
            {
                do
                {
                    Mem.Copy64Add(dst, litStream, &dst[recentOffs]);
                    dst += 8;
                    litStream += 8;
                    length -= 8;
                } while (length >= 8);
            }
            if (length > 0)
            {
                do
                {
                    *dst = (byte)(*litStream++ + dst[recentOffs]);
                    dst++;
                } while (--length != 0);
            }
        }
        else
        {
            Mem.Move(dst, litStream, length);
            litStream += length;
        }

        *savedDist = (int)recentOffs;
        lz->LengthStream = lengthStream;
        lz->Off16Stream = off16Stream;
        lz->LitStream = litStream;
        return lengthStream;
    }

    /// <summary>ooz <c>Mermaid_ProcessLzRuns</c>: the 128 KiB chunk is processed as two 64 KiB halves.</summary>
    static bool MermaidProcessLzRuns(int mode, byte* src, byte* srcEnd, byte* dst, nint dstSize, ulong offset,
                                     byte* dstEnd, MermaidLzTable* lz)
    {
        byte* windowBase = dst - (nint)offset;
        int savedDist = -8;
        byte* srcCur = null;

        // [CTH] a first chunk shorter than its 8 raw bytes can never decode (ooz fails later)
        if (offset == 0 && dstSize < 8)
            return false;

        for (int iteration = 0; iteration != 2; iteration++)
        {
            nint dstSizeCur = dstSize;
            if (dstSizeCur > 0x10000) dstSizeCur = 0x10000;

            if (iteration == 0)
            {
                lz->Off32Stream = lz->Off32Stream1;
                // [CTH] ooz adds off32_size_1 * 4 to a uint32*, i.e. 4x too far, which disables its own check
                lz->Off32StreamEnd = lz->Off32Stream1 + lz->Off32Size1;
                lz->CmdStreamEnd = lz->CmdStream + lz->CmdStream2Offs;
            }
            else
            {
                lz->Off32Stream = lz->Off32Stream2;
                lz->Off32StreamEnd = lz->Off32Stream2 + lz->Off32Size2;
                lz->CmdStreamEnd = lz->CmdStream + lz->CmdStream2OffsEnd;
                lz->CmdStream += lz->CmdStream2Offs;
            }

            nint startoff = (offset == 0) && (iteration == 0) ? 8 : 0;
            srcCur = mode == 0
                ? MermaidMode<FlagTrue>(dst, dstSizeCur, windowBase, srcEnd, lz, &savedDist, startoff)
                : MermaidMode<FlagFalse>(dst, dstSizeCur, windowBase, srcEnd, lz, &savedDist, startoff);
            if (srcCur == null)
                return false;

            dst += dstSizeCur;
            dstSize -= dstSizeCur;
            if (dstSize == 0)
                break;
        }

        if (srcCur != srcEnd)
            return false;

        return true;
    }
}
