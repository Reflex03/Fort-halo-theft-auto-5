using System.IO.Hashing;
using NVorbis;
using OggVorbisEncoder;

namespace CthExtract.Halo.Tests;

/// <summary>
/// Synthetic FMOD FSB5 banks written by our own code: Vorbis packets come from OggVorbisEncoder (MIT, a libvorbis
/// port) and are packed the way FSB5 stores them ([u16 size][packet]..., VORBISDATA chunk with the setup crc32).
/// </summary>
public static class SynthAudio
{
    public sealed record Encoded(int Channels, int Rate, byte[] Setup, List<byte[]> Packets, int Frames, byte[] ReferenceOgg)
    {
        public uint SetupCrc => Crc32.HashToUInt32(Setup);
    }

    /// <summary>A tone (different per channel) encoded with OggVorbisEncoder; also returns a normal .ogg of the same packets as a decode reference.</summary>
    public static Encoded Encode(int channels, int rate, float quality, double seconds, double freq = 440)
    {
        var info = VorbisInfo.InitVariableBitRate(channels, rate, quality);
        var setup = HeaderPacketBuilder.BuildBooksPacket(info).PacketData;
        var packets = new List<byte[]>();
        var refOgg = new MemoryStream();
        var os = new OggStream(1);
        os.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
        os.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(new Comments()));
        os.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));
        while (os.PageOut(out var hp, true)) { refOgg.Write(hp.Header); refOgg.Write(hp.Body); }
        var proc = ProcessingState.Create(info);
        int frames = (int)(rate * seconds), chunk = 1024;
        var buf = new float[channels][];
        for (int c = 0; c < channels; c++) buf[c] = new float[chunk];
        for (int pos = 0; pos < frames; pos += chunk)
        {
            int n = Math.Min(chunk, frames - pos);
            for (int c = 0; c < channels; c++)
                for (int i = 0; i < n; i++)
                    buf[c][i] = 0.4f * MathF.Sin(2 * MathF.PI * (float)(freq * (1 + 0.5 * c)) * (pos + i) / rate);
            proc.WriteData(buf, n);
            Drain();
        }
        proc.WriteEndOfStream();
        Drain();
        while (os.PageOut(out var tp, true)) { refOgg.Write(tp.Header); refOgg.Write(tp.Body); }
        return new Encoded(channels, rate, setup, packets, frames, refOgg.ToArray());

        void Drain()
        {
            while (proc.PacketOut(out var p))
            {
                packets.Add(p.PacketData);
                if (!os.Finished)
                {
                    os.PacketIn(p);
                    while (os.PageOut(out var page, false)) { refOgg.Write(page.Header); refOgg.Write(page.Body); }
                }
            }
        }
    }

    public sealed record FsbItem(string Name, Encoded Audio);

    /// <summary>FSB5 v1, codec VORBIS, with a name table and one VORBISDATA chunk per sample.</summary>
    public static byte[] BuildFsb5(IReadOnlyList<FsbItem> items)
    {
        var freqIds = new Dictionary<int, int> { { 8000, 1 }, { 11000, 2 }, { 16000, 4 }, { 22050, 5 }, { 24000, 6 }, { 32000, 7 }, { 44100, 8 }, { 48000, 9 }, { 96000, 10 } };
        var data = new MemoryStream();
        var offsets = new List<long>();
        foreach (var it in items)
        {
            while (data.Length % 32 != 0) data.WriteByte(0);
            offsets.Add(data.Length);
            var w = new BinaryWriter(data);
            foreach (var p in it.Audio.Packets) { w.Write((ushort)p.Length); w.Write(p); }
            w.Write((ushort)0);
        }
        while (data.Length % 32 != 0) data.WriteByte(0);

        var sh = new MemoryStream();
        var shw = new BinaryWriter(sh);
        for (int i = 0; i < items.Count; i++)
        {
            var a = items[i].Audio;
            ulong chanBits = a.Channels switch { 1 => 0UL, 2 => 1UL, 6 => 2UL, 8 => 3UL, _ => throw new ArgumentException("channels") };
            ulong enc = 1UL | ((ulong)freqIds[a.Rate] << 1) | (chanBits << 5) | ((ulong)(offsets[i] / 32) << 7) | ((ulong)a.Frames << 34);
            shw.Write(enc);
            uint size = 8; // crc32 + one empty seek-table word
            uint chunk = 0u | (size << 1) | (11u << 25); // VORBISDATA, no more chunks
            shw.Write(chunk);
            shw.Write(a.SetupCrc);
            shw.Write(0u);
        }

        var nt = new MemoryStream();
        var ntw = new BinaryWriter(nt);
        var names = items.Select(it => System.Text.Encoding.ASCII.GetBytes(it.Name + "\0")).ToList();
        uint pos = (uint)(4 * items.Count);
        foreach (var nb in names) { ntw.Write(pos); pos += (uint)nb.Length; }
        foreach (var nb in names) ntw.Write(nb);
        while (nt.Length % 32 != 0) nt.WriteByte(0);

        var o = new MemoryStream();
        var ow = new BinaryWriter(o);
        ow.Write("FSB5"u8.ToArray()); ow.Write(1u); ow.Write((uint)items.Count); ow.Write((uint)sh.Length); ow.Write((uint)nt.Length); ow.Write((uint)data.Length);
        ow.Write(15u); ow.Write(0u); ow.Write(0u); ow.Write(new byte[16]); ow.Write(new byte[8]);
        if (o.Length != 0x3C) throw new InvalidOperationException("FSB5 v1 header must be 0x3C bytes");
        ow.Write(sh.ToArray()); ow.Write(nt.ToArray()); ow.Write(data.ToArray());
        return o.ToArray();
    }

    /// <summary>Interleaved float PCM of an Ogg Vorbis file (NVorbis 0.10.5, test oracle only).</summary>
    public static float[] Decode(byte[] ogg, out int channels, out int rate)
    {
        using var r = new VorbisReader(new MemoryStream(ogg), true);
        channels = r.Channels; rate = r.SampleRate;
        var all = new List<float>();
        var buf = new float[4096];
        int n;
        while ((n = r.ReadSamples(buf, 0, buf.Length)) > 0) all.AddRange(buf.AsSpan(0, n).ToArray());
        return all.ToArray();
    }
}
