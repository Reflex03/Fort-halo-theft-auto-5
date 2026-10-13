// C# port of vgmstream src/meta/ueba.c (UEBA header) and the packet framing of src/coding/binka_decoder.c
// (commit 7c520e8f9b078dfde2f84f1b5bcbad0c6c415806).
// Copyright (c) 2008-2025 Adam Gashlin, Fastelbja, Ronny Elfert, bnnm, et al. ISC licence: see LICENSE.
//
// Port notes (CthExtract): vgmstream renders exactly num_samples samples and fills the rest with silence when a
// packet cannot be decoded; UebaReader.Decode does the same and reports how far it got, so callers can reject a
// stream that broke early. Float samples are in PCM16 scale ("F16"); ToPcm16 converts like vgmstream's CLI
// (truncation, then clamp).
using System.Buffers.Binary;

namespace VgmstreamBinka;

/// <summary>The fields of a UEBA ("ABEU") header (ueba.c).</summary>
public sealed record UebaHeader(int Version, int Channels, int SampleRate, int NumSamples, int MaxFrameSize, int Flags,
    int SeekEntries, int FramesPerSeekBlock, int StartOffset);

/// <summary>Decoded audio: interleaved floats in PCM16 scale.</summary>
public sealed class UebaAudio
{
    public required UebaHeader Header { get; init; }
    public int Channels => Header.Channels;
    public int SampleRate => Header.SampleRate;
    /// <summary>Samples per channel (= header NumSamples, like vgmstream).</summary>
    public required int Samples { get; init; }
    /// <summary>Interleaved, Samples * Channels values, PCM16 scale (not clamped).</summary>
    public required float[] Interleaved { get; init; }
    /// <summary>Samples per channel that came from decoded packets; the rest is silence.</summary>
    public required int DecodedSamples { get; init; }
    public required int Packets { get; init; }
    /// <summary>Why decoding stopped before NumSamples, or null.</summary>
    public string? Error { get; init; }

    /// <summary>vgmstream's F16 -> S16: (int)f then clamp to [-32768, 32767].</summary>
    public short[] ToPcm16()
    {
        var o = new short[Interleaved.Length];
        for (int i = 0; i < o.Length; i++)
        {
            int v = (int)Interleaved[i];
            o[i] = (short)(v > 32767 ? 32767 : v < -32768 ? -32768 : v);
        }
        return o;
    }
}

public static class UebaReader
{
    // observed max is ~0x900 in 2ch files for UEBA (binka_decoder.c)
    private const int MaxFrameSizeChannel = 0x800;

    /// <summary>True when the bytes start with the UEBA id "ABEU".</summary>
    public static bool IsUeba(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == (byte)'A' && data[1] == (byte)'B' && data[2] == (byte)'E' && data[3] == (byte)'U';

    /// <summary>init_vgmstream_ueba: parse and check the header. Throws InvalidDataException on anything vgmstream rejects.</summary>
    public static UebaHeader ReadHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x1c || !IsUeba(data))
            throw new InvalidDataException("not a UEBA (Bink Audio 2) stream: missing 'ABEU' header");
        int version = data[0x04];
        int channels = data[0x05];
        int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data[0x08..]);
        int numSamples = BinaryPrimitives.ReadInt32LittleEndian(data[0x0c..]);
        int maxFrameSize = BinaryPrimitives.ReadUInt16LittleEndian(data[0x10..]);
        int flags = BinaryPrimitives.ReadUInt16LittleEndian(data[0x12..]);
        // v1 is enforced by exes, flags is always 1 but seems ignored
        if (version != 1 || flags != 1)
            throw new InvalidDataException($"UEBA: unknown version {version} / flags {flags}");
        int seekEntries = BinaryPrimitives.ReadUInt16LittleEndian(data[0x18..]);
        int framesPerSeekBlock = BinaryPrimitives.ReadUInt16LittleEndian(data[0x1a..]);
        int start = 0x1c + seekEntries * 0x02;
        if (channels < 1 || channels > 8)
            throw new InvalidDataException($"UEBA: {channels} channels");
        if (sampleRate < 300 || sampleRate > 96000)
            throw new InvalidDataException($"UEBA: sample rate {sampleRate}");
        if (numSamples < 0)
            throw new InvalidDataException($"UEBA: {numSamples} samples");
        if (start > data.Length)
            throw new InvalidDataException("UEBA: seek table runs past the end of the data");
        return new UebaHeader(version, channels, sampleRate, numSamples, maxFrameSize, flags, seekEntries, framesPerSeekBlock, start);
    }

    /// <summary>Decode a whole UEBA stream the way vgmstream renders it (num_samples samples, silence after a failure).</summary>
    /// <param name="maxSamples">Refuse streams longer than this (samples per channel) to bound memory.</param>
    public static UebaAudio Decode(ReadOnlySpan<byte> data, int maxSamples = 96000 * 60 * 10)
    {
        var h = ReadHeader(data);
        if (h.NumSamples > maxSamples)
            throw new InvalidDataException($"UEBA: {h.NumSamples} samples is longer than the {maxSamples} allowed");
        var handle = BinkaHandle.Init(h.SampleRate, h.Channels, BinkaMode.Ueba)
                     ?? throw new InvalidDataException("UEBA: decoder rejected the stream parameters");

        int channels = h.Channels;
        var output = new float[(long)h.NumSamples * channels];
        var frame = new float[handle.FrameSamples * channels];
        int bufSize = MaxFrameSizeChannel * channels;

        int offset = h.StartOffset;
        int filled = 0; // samples per channel written to output
        int packets = 0;
        string? error = null;
        int emptyReads = 0;

        while (filled < h.NumSamples)
        {
            // read_frame
            if (offset + 4 > data.Length) { error = $"stream ended at 0x{offset:X} after {packets} packets"; break; }
            uint header = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            if (header == 0x4B454553u) // "SEEK" (get_id32be("KEES") read as LE)
            {
                if (offset + 0x0f > data.Length) { error = "truncated SEEK chunk"; break; }
                int entries = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 0x0b)..]);
                if (entries <= 0) { error = $"SEEK chunk with {entries} entries at 0x{offset:X}"; break; }
                offset += 0x0f + entries * 0x02;
                if (++emptyReads > 1000) { error = "too many SEEK chunks without audio"; break; } // vgmstream's deadlock guard
                continue;
            }

            int sync = (int)(header & 0xFFFF);
            int packetSize = (int)((header >> 16) & 0xFFFF);
            offset += 0x04;

            if (sync != 0x9999) { error = $"missing sync at 0x{offset:X}"; break; }

            int packetSamples = 0;
            if (packetSize == 0xFFFF)
            {
                // last packet marker? (used in UEBA)
                if (offset + 4 > data.Length) { error = "truncated last-packet header"; break; }
                packetSize = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
                packetSamples = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 2)..]); // less samples than max
                offset += 0x04;
            }

            if (packetSize > bufSize) { error = $"packet too large, 0x{packetSize:X} vs 0x{bufSize:X}"; break; }
            if (offset + packetSize > data.Length) { error = "truncated packet"; break; }
            var packet = data.Slice(offset, packetSize);
            offset += packetSize;
            emptyReads = 0;

            if (packetSize == 0)
            {
                // nothing to decode in this call
                if (++emptyReads > 1000) { error = "too many empty packets"; break; }
                continue;
            }

            int samples = handle.Decode(packet, frame);
            if (samples < 0) { error = $"packet {packets} at 0x{offset - packetSize:X} failed to decode ({samples})"; break; }
            packets++;

            // last packet may emit less samples
            if (packetSamples != 0 && samples > packetSamples)
                samples = packetSamples;

            int copy = Math.Min(samples, h.NumSamples - filled);
            Array.Copy(frame, 0, output, (long)filled * channels, (long)copy * channels);
            filled += copy;
        }

        return new UebaAudio
        {
            Header = h,
            Samples = h.NumSamples,
            Interleaved = output,
            DecodedSamples = filled,
            Packets = packets,
            Error = error,
        };
    }
}
