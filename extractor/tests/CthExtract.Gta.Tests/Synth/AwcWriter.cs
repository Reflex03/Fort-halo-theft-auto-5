using CthExtract.Gta.Audio;
using CthExtract.Gta.Crypto;

namespace CthExtract.Gta.Tests.Synth;

/// <summary>Standard IMA ADPCM encoder producing AWC-style 0x800-byte frames (test-only).</summary>
public static class ImaEncoder
{
    public static byte[] EncodeFrames(short[] pcm)
    {
        int frames = Math.Max(1, (pcm.Length + ImaAdpcm.SamplesPerFrame - 1) / ImaAdpcm.SamplesPerFrame);
        var o = new byte[frames * ImaAdpcm.FrameSize];
        int predictor = 0, index = 0, s = 0;
        for (int f = 0; f < frames; f++)
        {
            int b = f * ImaAdpcm.FrameSize;
            new Blk(4).S16(0, index).S16(2, predictor).B.CopyTo(o, b);
            for (int p = 4; p < ImaAdpcm.FrameSize; p++)
            {
                int lo = Code(s < pcm.Length ? pcm[s] : (short)0, ref predictor, ref index); s++;
                int hi = Code(s < pcm.Length ? pcm[s] : (short)0, ref predictor, ref index); s++;
                o[b + p] = (byte)(lo | (hi << 4));
            }
        }
        return o;
    }

    private static int Code(short sample, ref int predictor, ref int index)
    {
        int step = ImaAdpcm.StepTable[index];
        int diff = sample - predictor;
        int code = 0;
        if (diff < 0) { code = 8; diff = -diff; }
        if (diff >= step) { code |= 4; diff -= step; }
        if (diff >= step >> 1) { code |= 2; diff -= step >> 1; }
        if (diff >= step >> 2) code |= 1;
        ImaAdpcm.Expand(code, ref predictor, ref index);
        return code;
    }
}

/// <summary>Our own AWC writer: an sfx pack (one mono wave per stream: format tag 0xFA + data tag 0x55) or a
/// multichannel blocked IMA stream (stream id 0 with header tag 0x48). Layout per vgmstream's documentation (ISC).</summary>
public static class AwcWriter
{
    public sealed record Wave(string Name, short[] Pcm, int Rate, int Codec = 4, int LoopPoint = -1);

    public static byte[] SfxPack(IReadOnlyList<Wave> waves, uint[]? encryptKey = null, bool chunkIndices = false, uint encryptFlag = 0x00020000, bool setFlagOnly = false)
    {
        int n = waves.Count;
        int headerLen = 16 + (chunkIndices ? 2 * n : 0) + 4 * n + 8 * 2 * n;
        var body = new MemoryStream();
        var tags = new List<(ulong fmt, ulong data)>();
        int Align(int x) => (x + 15) / 16 * 16;
        int pos = Align(headerLen);
        var chunks = new List<(int offset, byte[] bytes)>();
        foreach (var w in waves)
        {
            var fmt = new Blk(0x14).U32(0, (uint)w.Pcm.Length).U32(4, unchecked((uint)w.LoopPoint)).U16(8, w.Rate).S16(10, -100)
                .U16(0x0C, 0).U16(0x0E, 0).U16(0x10, 0).U8(0x12, 0).U8(0x13, w.Codec).B;
            byte[] data = w.Codec == 4 ? ImaEncoder.EncodeFrames(w.Pcm) : Pcm16(w.Pcm);
            if (encryptKey is not null && !setFlagOnly) Xxtea.EncryptInPlace(data, AwcFile.EffectiveKey(encryptKey));
            int fo = pos; pos = Align(pos + fmt.Length);
            int dof = pos; pos = Align(pos + data.Length);
            chunks.Add((fo, fmt)); chunks.Add((dof, data));
            tags.Add((Tag(0xFA, fmt.Length, fo), Tag(0x55, data.Length, dof)));
        }
        var file = new byte[pos];
        uint flags = 0xFF000001 | (chunkIndices ? 0x00010000u : 0) | (encryptKey is not null ? encryptFlag : 0);
        var h = new Blk(headerLen).U32(0, AwcFile.MagicLe).U32(4, flags).U32(8, (uint)n).U32(12, (uint)headerLen);
        int o = 16;
        if (chunkIndices) for (int i = 0; i < n; i++, o += 2) h.U16(o, i * 2);
        for (int i = 0; i < n; i++, o += 4) h.U32(o, (2u << 29) | Hashes.AwcStreamId(waves[i].Name));
        for (int i = 0; i < n; i++, o += 16) h.U64(o, tags[i].fmt).U64(o + 8, tags[i].data);
        h.B.CopyTo(file, 0);
        foreach (var (off, bytes) in chunks) bytes.CopyTo(file, off);
        return file;
    }

    /// <summary>Multichannel IMA stream: blocks of blockSize bytes, each with per-channel 0x18-byte info, seek tables,
    /// padding to 0x800 and then every channel's 0x800-byte frames.</summary>
    public static byte[] Multichannel(IReadOnlyList<Wave> channels, int framesPerBlock = 2)
    {
        int ch = channels.Count;
        int samplesPerBlock = framesPerBlock * ImaAdpcm.SamplesPerFrame;
        int total = channels.Max(c => c.Pcm.Length);
        int blocks = (total + samplesPerBlock - 1) / samplesPerBlock;
        int header = ch * 0x18 + ch * framesPerBlock * 4;
        int headerPadded = (header + 0x7FF) / 0x800 * 0x800;
        int blockSize = headerPadded + ch * framesPerBlock * 0x800;
        var data = new byte[blocks * blockSize];
        for (int b = 0; b < blocks; b++)
        {
            int bo = b * blockSize;
            for (int c = 0; c < ch; c++)
            {
                int start = b * samplesPerBlock;
                int count = Math.Clamp(channels[c].Pcm.Length - start, 0, samplesPerBlock);
                var slice = channels[c].Pcm.AsSpan(start, count).ToArray();
                var frames = ImaEncoder.EncodeFrames(slice.Length == 0 ? new short[1] : slice);
                Array.Resize(ref frames, framesPerBlock * 0x800);
                new Blk(0x18).U32(0, (uint)(b * framesPerBlock)).U32(4, (uint)framesPerBlock).U32(8, 0).U32(0x0C, (uint)count).B.CopyTo(data, bo + c * 0x18);
                frames.CopyTo(data, bo + headerPadded + c * framesPerBlock * 0x800);
            }
        }
        int n = ch + 1;
        int headerLen = 16 + 4 * n + 8 * 2;
        int hdrChunk = (headerLen + 15) / 16 * 16;
        int hdrSize = 0x0C + 0x10 * ch;
        int dataOff = (hdrChunk + hdrSize + 0x7FF) / 0x800 * 0x800;
        var file = new byte[dataOff + data.Length];
        var h = new Blk(headerLen).U32(0, AwcFile.MagicLe).U32(4, 0xFF040001).U32(8, (uint)n).U32(12, (uint)headerLen);
        h.U32(16, 2u << 29); // stream 0: the multichannel stream, 2 tags
        for (int c = 0; c < ch; c++) h.U32(20 + 4 * c, Hashes.AwcStreamId(channels[c].Name));
        int to = 16 + 4 * n;
        h.U64(to, Tag(0x48, hdrSize, hdrChunk)).U64(to + 8, Tag(0x55, data.Length, dataOff));
        h.B.CopyTo(file, 0);
        var hc = new Blk(hdrSize).U32(0, (uint)blocks).U32(4, (uint)blockSize).U32(8, (uint)ch);
        for (int c = 0; c < ch; c++)
            hc.U32(0x0C + 0x10 * c, Hashes.AwcStreamId(channels[c].Name)).U32(0x0C + 0x10 * c + 4, (uint)channels[c].Pcm.Length)
              .U16(0x0C + 0x10 * c + 0x0A, channels[c].Rate).U8(0x0C + 0x10 * c + 0x0C, 4);
        hc.B.CopyTo(file, hdrChunk);
        data.CopyTo(file, dataOff);
        return file;
    }

    private static ulong Tag(int type, int size, int offset) => ((ulong)type << 56) | ((ulong)(uint)size << 28) | (uint)offset;

    private static byte[] Pcm16(short[] pcm)
    {
        var b = new Blk(pcm.Length * 2);
        for (int i = 0; i < pcm.Length; i++) b.S16(i * 2, pcm[i]);
        return b.B;
    }

    public static short[] Sweep(int rate, double seconds, double f0, double f1, double amp = 0.6)
    {
        int n = (int)(rate * seconds);
        var o = new short[n];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / n;
            double f = f0 + (f1 - f0) * (0.5 - 0.5 * Math.Cos(2 * Math.PI * t));
            phase += 2 * Math.PI * f / rate;
            o[i] = (short)(Math.Sin(phase) * amp * 32767);
        }
        return o;
    }
}
