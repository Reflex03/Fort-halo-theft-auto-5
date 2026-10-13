// C# port of ooz (https://github.com/powzix/ooz, commit 05038060, GPL-3.0-or-later).
// Copyright (C) 2016, Powzix (original C++); C# port (C) 2026 Craft Theft Halo contributors.
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General
// Public License as published by the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. See extractor/vendor/ooz/LICENSE and VENDORED.md.

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CthExtract.Oodle.Internal;

namespace CthExtract.Oodle;

/// <summary>Oodle bitstream families. Mermaid and Selkie share one bitstream (decoder type 10).</summary>
public enum OozCodec
{
    Unknown = 0,
    Kraken,
    Mermaid,
    Leviathan,
    /// <summary>Recognised but not supported by this port.</summary>
    Lzna,
    /// <summary>Recognised but not supported by this port.</summary>
    BitKnit,
}

public enum OozErrorCode
{
    None = 0,
    /// <summary>The stream does not start with a valid Oodle block header (low nibble 0xC, known decoder type).</summary>
    BadBlockHeader,
    /// <summary>LZNA or BitKnit (not used by Unreal Engine; not ported).</summary>
    UnsupportedCodec,
    BadQuantumHeader,
    /// <summary>The input ended before the requested number of bytes was decoded.</summary>
    TruncatedInput,
    /// <summary>All requested bytes were decoded but compressed bytes are left over (wrong sizes from the caller).</summary>
    TrailingInput,
    /// <summary>A quantum failed to decode (corrupt data, wrong decompressed size, or an encoder feature ooz lacks).</summary>
    CorruptData,
    /// <summary>The buffers for the requested size could not be allocated.</summary>
    OutOfMemory,
    /// <summary>An unexpected managed exception (a bug in this port); the input is treated as corrupt.</summary>
    InternalError,
}

/// <summary>Why a decode failed: what, at which compressed/decompressed offset, in which codec.</summary>
public readonly record struct OozError(OozErrorCode Code, long CompressedOffset, long DecompressedOffset, OozCodec Codec)
{
    public bool IsError => Code != OozErrorCode.None;

    public override string ToString() => Code == OozErrorCode.None
        ? "ok"
        : $"{Code} ({Codec}) at compressed offset {CompressedOffset}, decompressed offset {DecompressedOffset}";
}

/// <summary>
/// Managed decoder for Oodle Kraken, Mermaid, Selkie and Leviathan streams: a C# port of ooz
/// (https://github.com/powzix/ooz, GPL-3.0). It needs no native code and never loads or downloads an Oodle library.
///
/// The output is identical to ooz for every stream ooz decodes (checked bit-exact against a native ooz build on
/// real Oodle data). Unlike ooz it is safe on corrupt input: every read and write stays inside padded copies of the
/// caller's buffers, and corrupt data makes it return -1 instead of crashing. Thread-safe (one decoder per thread).
/// </summary>
public static unsafe class Ooz
{
    /// <summary>ooz's SAFE_SPACE: the decoder may write this many bytes past the end of its output buffer.
    /// <see cref="Decompress(ReadOnlySpan{byte}, Span{byte})"/> handles that internally; callers never need it.</summary>
    public const int SafeSpace = OozDecoder.OutputPad;

    [ThreadStatic] static OozDecoder? t_decoder;

    /// <summary>Decompresses src into exactly dst.Length bytes. Returns the number of bytes written (dst.Length),
    /// or -1 if the data is corrupt, uses an unsupported codec, or the sizes do not match.</summary>
    public static int Decompress(ReadOnlySpan<byte> src, Span<byte> dst) => Decompress(src, dst, out _);

    /// <summary>Same as <see cref="Decompress(ReadOnlySpan{byte}, Span{byte})"/>, and says why on failure.</summary>
    public static int Decompress(ReadOnlySpan<byte> src, Span<byte> dst, out OozError error)
    {
        error = default;
        if (dst.Length == 0)
        {
            // ooz: nothing to decode; the input must then be empty as well.
            if (src.Length == 0)
                return 0;
            error = new OozError(OozErrorCode.TrailingInput, 0, 0, DetectCodec(src));
            return -1;
        }
        if (src.Length < 2)
        {
            error = new OozError(OozErrorCode.TruncatedInput, 0, 0, OozCodec.Unknown);
            return -1;
        }
        if (src.Length > Array.MaxLength - 2 * OozDecoder.InputPad || dst.Length > Array.MaxLength - OozDecoder.OutputPad)
        {
            // the padded working copies would not fit in an array
            error = new OozError(OozErrorCode.OutOfMemory, 0, 0, DetectCodec(src));
            return -1;
        }

        byte[]? inBuf = null, outBuf = null;
        try
        {
            int inLen = src.Length + 2 * OozDecoder.InputPad;
            int outLen = dst.Length + OozDecoder.OutputPad;
            inBuf = ArrayPool<byte>.Shared.Rent(inLen);
            outBuf = ArrayPool<byte>.Shared.Rent(outLen);
            fixed (byte* pin = inBuf)
            fixed (byte* pout = outBuf)
            {
                new Span<byte>(pin, OozDecoder.InputPad).Clear();
                src.CopyTo(new Span<byte>(pin + OozDecoder.InputPad, src.Length));
                new Span<byte>(pin + OozDecoder.InputPad + src.Length, OozDecoder.InputPad).Clear();
                new Span<byte>(pout + dst.Length, OozDecoder.OutputPad).Clear();

                var dec = t_decoder ??= new OozDecoder();
                int n = dec.Decompress(pin + OozDecoder.InputPad, src.Length, pout, dst.Length, out error);
                if (n != dst.Length)
                {
                    if (!error.IsError)
                        error = new OozError(OozErrorCode.CorruptData, 0, 0, DetectCodec(src));
                    return -1;
                }
                new ReadOnlySpan<byte>(pout, n).CopyTo(dst);
                return n;
            }
        }
        catch (OutOfMemoryException)
        {
            error = new OozError(OozErrorCode.OutOfMemory, 0, 0, DetectCodec(src));
            return -1;
        }
        catch (Exception)
        {
            // Never let corrupt data escape as an exception. Any exception here is a bug in the port.
            t_decoder = null;
            error = new OozError(OozErrorCode.InternalError, 0, 0, DetectCodec(src));
            return -1;
        }
        finally
        {
            if (inBuf != null) ArrayPool<byte>.Shared.Return(inBuf);
            if (outBuf != null) ArrayPool<byte>.Shared.Return(outBuf);
        }
    }

    /// <summary>Shape of CUE4Parse's <c>DecompressDelegate</c>: true only when exactly dst.Length bytes were
    /// produced. Register with <c>CUE4Parse.Compression.Compression.UseOodle(Ooz.TryDecompress)</c>.</summary>
    public static bool TryDecompress(ReadOnlySpan<byte> src, Span<byte> dst, out int written)
    {
        int n = Decompress(src, dst, out _);
        written = n < 0 ? 0 : n;
        return n == dst.Length;
    }

    /// <summary>Identifies the codec from the first block header (2 bytes), without decoding.</summary>
    public static OozCodec DetectCodec(ReadOnlySpan<byte> src)
    {
        if (src.Length < 2)
            return OozCodec.Unknown;
        int b = src[0];
        if ((b & 0xF) != 0xC || ((b >> 4) & 3) != 0)
            return OozCodec.Unknown;
        return OozDecoder.CodecOf(src[1] & 0x7F);
    }
}
