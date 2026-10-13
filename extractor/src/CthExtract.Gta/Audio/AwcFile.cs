using System.Buffers.Binary;
using CthExtract.Gta.Crypto;

namespace CthExtract.Gta.Audio;

/// <summary>One sound in an AWC: a mono wave (sfx pack entry, or one channel of a multichannel stream).</summary>
public sealed class AwcStream
{
    public uint Id { get; init; }
    public int Samples { get; init; }
    public int SampleRate { get; init; }
    public int Codec { get; init; }
    /// <summary>Loop start sample from the format chunk, or -1 when the sound does not loop (format word at +4) [inference].</summary>
    public int LoopPoint { get; init; } = -1;
    public bool InMultichannel { get; init; }
    internal Func<short[]>? Decoder { get; set; }
    /// <summary>How the data was decoded (e.g. "decrypted"), set after DecodePcm.</summary>
    public string? Note { get; internal set; }
    public double Seconds => SampleRate > 0 ? (double)Samples / SampleRate : 0;
    public bool Loops => LoopPoint >= 0;
    public string CodecName => Codec switch { 0 or 1 => "PCM", 4 => "IMA-ADPCM", 5 => "XMA2", 7 => "MPEG", 8 => "Vorbis", 0x0D => "Opus", 0x0F => "ATRAC9", _ => $"codec {Codec}" };
    public bool CanDecode => Decoder is not null;
    public short[] DecodePcm() => Decoder?.Invoke() ?? throw new NotSupportedException($"AWC {CodecName} is not supported");
}

/// <summary>
/// RAGE Audio Wave Container reader (PC, little-endian "ADAT"). Format facts from vgmstream's awc.c (ISC):
/// header {magic, flags 0xFF??0001, stream count, header size}, optional u16 chunk indices (flag 0x10000), one u32 per
/// stream (29-bit id + 3-bit tag count), then 8-byte tags {type:8, size:28, offset:28}. Tag 0x55 = data, 0xFA = sfx
/// format, 0x48 = multichannel stream header. A file whose magic is wrong is tried as wholly encrypted.
/// Which flag marks an encrypted single-wave data chunk is disputed: CodeWalker reads 0x20000 as "single-channel
/// encrypted" and 0x80000 as "multichannel encrypted", vgmstream reads 0x80000 as "encrypted" and 0x20000 as
/// "unordered". For single waves with either bit set we decode both the decrypted and the plain chunk and keep the one
/// that looks like audio (IMA frame headers in range / PCM not white noise); multichannel blocks are decrypted on 0x80000.
/// </summary>
public sealed class AwcFile
{
    public const uint MagicLe = 0x54414441; // "ADAT"
    public uint Flags { get; private set; }
    /// <summary>0x80000: vgmstream "encrypted data", CodeWalker "multichannel encrypted".</summary>
    public bool EncryptFlag => (Flags & 0x00080000) != 0;
    /// <summary>0x20000: CodeWalker "single-channel encrypted", vgmstream "unordered chunks".</summary>
    public bool SingleEncryptFlag => (Flags & 0x00020000) != 0;
    public bool ChunksEncrypted => !WholeFileWasEncrypted && (EncryptFlag || (!Multichannel && SingleEncryptFlag));
    public bool WholeFileWasEncrypted { get; private set; }
    public bool Multichannel { get; private set; }
    public List<AwcStream> Streams { get; } = new();
    public List<string> Notes { get; } = new();

    /// <summary>Rockstar's AWC cipher is XXTEA whose key term is additionally XORed with 0x7B3A207F, i.e. standard XXTEA
    /// with every key word XOR 0x7B3A207F (vgmstream: "the key is xored by a game/platform specific 32-bit value").
    /// Checked against CodeWalker as a local oracle on synthetic files.</summary>
    public const uint KeyXor = 0x7B3A207F;

    public static uint[] EffectiveKey(uint[] awcKey) => awcKey.Select(k => k ^ KeyXor).ToArray();

    public static AwcFile Parse(byte[] input, uint[]? awcKey)
    {
        var a = new AwcFile();
        awcKey = awcKey is null ? null : EffectiveKey(awcKey);
        byte[] d = input;
        if (d.Length < 16) throw new InvalidDataException("AWC too short");
        if (BinaryPrimitives.ReadUInt32LittleEndian(d) != MagicLe)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(d) == MagicLe) throw new InvalidDataException("big-endian (console) AWC");
            if (awcKey is null) throw new KeyUnavailableException("AWC is encrypted and the AWC key is unavailable");
            if (d.Length % 4 != 0) throw new InvalidDataException("AWC magic missing and size is not a multiple of 4");
            d = (byte[])input.Clone();
            Xxtea.DecryptInPlace(d, awcKey);
            if (BinaryPrimitives.ReadUInt32LittleEndian(d) != MagicLe) throw new InvalidDataException("AWC magic missing (not decryptable with the AWC key)");
            a.WholeFileWasEncrypted = true;
        }
        a.Flags = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4));
        if ((a.Flags & 0xFF000000) != 0xFF000000) a.Notes.Add($"unexpected flags 0x{a.Flags:X8}");
        int entries = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(8));
        if (entries <= 0 || entries > 100000) throw new InvalidDataException($"implausible AWC stream count {entries}");
        int off = 16;
        if ((a.Flags & 0x00010000) != 0) off += 2 * entries;
        var ids = new uint[entries];
        var tagCounts = new int[entries];
        for (int i = 0; i < entries; i++, off += 4)
        {
            uint w = U32(d, off);
            ids[i] = w & 0x1FFFFFFF;
            tagCounts[i] = (int)(w >> 29);
        }
        var tags = new List<(byte type, int size, int offset)>[entries];
        for (int i = 0; i < entries; i++)
        {
            tags[i] = new();
            for (int t = 0; t < tagCounts[i]; t++, off += 8)
            {
                ulong q = U64(d, off);
                tags[i].Add(((byte)(q >> 56), (int)((q >> 28) & 0x0FFFFFFF), (int)(q & 0x0FFFFFFF)));
            }
        }
        a.Multichannel = ids[0] == 0;
        if (a.Multichannel && a.ChunksEncrypted && awcKey is null) throw new KeyUnavailableException("AWC stream blocks are encrypted and the AWC key is unavailable");
        if (a.Multichannel) a.ParseMultichannel(d, ids, tags, awcKey);
        else
            for (int i = 0; i < entries; i++)
            {
                var data = tags[i].FirstOrDefault(t => t.type == 0x55);
                var fmt = tags[i].FirstOrDefault(t => t.type == 0xFA);
                if (fmt.size == 0 && fmt.offset == 0) { a.Streams.Add(new AwcStream { Id = ids[i], Codec = -1 }); continue; }
                int samples = (int)U32(d, fmt.offset);
                int loop = (int)U32(d, fmt.offset + 4);
                int rate = U16(d, fmt.offset + 8);
                int codec = d[Check(d, fmt.offset + 0x13, 1)];
                int dataOff = data.offset, dataLen = data.size;
                Check(d, dataOff, dataLen);
                byte[] src = d;
                var stream = new AwcStream { Id = ids[i], Samples = samples, SampleRate = rate, Codec = codec, LoopPoint = loop };
                if (codec is 0 or 1 or 4)
                    stream.Decoder = () => a.DecodeSingle(src.AsSpan(dataOff, dataLen).ToArray(), codec, samples, awcKey, stream);
                a.Streams.Add(stream);
            }
        return a;
    }

    private short[] DecodeSingle(byte[] chunk, int codec, int samples, uint[]? key, AwcStream stream)
    {
        short[] Decode(byte[] c) => codec == 4 ? ImaAdpcm.DecodeFrames(c, samples) : Pcm16(c, samples);
        if (!ChunksEncrypted) return Decode(chunk);
        bool plainOk = LooksLikeAudio(chunk, codec, out var plainPcm, samples);
        if (key is null)
        {
            if (plainOk) { stream.Note = "flagged encrypted but decodes as plain audio"; return plainPcm!; }
            throw new KeyUnavailableException("AWC data chunk is encrypted and the AWC key is unavailable");
        }
        var dec = (byte[])chunk.Clone();
        Xxtea.DecryptInPlace(dec, key);
        bool decOk = LooksLikeAudio(dec, codec, out var decPcm, samples);
        if (decOk || !plainOk) { stream.Note = decOk ? "decrypted" : "decrypted (neither variant looks like audio)"; return decPcm ?? Decode(dec); }
        stream.Note = "flagged encrypted but decodes as plain audio";
        return plainPcm!;
    }

    /// <summary>IMA: every frame header has a step index 0..88 and a sane predictor; PCM: not white noise.</summary>
    public static bool LooksLikeAudio(byte[] data, int codec, out short[]? pcm, int samples)
    {
        pcm = null;
        if (codec == 4)
        {
            int frames = (data.Length + ImaAdpcm.FrameSize - 1) / ImaAdpcm.FrameSize;
            for (int f = 0; f < frames; f++)
            {
                int o = f * ImaAdpcm.FrameSize;
                if (o + 4 > data.Length) break;
                int idx = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(o));
                if (idx < 0 || idx > 88) return false;
            }
            pcm = ImaAdpcm.DecodeFrames(data, samples);
            return true;
        }
        pcm = Pcm16(data, samples);
        if (pcm.Length < 16) return true;
        double diff = 0, abs = 0;
        for (int i = 1; i < pcm.Length; i++) { diff += Math.Abs(pcm[i] - pcm[i - 1]); abs += Math.Abs((int)pcm[i]); }
        return abs < 1 || diff / abs < 0.9; // white noise gives ~1.1+, real sound far less
    }

    private void ParseMultichannel(byte[] d, uint[] ids, List<(byte type, int size, int offset)>[] tags, uint[]? key)
    {
        var hdr = tags[0].FirstOrDefault(t => t.type == 0x48);
        var data = tags[0].FirstOrDefault(t => t.type == 0x55);
        if (hdr.offset == 0) throw new InvalidDataException("multichannel AWC without stream header");
        int blockCount = (int)U32(d, hdr.offset), blockSize = (int)U32(d, hdr.offset + 4), channels = (int)U32(d, hdr.offset + 8);
        if (channels <= 0 || channels > 64 || blockSize <= 0 || blockCount <= 0) throw new InvalidDataException("bad multichannel AWC header");
        var chans = new List<(uint hash, int samples, int rate, int codec)>();
        for (int c = 0; c < channels; c++)
        {
            int o = hdr.offset + 0x0C + 0x10 * c;
            chans.Add((U32(d, o), (int)U32(d, o + 4), U16(d, o + 0x0A), d[Check(d, o + 0x0C, 1)]));
        }
        Check(d, data.offset, data.size);
        for (int c = 0; c < channels; c++)
        {
            int ch = c;
            var (hash, samples, rate, codec) = chans[c];
            Func<short[]>? dec = codec != 4 ? null : () => DecodeBlockedIma(d, data.offset, data.size, blockSize, channels, ch, samples, key);
            Streams.Add(new AwcStream { Id = hash & 0x1FFFFFFF, Samples = samples, SampleRate = rate, Codec = codec, InMultichannel = true, Decoder = dec });
        }
    }

    /// <summary>Blocked multichannel IMA: each block = per-channel info (0x10 or 0x18 bytes: start, frames, skip, samples),
    /// per-channel seek tables (4 bytes per frame), padding to 0x800, then each channel's frames.</summary>
    private short[] DecodeBlockedIma(byte[] d, int start, int size, int blockSize, int channels, int ch, int total, uint[]? key)
    {
        // total comes from the file: never pre-allocate more than the data can hold (IMA = 2 samples per byte)
        if (total < 0) throw new InvalidDataException($"multichannel AWC channel {ch}: negative sample count {total}");
        var o = new List<short>((int)Math.Min(total, 2L * size + 16));
        for (long b = start; b < (long)start + size && o.Count < total; b += blockSize)
        {
            int len = (int)Math.Min(blockSize, (long)start + size - b);
            var block = d.AsSpan((int)b, len).ToArray();
            if (ChunksEncrypted) Xxtea.DecryptInPlace(block, key!);
            int chHeader = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(0x14)) == 0 ? 0x18 : 0x10;
            int header = 0;
            for (int c = 0; c < channels; c++) header += chHeader + 4 * BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(chHeader * c + 4));
            if (header % 0x800 != 0) header += 0x800 - header % 0x800;
            int frameOff = header;
            for (int c = 0; c < ch; c++) frameOff += 0x800 * BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(chHeader * c + 4));
            int frames = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(chHeader * ch + 4));
            int blockSamples = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(chHeader * ch + 0x0C));
            if (frameOff + frames * 0x800 > block.Length) throw new InvalidDataException("multichannel AWC block out of range");
            var pcm = ImaAdpcm.DecodeFrames(block.AsSpan(frameOff, frames * 0x800), blockSamples);
            o.AddRange(pcm);
        }
        if (o.Count > total) o.RemoveRange(total, o.Count - total);
        return o.ToArray();
    }

    private static short[] Pcm16(byte[] chunk, int samples)
    {
        int n = Math.Min(samples, chunk.Length / 2);
        var o = new short[n];
        for (int i = 0; i < n; i++) o[i] = BinaryPrimitives.ReadInt16LittleEndian(chunk.AsSpan(i * 2));
        return o;
    }

    private static int Check(byte[] d, int off, int len)
    {
        if (off < 0 || len < 0 || (long)off + len > d.Length) throw new InvalidDataException($"AWC chunk 0x{off:X}+0x{len:X} out of range");
        return off;
    }
    private static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(Check(d, o, 4)));
    private static ulong U64(byte[] d, int o) => BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(Check(d, o, 8)));
    private static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(Check(d, o, 2)));
}
