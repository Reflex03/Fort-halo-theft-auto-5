// C# port of ooz (https://github.com/powzix/ooz, commit 05038060, GPL-3.0-or-later).
// Copyright (C) 2016, Powzix (original C++); C# port (C) 2026 Craft Theft Halo contributors.
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General
// Public License as published by the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. See extractor/vendor/ooz/LICENSE and VENDORED.md.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[module: SkipLocalsInit]

namespace CthExtract.Oodle.Internal;

/// <summary>
/// One decoder instance = ooz's <c>KrakenDecoder</c> (the 0x6C000-byte scratch) plus the Huffman/tANS tables
/// that ooz keeps on the stack. Not thread-safe; <see cref="Ooz"/> keeps one per thread.
///
/// Memory contract of <see cref="Decompress"/> (the public API guarantees it by copying into padded buffers):
/// <list type="bullet">
/// <item>src: <see cref="InputPad"/> readable bytes before src and after src + srcLen (the C++ code reads a few
/// bytes outside a sub-stream; every such over-read in this port is bounded well below the pad).</item>
/// <item>dst: dstLen + <see cref="OutputPad"/> writable bytes (ooz's SAFE_SPACE: match/literal copies are done 8 or
/// 16 bytes at a time and may run up to ~41 bytes past the end of a chunk; the extra checks added in this port
/// bound that).</item>
/// </list>
/// </summary>
internal sealed unsafe partial class OozDecoder
{
    public const int InputPad = 64;
    public const int OutputPad = 64;

    /// <summary>ooz <c>Kraken_Create</c>: scratch_size = 0x6C000.</summary>
    public const int ScratchSize = 0x6C000;

    /// <summary>Slack after the logical scratch end. Nothing is allocated from it; it only absorbs the bounded
    /// over-reads of entropy streams that live at the end of the scratch (8/16-byte copies, Mermaid's 3-byte
    /// alignment overshoot).</summary>
    public const int ScratchPad = 0x1000;

    /// <summary>Guard against stack exhaustion: entropy blocks can nest (recursive / RLE / multi-array). ooz has no
    /// limit; real Oodle data nests 1-2 levels.</summary>
    public const int MaxEntropyDepth = 16;

    const int HuffLutOffset = ScratchSize + ScratchPad;            // NewHuffLut: bits2len[2048+16], bits2sym[2048+16]
    const int HuffLutSize = 2 * (2048 + 16);
    const int RevLutOffset = HuffLutOffset + HuffLutSize;          // HuffRevLut as ushort[2048]: len | sym << 8
    const int RevLutSize = 2 * 2048;
    const int TansAOffset = RevLutOffset + RevLutSize;             // TansData.A[256] (+pad)
    const int TansASize = 512;
    const int TansBOffset = TansAOffset + TansASize;               // TansData.B[256] (+pad)
    const int TansBSize = 4 * 320;

    /// <summary>Bytes of 64-byte-aligned memory one decoder needs (a multiple of 64).</summary>
    public const int MemorySize = (TansBOffset + TansBSize + 63) & ~63;

    readonly byte[]? _backing;
    readonly byte* _mem;
    readonly byte* _scratch;
    readonly byte* _huffLen, _huffSym;
    readonly ushort* _revLut;
    readonly byte* _tansA;
    readonly uint* _tansB;
    int _depth;

    public OozDecoder()
    {
        _backing = GC.AllocateUninitializedArray<byte>(MemorySize + 64, pinned: true);
        byte* p = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_backing));
        _mem = Mem.AlignPointer(p, 64);
        new Span<byte>(_mem, MemorySize).Clear();
        _scratch = _mem;
        _huffLen = _mem + HuffLutOffset;
        _huffSym = _huffLen + 2048 + 16;
        _revLut = (ushort*)(_mem + RevLutOffset);
        _tansA = _mem + TansAOffset;
        _tansB = (uint*)(_mem + TansBOffset);
    }

    /// <summary>Uses caller-provided memory (tests put guard pages around it). Must be at least
    /// <see cref="MemorySize"/> bytes, 64-byte aligned, and outlive the decoder.</summary>
    public OozDecoder(byte* memory)
    {
        if (((nint)memory & 63) != 0) throw new ArgumentException("memory must be 64-byte aligned");
        _mem = memory;
        new Span<byte>(_mem, MemorySize).Clear();
        _scratch = _mem;
        _huffLen = _mem + HuffLutOffset;
        _huffSym = _huffLen + 2048 + 16;
        _revLut = (ushort*)(_mem + RevLutOffset);
        _tansA = _mem + TansAOffset;
        _tansB = (uint*)(_mem + TansBOffset);
    }

    struct KrakenHeader
    {
        public int DecoderType;
        public bool RestartDecoder;
        public bool Uncompressed;
        public bool UseChecksums;
    }

    struct QuantumHeader
    {
        public uint CompressedSize;
        public uint Checksum;
        public byte Flag1, Flag2;
        public uint WholeMatchDistance;
    }

    /// <summary>ooz <c>Kraken_Decompress</c>: decodes exactly dstLen bytes. Returns dstLen, or -1 (error says why).
    /// See the class remarks for the padding contract.</summary>
    public int Decompress(byte* src, int srcLen, byte* dst, int dstLen, out OozError error)
    {
        error = default;
        _depth = 0;
        KrakenHeader hdr = default;
        int offset = 0;
        byte* srcStart = src;
        nint left = dstLen;
        nint srcLeft = srcLen;
        while (left != 0)
        {
            var code = DecodeStep(ref hdr, dst, offset, left, src, srcLeft, out int srcUsed, out int dstUsed);
            if (code != OozErrorCode.None)
            {
                error = new OozError(code, (long)(src - srcStart), offset, CodecOf(hdr.DecoderType));
                return -1;
            }
            if (srcUsed == 0)
            {
                error = new OozError(OozErrorCode.TruncatedInput, (long)(src - srcStart), offset, CodecOf(hdr.DecoderType));
                return -1;
            }
            src += srcUsed;
            srcLeft -= srcUsed;
            left -= dstUsed;
            offset += dstUsed;
        }
        if (srcLeft != 0)
        {
            error = new OozError(OozErrorCode.TrailingInput, (long)(src - srcStart), offset, CodecOf(hdr.DecoderType));
            return -1;
        }
        return offset;
    }

    internal static OozCodec CodecOf(int decoderType) => decoderType switch
    {
        6 => OozCodec.Kraken,
        10 => OozCodec.Mermaid,
        12 => OozCodec.Leviathan,
        5 => OozCodec.Lzna,
        11 => OozCodec.BitKnit,
        _ => OozCodec.Unknown,
    };

    /// <summary>ooz <c>Kraken_ParseHeader</c>.</summary>
    static byte* ParseHeader(ref KrakenHeader hdr, byte* p)
    {
        int b = p[0];
        if ((b & 0xF) == 0xC)
        {
            if (((b >> 4) & 3) != 0) return null;
            hdr.RestartDecoder = ((b >> 7) & 1) != 0;
            hdr.Uncompressed = ((b >> 6) & 1) != 0;
            b = p[1];
            hdr.DecoderType = b & 0x7F;
            hdr.UseChecksums = (b >> 7) != 0;
            if (hdr.DecoderType != 6 && hdr.DecoderType != 10 && hdr.DecoderType != 5 && hdr.DecoderType != 11 && hdr.DecoderType != 12)
                return null;
            return p + 2;
        }
        return null;
    }

    /// <summary>ooz <c>Kraken_ParseQuantumHeader</c> (may read up to 6 bytes; the caller checks the result
    /// against the input end, and the input pad makes the read itself safe).</summary>
    static byte* ParseQuantumHeader(out QuantumHeader hdr, byte* p, bool useChecksum)
    {
        hdr = default;
        uint v = (uint)((p[0] << 16) | (p[1] << 8) | p[2]);
        uint size = v & 0x3FFFF;
        if (size != 0x3ffff)
        {
            hdr.CompressedSize = size + 1;
            hdr.Flag1 = (byte)((v >> 18) & 1);
            hdr.Flag2 = (byte)((v >> 19) & 1);
            if (useChecksum)
            {
                hdr.Checksum = (uint)((p[3] << 16) | (p[4] << 8) | p[5]);
                return p + 6;
            }
            return p + 3;
        }
        v >>= 18;
        if (v == 1)
        {
            // memset
            hdr.Checksum = p[3];
            hdr.CompressedSize = 0;
            hdr.WholeMatchDistance = 0;
            return p + 4;
        }
        return null;
    }

    /// <summary>ooz <c>Kraken_DecodeStep</c> for the Kraken family (decoder types 6, 10, 12). LZNA (5) and BitKnit
    /// (11) are reported as unsupported.</summary>
    OozErrorCode DecodeStep(ref KrakenHeader hdr, byte* dstStart, int offset, nint dstBytesLeftIn,
                            byte* src, nint srcBytesLeft, out int srcUsed, out int dstUsed)
    {
        srcUsed = dstUsed = 0;
        byte* srcIn = src;
        byte* srcEnd = src + srcBytesLeft;

        if ((offset & 0x3FFFF) == 0)
        {
            if (srcEnd - src < 2)
                return OozErrorCode.TruncatedInput;
            src = ParseHeader(ref hdr, src);
            if (src == null)
                return OozErrorCode.BadBlockHeader;
        }

        bool isKrakenDecoder = hdr.DecoderType == 6 || hdr.DecoderType == 10 || hdr.DecoderType == 12;
        if (!isKrakenDecoder)
            return OozErrorCode.UnsupportedCodec; // LZNA / BitKnit: not ported (not used by Unreal Engine)

        int dstBytesLeft = (int)Math.Min(0x40000, dstBytesLeftIn);

        if (hdr.Uncompressed)
        {
            if (srcEnd - src < dstBytesLeft)
                return OozErrorCode.None; // srcUsed == 0 -> truncated
            Mem.Move(dstStart + offset, src, dstBytesLeft);
            srcUsed = (int)(src - srcIn) + dstBytesLeft;
            dstUsed = dstBytesLeft;
            return OozErrorCode.None;
        }

        src = ParseQuantumHeader(out QuantumHeader qhdr, src, hdr.UseChecksums);
        if (src == null)
            return OozErrorCode.BadQuantumHeader;
        if (src > srcEnd)
            return OozErrorCode.TruncatedInput;

        // Too few bytes in buffer to make any progress?
        if ((nuint)(srcEnd - src) < qhdr.CompressedSize)
            return OozErrorCode.None; // srcUsed == 0 -> truncated

        if (qhdr.CompressedSize > (uint)dstBytesLeft)
            return OozErrorCode.BadQuantumHeader;

        if (qhdr.CompressedSize == 0)
        {
            // Kraken-family quantum headers never carry a whole-match distance (LZNA only), so this is memset.
            Mem.Set(dstStart + offset, (byte)qhdr.Checksum, dstBytesLeft);
            srcUsed = (int)(src - srcIn);
            dstUsed = dstBytesLeft;
            return OozErrorCode.None;
        }

        // ooz compares against Kraken_GetCrc(), which is an unimplemented stub returning 0, so it rejects every
        // checksummed quantum whose CRC is non-zero. The CRC algorithm is unknown; this port skips the check.

        if (qhdr.CompressedSize == (uint)dstBytesLeft)
        {
            Mem.Move(dstStart + offset, src, dstBytesLeft);
            srcUsed = (int)(src - srcIn) + dstBytesLeft;
            dstUsed = dstBytesLeft;
            return OozErrorCode.None;
        }

        byte* qdst = dstStart + offset;
        byte* qdstEnd = qdst + dstBytesLeft;
        byte* qsrcEnd = src + qhdr.CompressedSize;
        int n = hdr.DecoderType switch
        {
            6 => KrakenDecodeQuantum(qdst, qdstEnd, dstStart, src, qsrcEnd, _scratch, _scratch + ScratchSize),
            10 => MermaidDecodeQuantum(qdst, qdstEnd, dstStart, src, qsrcEnd, _scratch, _scratch + ScratchSize),
            12 => LeviathanDecodeQuantum(qdst, qdstEnd, dstStart, src, qsrcEnd, _scratch, _scratch + ScratchSize),
            _ => -1,
        };
        if (n != (int)qhdr.CompressedSize)
            return OozErrorCode.CorruptData;

        srcUsed = (int)(src - srcIn) + n;
        dstUsed = dstBytesLeft;
        return OozErrorCode.None;
    }
}
