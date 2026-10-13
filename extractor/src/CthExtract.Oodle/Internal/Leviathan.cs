// C# port of ooz (https://github.com/powzix/ooz, commit 05038060, GPL-3.0-or-later).
// Copyright (C) 2016, Powzix (original C++); C# port (C) 2026 Craft Theft Halo contributors.
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General
// Public License as published by the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. See extractor/vendor/ooz/LICENSE and VENDORED.md.
//
// Leviathan LZ. The C++ template Leviathan_ProcessLz<Mode, MultiCmd> becomes a generic method over struct types,
// which the JIT specializes the same way. Where ooz selects a literal or command stream with (uintptr_t)dst & mask,
// this port uses (dst - chunkStart) & mask, which is what the C++ computes after its pointer re-basing.
// [CTH] marks checks added for fuzz safety; each one only rejects states a valid stream never reaches.

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace CthExtract.Oodle.Internal;

internal sealed unsafe partial class OozDecoder
{
    /// <summary>ooz <c>LeviathanLzTable</c>.</summary>
    struct LeviathanLzTable
    {
        public int* OffsStream;
        public int OffsStreamSize;
        public int* LenStream;
        public int LenStreamSize;
        public fixed long LitStream[16];      // byte*
        public fixed int LitStreamSize[16];
        public int LitStreamTotal;
        public fixed long MultiCmdPtr[8];     // byte*
        public fixed long MultiCmdEnd[8];     // byte*
        public byte* CmdStream;
        public int CmdStreamSize;
    }

    /// <summary>sizeof(LeviathanLzTable) in the x64 C++ build.</summary>
    const int LeviathanLzTableNativeSize = 376;

    /// <summary>ooz <c>Leviathan_DecodeQuantum</c>.</summary>
    int LeviathanDecodeQuantum(byte* dst, byte* dstEnd, byte* dstStart, byte* src, byte* srcEnd, byte* scratch, byte* scratchEnd)
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
                    if (scratchUsage < LeviathanLzTableNativeSize)
                        return -1;
                    LeviathanLzTable lz;
                    if (!LeviathanReadLzTable(mode, src, src + srcUsed, dst, dstCount, (int)(dst - dstStart),
                                              scratch + LeviathanLzTableNativeSize, scratch + scratchUsage, &lz))
                        return -1;
                    if (!LeviathanProcessLzRuns(mode, dst, dstCount, (int)(dst - dstStart), &lz))
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

    /// <summary>ooz <c>Leviathan_ReadLzTable</c>.</summary>
    bool LeviathanReadLzTable(int chunkType, byte* src, byte* srcEnd, byte* dst, int dstSize, int offset,
                              byte* scratch, byte* scratchEnd, LeviathanLzTable* lztable)
    {
        byte* packedOffsStream, packedLenStream, outp;
        int decodeCount, n;

        if (chunkType > 5)
            return false;

        if (srcEnd - src < 13)
            return false;

        if (offset == 0)
        {
            Mem.Copy64(dst, src);
            dst += 8;
            src += 8;
        }

        int offsScaling = 0;
        byte* packedOffsStreamExtra = null;

        int offsStreamLimit = dstSize / 3;

        if ((src[0] & 0x80) == 0)
        {
            // Decode packed offset stream, it's bounded by the command length.
            packedOffsStream = scratch;
            n = DecodeBytes(ref packedOffsStream, src, srcEnd, out lztable->OffsStreamSize,
                            Math.Min(scratchEnd - scratch, offsStreamLimit), false, scratch, scratchEnd);
            if (n < 0)
                return false;
            src += n;
            scratch += lztable->OffsStreamSize;
        }
        else
        {
            // uses the mode where distances are coded with 2 tables
            // and the transformation offs * scaling + low_bits
            offsScaling = src[0] - 127;
            src++;

            packedOffsStream = scratch;
            n = DecodeBytes(ref packedOffsStream, src, srcEnd, out lztable->OffsStreamSize,
                            Math.Min(scratchEnd - scratch, offsStreamLimit), false, scratch, scratchEnd);
            if (n < 0)
                return false;
            src += n;
            scratch += lztable->OffsStreamSize;

            if (offsScaling != 1)
            {
                packedOffsStreamExtra = scratch;
                n = DecodeBytes(ref packedOffsStreamExtra, src, srcEnd, out decodeCount,
                                Math.Min(scratchEnd - scratch, offsStreamLimit), false, scratch, scratchEnd);
                if (n < 0 || decodeCount != lztable->OffsStreamSize)
                    return false;
                src += n;
                scratch += decodeCount;
            }
        }

        // Decode packed litlen stream. It's bounded by 1/5 of dst_size.
        packedLenStream = scratch;
        n = DecodeBytes(ref packedLenStream, src, srcEnd, out lztable->LenStreamSize,
                        Math.Min(scratchEnd - scratch, dstSize / 5), false, scratch, scratchEnd);
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

        if (scratch > scratchEnd)
            return false;

        if (chunkType <= 1)
        {
            // Decode lit stream, bounded by dst_size
            outp = scratch;
            n = DecodeBytes(ref outp, src, srcEnd, out decodeCount, Math.Min(scratchEnd - scratch, dstSize),
                            true, scratch, scratchEnd);
            if (n < 0)
                return false;
            src += n;
            lztable->LitStream[0] = (long)outp;
            lztable->LitStreamSize[0] = decodeCount;
        }
        else
        {
            int arrayCount = (chunkType == 2) ? 2 : (chunkType == 3) ? 4 : 16;
            byte** litPtrs = stackalloc byte*[16];
            int* litSizes = stackalloc int[16];
            n = DecodeMultiArray(src, srcEnd, scratch, scratchEnd, litPtrs, litSizes, arrayCount,
                                 out decodeCount, true, scratch, scratchEnd);
            if (n < 0)
                return false;
            src += n;
            for (int i = 0; i < arrayCount; i++)
            {
                lztable->LitStream[i] = (long)litPtrs[i];
                lztable->LitStreamSize[i] = litSizes[i];
            }
        }
        scratch += decodeCount;
        lztable->LitStreamTotal = decodeCount;

        if (src >= srcEnd)
            return false;

        if ((src[0] & 0x80) == 0)
        {
            // Decode command stream, bounded by dst_size
            outp = scratch;
            n = DecodeBytes(ref outp, src, srcEnd, out decodeCount, Math.Min(scratchEnd - scratch, dstSize),
                            true, scratch, scratchEnd);
            if (n < 0)
                return false;
            src += n;
            lztable->CmdStream = outp;
            lztable->CmdStreamSize = decodeCount;
            scratch += decodeCount;
        }
        else
        {
            if (src[0] != 0x83)
                return false;
            src++;
            byte** cmdPtrs = stackalloc byte*[8];
            int* multiCmdLens = stackalloc int[8];
            n = DecodeMultiArray(src, srcEnd, scratch, scratchEnd, cmdPtrs, multiCmdLens, 8,
                                 out decodeCount, true, scratch, scratchEnd);
            if (n < 0)
                return false;
            src += n;
            for (int i = 0; i < 8; i++)
            {
                lztable->MultiCmdPtr[i] = (long)cmdPtrs[i];
                lztable->MultiCmdEnd[i] = (long)(cmdPtrs[i] + multiCmdLens[i]);
            }

            lztable->CmdStream = null;
            lztable->CmdStreamSize = decodeCount;
            scratch += decodeCount;
        }

        // ooz relies on this: literal streams may be over-read by up to dst_size, which then stays in scratch.
        if (dstSize > scratchEnd - scratch)
            return false;

        return UnpackOffsets(src, srcEnd, packedOffsStream, packedOffsStreamExtra,
                             lztable->OffsStreamSize, offsScaling,
                             packedLenStream, lztable->LenStreamSize,
                             lztable->OffsStream, lztable->LenStream, false);
    }

    // --------------------------------------------------------------------------------------------------------
    // Literal modes (ooz LeviathanModeRaw / Sub / LamSub / SubAnd3 / SubAndF / O1)

    interface ILevMode
    {
        void Init(LeviathanLzTable* lzt, byte* chunkStart, byte** streams, byte* nextLit);
        bool CopyLiterals(uint cmd, ref byte* dst, ref int* lenStream, byte* matchZoneEnd, byte* dstEnd, nint lastOffset);
        void CopyFinalLiterals(uint finalLen, ref byte* dst, nint lastOffset);
    }

    struct LevModeRaw : ILevMode
    {
        byte* _lit;

        public void Init(LeviathanLzTable* lzt, byte* chunkStart, byte** streams, byte* nextLit) => _lit = (byte*)lzt->LitStream[0];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CopyLiterals(uint cmd, ref byte* dst, ref int* lenStream, byte* matchZoneEnd, byte* dstEnd, nint lastOffset)
        {
            uint litlen = (cmd >> 3) & 3;
            // use cmov
            uint lenStreamValue = (uint)*lenStream & 0xffffff;
            int* nextLenStream = lenStream + 1;
            lenStream = (litlen == 3) ? nextLenStream : lenStream;
            litlen = (litlen == 3) ? lenStreamValue : litlen;
            byte* d = dst;
            byte* lit = _lit;
            Mem.Copy64(d, lit);
            if (litlen > 8)
            {
                Mem.Copy64(d + 8, lit + 8);
                if (litlen > 16)
                {
                    Mem.Copy64(d + 16, lit + 16);
                    if (litlen > 24)
                    {
                        if (litlen > matchZoneEnd - d)
                            return false; // out of bounds
                        do
                        {
                            Mem.Copy64(d + 24, lit + 24);
                            litlen -= 8; d += 8; lit += 8;
                        } while (litlen > 24);
                    }
                }
            }
            dst = d + litlen;
            _lit = lit + litlen;
            return true;
        }

        public void CopyFinalLiterals(uint finalLen, ref byte* dst, nint lastOffset)
        {
            Mem.Move(dst, _lit, (nint)finalLen); // separate buffers: same as ooz's 64/8/1-byte copies
            dst += finalLen;
            _lit += finalLen;
        }
    }

    struct LevModeSub : ILevMode
    {
        byte* _lit;

        public void Init(LeviathanLzTable* lzt, byte* chunkStart, byte** streams, byte* nextLit) => _lit = (byte*)lzt->LitStream[0];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CopyLiterals(uint cmd, ref byte* dst, ref int* lenStream, byte* matchZoneEnd, byte* dstEnd, nint lastOffset)
        {
            uint litlen = (cmd >> 3) & 3;
            // use cmov
            uint lenStreamValue = (uint)*lenStream & 0xffffff;
            int* nextLenStream = lenStream + 1;
            lenStream = (litlen == 3) ? nextLenStream : lenStream;
            litlen = (litlen == 3) ? lenStreamValue : litlen;
            byte* d = dst;
            byte* lit = _lit;
            Mem.Copy64Add(d, lit, &d[lastOffset]);
            if (litlen > 8)
            {
                Mem.Copy64Add(d + 8, lit + 8, &d[lastOffset + 8]);
                if (litlen > 16)
                {
                    Mem.Copy64Add(d + 16, lit + 16, &d[lastOffset + 16]);
                    if (litlen > 24)
                    {
                        if (litlen > matchZoneEnd - d)
                            return false; // out of bounds
                        do
                        {
                            Mem.Copy64Add(d + 24, lit + 24, &d[lastOffset + 24]);
                            litlen -= 8; d += 8; lit += 8;
                        } while (litlen > 24);
                    }
                }
            }
            dst = d + litlen;
            _lit = lit + litlen;
            return true;
        }

        public void CopyFinalLiterals(uint finalLen, ref byte* dst, nint lastOffset)
        {
            byte* d = dst;
            if (finalLen >= 8)
            {
                do
                {
                    Mem.Copy64Add(d, _lit, &d[lastOffset]);
                    d += 8; _lit += 8; finalLen -= 8;
                } while (finalLen >= 8);
            }
            if (finalLen > 0)
            {
                do
                {
                    *d = (byte)(*_lit++ + d[lastOffset]);
                    d++;
                } while (--finalLen != 0);
            }
            dst = d;
        }
    }

    struct LevModeLamSub : ILevMode
    {
        byte* _lit, _lamLit;

        public void Init(LeviathanLzTable* lzt, byte* chunkStart, byte** streams, byte* nextLit)
        {
            _lit = (byte*)lzt->LitStream[0];
            _lamLit = (byte*)lzt->LitStream[1];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CopyLiterals(uint cmd, ref byte* dst, ref int* lenStream, byte* matchZoneEnd, byte* dstEnd, nint lastOffset)
        {
            uint litCmd = cmd & 0x18;
            if (litCmd == 0)
                return true;

            uint litlen = litCmd >> 3;
            // use cmov
            uint lenStreamValue = (uint)*lenStream & 0xffffff;
            int* nextLenStream = lenStream + 1;
            lenStream = (litlen == 3) ? nextLenStream : lenStream;
            litlen = (litlen == 3) ? lenStreamValue : litlen;

            if (litlen-- == 0)
                return false; // lamsub mode requires one literal

            byte* d = dst;
            d[0] = (byte)(*_lamLit++ + d[lastOffset]);
            d++;

            byte* lit = _lit;
            Mem.Copy64Add(d, lit, &d[lastOffset]);
            if (litlen > 8)
            {
                Mem.Copy64Add(d + 8, lit + 8, &d[lastOffset + 8]);
                if (litlen > 16)
                {
                    Mem.Copy64Add(d + 16, lit + 16, &d[lastOffset + 16]);
                    if (litlen > 24)
                    {
                        if (litlen > matchZoneEnd - d)
                            return false; // out of bounds
                        do
                        {
                            Mem.Copy64Add(d + 24, lit + 24, &d[lastOffset + 24]);
                            litlen -= 8; d += 8; lit += 8;
                        } while (litlen > 24);
                    }
                }
            }
            dst = d + litlen;
            _lit = lit + litlen;
            return true;
        }

        public void CopyFinalLiterals(uint finalLen, ref byte* dst, nint lastOffset)
        {
            byte* d = dst;
            d[0] = (byte)(*_lamLit++ + d[lastOffset]);
            d++;
            finalLen -= 1;

            if (finalLen >= 8)
            {
                do
                {
                    Mem.Copy64Add(d, _lit, &d[lastOffset]);
                    d += 8; _lit += 8; finalLen -= 8;
                } while (finalLen >= 8);
            }
            if (finalLen > 0)
            {
                do
                {
                    *d = (byte)(*_lit++ + d[lastOffset]);
                    d++;
                } while (--finalLen != 0);
            }
            dst = d;
        }
    }

    /// <summary>SubAnd3 (4 literal streams) and SubAndF (16 literal streams), selected by output position.</summary>
    struct LevModeSubAndN<TNum> : ILevMode where TNum : struct, IConst
    {
        byte** _lit;
        byte* _chunkStart;

        public void Init(LeviathanLzTable* lzt, byte* chunkStart, byte** streams, byte* nextLit)
        {
            int num = default(TNum).Value;
            _lit = streams;
            _chunkStart = chunkStart;
            // ooz: lit_stream[i] = lzt->lit_stream[(i - dst_start) & MASK], indexed by dst & MASK; equivalently
            // stream ((dst - dst_start) & MASK).
            for (int i = 0; i < num; i++)
                _lit[i] = (byte*)lzt->LitStream[i];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CopyLiterals(uint cmd, ref byte* dst, ref int* lenStream, byte* matchZoneEnd, byte* dstEnd, nint lastOffset)
        {
            nint mask = default(TNum).Value - 1;
            uint litCmd = cmd & 0x18;
            byte* d = dst;
            if (litCmd == 0x18)
            {
                uint litlen = (uint)*lenStream++ & 0xffffff;
                if (litlen > matchZoneEnd - d)
                    return false;
                while (litlen != 0)
                {
                    *d = (byte)(*_lit[(d - _chunkStart) & mask]++ + d[lastOffset]);
                    d++; litlen--;
                }
            }
            else if (litCmd != 0)
            {
                *d = (byte)(*_lit[(d - _chunkStart) & mask]++ + d[lastOffset]);
                d++;
                if (litCmd == 0x10)
                {
                    *d = (byte)(*_lit[(d - _chunkStart) & mask]++ + d[lastOffset]);
                    d++;
                }
            }
            dst = d;
            return true;
        }

        public void CopyFinalLiterals(uint finalLen, ref byte* dst, nint lastOffset)
        {
            nint mask = default(TNum).Value - 1;
            byte* d = dst;
            if (finalLen > 0)
            {
                do
                {
                    *d = (byte)(*_lit[(d - _chunkStart) & mask]++ + d[lastOffset]);
                    d++;
                } while (--finalLen != 0);
            }
            dst = d;
        }
    }

    /// <summary>Order-1 literals: 16 streams selected by the high nibble of the previous byte.</summary>
    struct LevModeO1 : ILevMode
    {
        byte** _lit;
        byte* _next;

        public void Init(LeviathanLzTable* lzt, byte* chunkStart, byte** streams, byte* nextLit)
        {
            _lit = streams;
            _next = nextLit;
            for (int i = 0; i != 16; i++)
            {
                byte* p = (byte*)lzt->LitStream[i];
                _next[i] = *p;
                _lit[i] = p + 1;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CopyLiterals(uint cmd, ref byte* dst, ref int* lenStream, byte* matchZoneEnd, byte* dstEnd, nint lastOffset)
        {
            uint litCmd = cmd & 0x18;
            byte* d = dst;
            if (litCmd == 0x18)
            {
                uint litlen = (uint)*lenStream++;
                if ((int)litlen <= 0)
                    return false;
                // [CTH] ooz does not bound this run at all
                if (litlen > dstEnd - d)
                    return false;
                uint context = d[-1];
                do
                {
                    nint slot = (nint)(context >> 4);
                    *d++ = (byte)(context = _next[slot]);
                    _next[slot] = *_lit[slot]++;
                } while (--litlen != 0);
            }
            else if (litCmd != 0)
            {
                // either 1 or 2
                uint context = d[-1];
                nint slot = (nint)(context >> 4);
                *d++ = (byte)(context = _next[slot]);
                _next[slot] = *_lit[slot]++;
                if (litCmd == 0x10)
                {
                    slot = (nint)(context >> 4);
                    *d++ = (byte)(context = _next[slot]);
                    _next[slot] = *_lit[slot]++;
                }
            }
            dst = d;
            return true;
        }

        public void CopyFinalLiterals(uint finalLen, ref byte* dst, nint lastOffset)
        {
            byte* d = dst;
            uint context = d[-1];
            while (finalLen != 0)
            {
                nint slot = (nint)(context >> 4);
                *d++ = (byte)(context = _next[slot]);
                _next[slot] = *_lit[slot]++;
                finalLen--;
            }
            dst = d;
        }
    }

    interface IConst { int Value { get; } }
    struct Const4 : IConst { public int Value => 4; }
    struct Const16 : IConst { public int Value => 16; }
    interface IFlag { bool Value { get; } }
    struct FlagTrue : IFlag { public bool Value => true; }
    struct FlagFalse : IFlag { public bool Value => false; }

    /// <summary>ooz <c>Leviathan_ProcessLzRuns</c>.</summary>
    static bool LeviathanProcessLzRuns(int chunkType, byte* dst, int dstSize, int offset, LeviathanLzTable* lzt)
    {
        byte* dstCur = dst + (offset == 0 ? 8 : 0);
        byte* dstEnd = dst + dstSize;
        byte* windowBase = dst - offset;

        // [CTH] a first chunk shorter than its 8 raw bytes can never decode (ooz fails later)
        if (offset == 0 && dstSize < 8)
            return false;

        if (lzt->CmdStream != null)
        {
            // single cmd mode
            return chunkType switch
            {
                0 => LeviathanProcessLz<LevModeSub, FlagFalse>(lzt, dstCur, dst, dstEnd, windowBase),
                1 => LeviathanProcessLz<LevModeRaw, FlagFalse>(lzt, dstCur, dst, dstEnd, windowBase),
                2 => LeviathanProcessLz<LevModeLamSub, FlagFalse>(lzt, dstCur, dst, dstEnd, windowBase),
                3 => LeviathanProcessLz<LevModeSubAndN<Const4>, FlagFalse>(lzt, dstCur, dst, dstEnd, windowBase),
                4 => LeviathanProcessLz<LevModeO1, FlagFalse>(lzt, dstCur, dst, dstEnd, windowBase),
                5 => LeviathanProcessLz<LevModeSubAndN<Const16>, FlagFalse>(lzt, dstCur, dst, dstEnd, windowBase),
                _ => false,
            };
        }
        // multi cmd mode
        return chunkType switch
        {
            0 => LeviathanProcessLz<LevModeSub, FlagTrue>(lzt, dstCur, dst, dstEnd, windowBase),
            1 => LeviathanProcessLz<LevModeRaw, FlagTrue>(lzt, dstCur, dst, dstEnd, windowBase),
            2 => LeviathanProcessLz<LevModeLamSub, FlagTrue>(lzt, dstCur, dst, dstEnd, windowBase),
            3 => LeviathanProcessLz<LevModeSubAndN<Const4>, FlagTrue>(lzt, dstCur, dst, dstEnd, windowBase),
            4 => LeviathanProcessLz<LevModeO1, FlagTrue>(lzt, dstCur, dst, dstEnd, windowBase),
            5 => LeviathanProcessLz<LevModeSubAndN<Const16>, FlagTrue>(lzt, dstCur, dst, dstEnd, windowBase),
            _ => false,
        };
    }

    /// <summary>ooz <c>Leviathan_ProcessLz&lt;Mode, MultiCmd&gt;</c>.</summary>
    static bool LeviathanProcessLz<TMode, TMulti>(LeviathanLzTable* lzt, byte* dst, byte* dstStart, byte* dstEnd, byte* windowBase)
        where TMode : struct, ILevMode
        where TMulti : struct, IFlag
    {
        bool multiCmd = default(TMulti).Value;
        byte* cmdStream = lzt->CmdStream;
        byte* cmdStreamEnd = cmdStream + lzt->CmdStreamSize;
        int* lenStream = lzt->LenStream;
        int* lenStreamEnd = lenStream + lzt->LenStreamSize;

        int* offsStream = lzt->OffsStream;
        int* offsStreamEnd = offsStream + lzt->OffsStreamSize;
        byte* copyfrom;
        byte* matchZoneEnd = (dstEnd - dstStart >= 16) ? dstEnd - 16 : dstStart;

        int* recentOffs = stackalloc int[16];
        new Span<int>(recentOffs, 16).Clear();
        recentOffs[8] = recentOffs[9] = recentOffs[10] = recentOffs[11] = -8;
        recentOffs[12] = recentOffs[13] = recentOffs[14] = -8;

        nint offset = -8;

        byte** streams = stackalloc byte*[16];
        byte* nextLit = stackalloc byte[16];
        TMode mode = default;
        mode.Init(lzt, dstStart, streams, nextLit);

        uint cmdStreamLeft = 0;
        byte** multiCmdStream = stackalloc byte*[8];
        byte** multiCmdEnd = stackalloc byte*[8];
        int cmdIdx = 0;
        if (multiCmd)
        {
            // ooz: multi_cmd_stream[i] = multi_cmd_ptr[(i - dst_start) & 7], indexed by dst & 7; equivalently
            // stream ((dst - dst_start) & 7).
            for (int i = 0; i != 8; i++)
            {
                multiCmdStream[i] = (byte*)lzt->MultiCmdPtr[i];
                multiCmdEnd[i] = (byte*)lzt->MultiCmdEnd[i];
            }
            cmdStreamLeft = (uint)lzt->CmdStreamSize;
            cmdIdx = (int)((dst - dstStart) & 7);
            cmdStream = multiCmdStream[cmdIdx];
        }

        for (;;)
        {
            uint cmd;

            if (!multiCmd)
            {
                if (cmdStream >= cmdStreamEnd)
                    break;
                cmd = *cmdStream++;
            }
            else
            {
                if (cmdStreamLeft == 0)
                    break;
                // [CTH] each of the 8 command streams must stay within its own array
                if (cmdStream >= multiCmdEnd[cmdIdx])
                    return false;
                cmdStreamLeft--;
                cmd = *cmdStream;
                multiCmdStream[cmdIdx] = cmdStream + 1;
            }

            uint offsIndex = cmd >> 5;
            uint matchlen = (cmd & 7) + 2;

            recentOffs[15] = *offsStream;

            if (!mode.CopyLiterals(cmd, ref dst, ref lenStream, matchZoneEnd, dstEnd, offset))
                return false;

            offset = recentOffs[offsIndex + 8];

            // Permute the recent offsets table: [offs_index+1 .. offs_index+8] <- [offs_index .. offs_index+7]
            // (ooz: two unaligned 128-bit loads, then two stores)
            {
                int* r = recentOffs + offsIndex;
                Vector128<int> hi = Vector128.Load(r + 4);
                Vector128<int> lo = Vector128.Load(r);
                Vector128.Store(lo, r + 1);
                Vector128.Store(hi, r + 5);
            }
            recentOffs[8] = (int)offset;
            offsStream += offsIndex == 7 ? 1 : 0;

            if ((nuint)offset < (nuint)(windowBase - dst))
                return false; // offset out of bounds
            copyfrom = dst + offset;

            if (matchlen == 9)
            {
                if (lenStream >= lenStreamEnd)
                    return false; // len stream empty
                matchlen = (uint)*--lenStreamEnd + 6;
                Mem.Copy64(dst, copyfrom);
                Mem.Copy64(dst + 8, copyfrom + 8);
                byte* nextDst = dst + matchlen;
                if (multiCmd)
                {
                    cmdIdx = (int)((nextDst - dstStart) & 7);
                    cmdStream = multiCmdStream[cmdIdx];
                }
                if (matchlen > 16)
                {
                    // [CTH] signed (ooz's unsigned compare passes when dst is already past dst_end - 8)
                    if (matchlen > dstEnd - 8 - dst)
                        return false; // no space in buf
                    Mem.Copy64(dst + 16, copyfrom + 16);
                    do
                    {
                        Mem.Copy64(dst + 24, copyfrom + 24);
                        matchlen -= 8;
                        dst += 8;
                        copyfrom += 8;
                    } while (matchlen > 24);
                }
                dst = nextDst;
            }
            else
            {
                Mem.Copy64(dst, copyfrom);
                dst += matchlen;
                if (multiCmd)
                {
                    cmdIdx = (int)((dst - dstStart) & 7);
                    cmdStream = multiCmdStream[cmdIdx];
                }
            }

            // [CTH] keep the cursors inside their buffers (ooz only checks them after the loop)
            if (dst > dstEnd || offsStream > offsStreamEnd || lenStream > lenStreamEnd)
                return false;
        }

        // check for incorrect input
        if (offsStream != offsStreamEnd || lenStream != lenStreamEnd)
            return false;

        // copy final literals
        if (dst < dstEnd)
        {
            mode.CopyFinalLiterals((uint)(dstEnd - dst), ref dst, offset);
        }
        else if (dst != dstEnd)
        {
            return false;
        }
        return true;
    }
}
