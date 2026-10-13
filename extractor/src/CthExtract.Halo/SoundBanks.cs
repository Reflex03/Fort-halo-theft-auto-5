using System.Globalization;
using Fmod5Sharp;
using Fmod5Sharp.CodecRebuilders;
using Fmod5Sharp.FmodTypes;
using CthExtract.Core;

namespace CthExtract.Halo;

/// <summary>A sound picked for a row: which bank, which subsound, and why.</summary>
public sealed record SoundPick(SoundBank Bank, FsbInfoRecord Record, string Route);

/// <summary>One MCC FMOD bank (&lt;name&gt;.fsb + &lt;name&gt;.fsb.info). The .info is read eagerly (≈280 bytes per
/// sample); the FSB is opened on first use and read lazily, one sample at a time.</summary>
public sealed class SoundBank : IDisposable
{
    public string FsbPath { get; }
    public FsbInfo Info { get; }
    private FmodSoundBank? bank;
    private readonly Log log;
    private readonly Func<IReadOnlyList<string>>? binaries;

    /// <summary>Setup-header crcs this bank needs that neither the table nor the install's binaries provide.</summary>
    public IReadOnlySet<uint> MissingSetupHeaders { get; private set; } = new HashSet<uint>();

    /// <param name="binaries">Binaries of the install to search for FMOD's own Vorbis setup headers (lazy).</param>
    public SoundBank(string fsbPath, FsbInfo info, Log log, Func<IReadOnlyList<string>>? binaries = null)
    {
        FsbPath = fsbPath; Info = info; this.log = log; this.binaries = binaries;
    }

    public string Name => Path.GetFileName(FsbPath);

    public FmodSoundBank Fsb
    {
        get
        {
            if (bank != null) return bank;
            bank = FsbLoader.LoadFsbFromFileLazy(FsbPath);
            log.Info($"[halo] opened {Name}: FSB5 v{bank.Header.Version}, {bank.Samples.Count} samples, codec {bank.Header.AudioType}; " +
                     $"{Info.Records.Count} .info records{(Info.Records.Count != bank.Samples.Count ? " (COUNT MISMATCH: index mapping may be off)" : "")}" +
                     $"{(Info.DuplicateHashes > 0 ? $", {Info.DuplicateHashes} duplicate hashes" : "")}");
            try { MissingSetupHeaders = VorbisSetup.EnsureFor(bank, Name, binaries ?? (() => Array.Empty<string>()), log); }
            catch (Exception e) { log.Warn($"[halo] setup header check for {Name} failed: {e.Message}"); }
            return bank;
        }
    }

    public void Dispose() { bank?.Dispose(); bank = null; }
}

/// <summary>All banks of halo3\fmod\pc, searched in order (sfx first) like TagTool's FMODSoundCache.FindSound.</summary>
public sealed class SoundBanks : IDisposable
{
    public IReadOnlyList<SoundBank> Banks { get; }

    public SoundBanks(IReadOnlyList<SoundBank> banks) => Banks = banks;

    public static SoundBanks Load(HaloInstall install, Log log)
    {
        var list = new List<SoundBank>();
        IReadOnlyList<string>? bins = null;
        IReadOnlyList<string> Binaries() => bins ??= VorbisSetup.CandidateBinaries(install);
        foreach (var (fsb, info) in install.SoundBanks())
        {
            try
            {
                var fi = FsbInfo.Load(info);
                log.Info($"[halo] {Path.GetFileName(info)}: {fi.Records.Count} records{(fi.TrailingBytes != 0 ? $", {fi.TrailingBytes} trailing bytes" : "")}");
                list.Add(new SoundBank(fsb, fi, log, Binaries));
            }
            catch (Exception e)
            {
                log.Warn($"[halo] cannot read {info}: {e.GetType().Name}: {e.Message}");
            }
        }
        return new SoundBanks(list);
    }

    public (SoundBank Bank, FsbInfoRecord Record)? FindHash(uint hash)
    {
        foreach (var b in Banks)
        {
            int i = b.Info.IndexOfHash(hash);
            if (i >= 0) return (b, b.Info.Records[i]);
        }
        return null;
    }

    public List<(SoundBank Bank, FsbInfoRecord Record)> Match(string pattern)
    {
        var all = new List<(SoundBank, FsbInfoRecord)>();
        foreach (var b in Banks)
            foreach (var r in b.Info.Match(pattern)) all.Add((b, r));
        return all;
    }

    public void Dispose() { foreach (var b in Banks) b.Dispose(); }
}

public enum SoundPurpose { Fire, Throw, ShieldRecharge, ShieldDepleted }

/// <summary>Ranks .info filenames for a purpose when the tag chain gave no answer.</summary>
public static class SoundPicker
{
    private static readonly string[] AlwaysBad = { "lod", "distant", "_far", "far_", "tail", "reverb", "flyby", "fly_by", "impact", "bounce", "foley" };

    private static readonly Dictionary<SoundPurpose, string[]> Bad = new()
    {
        [SoundPurpose.Fire] = new[] { "empty", "dry", "misfire", "overheat", "reload", "charge", "melee", "ready", "zoom", "pickup", "switch", "loop", "_out", "detonat", "expl", "vent", "spin" },
        [SoundPurpose.Throw] = new[] { "detonat", "expl", "beep", "stick", "pickup", "loop", "arm" },
        [SoundPurpose.ShieldRecharge] = new[] { "deplet", "empty", "down", "hit", "impact", "damage", "low" },
        [SoundPurpose.ShieldDepleted] = new[] { "charg", "recharge", "hit", "impact", "loop" },
    };

    private static readonly Dictionary<SoundPurpose, string[]> Good = new()
    {
        [SoundPurpose.Fire] = new[] { "fire" },
        [SoundPurpose.Throw] = new[] { "throw" },
        [SoundPurpose.ShieldRecharge] = new[] { "charg" },
        [SoundPurpose.ShieldDepleted] = new[] { "deplet", "down", "empty" },
    };

    public static double Score(FsbInfoRecord r, SoundPurpose purpose)
    {
        var n = r.FileName.ToLowerInvariant();
        double s = 0;
        foreach (var b in AlwaysBad) if (n.Contains(b)) s -= 10;
        foreach (var b in Bad[purpose]) if (n.Contains(b)) s -= 10;
        foreach (var g in Good[purpose]) if (n.Contains(g)) s += 5;
        if (r.ChannelCount == 1) s += 0.5;                // Minecraft positions mono sounds
        if (r.SampleCount == 0) s -= 20;
        s -= n.Length / 1000.0;                           // shorter path = more generic
        s -= r.Index / 1e7;                               // stable: earlier record wins ties
        return s;
    }

    public static IReadOnlyList<(SoundBank Bank, FsbInfoRecord Record)> Rank(IEnumerable<(SoundBank Bank, FsbInfoRecord Record)> candidates, SoundPurpose purpose) =>
        candidates.OrderByDescending(c => Score(c.Record, purpose)).ToList();
}

/// <summary>FSB subsound → Ogg Vorbis bytes for Minecraft.</summary>
public static class SoundDecode
{
    /// <summary>Longest sound we accept for a one-shot effect; anything longer is almost certainly the wrong subsound.</summary>
    public const double MaxSeconds = 30;

    public static byte[] ToOgg(SoundBank bank, FsbInfoRecord rec, List<string> notes)
    {
        var fsb = bank.Fsb;
        if (rec.Index >= fsb.Samples.Count)
            throw new InvalidDataException($"record {rec.Index} beyond the {fsb.Samples.Count} samples of {bank.Name}");
        var sample = fsb.Samples[rec.Index];
        var meta = sample.Metadata;
        double seconds = meta.Frequency > 0 ? meta.SampleCount / (double)meta.Frequency : 0;
        notes.Add(string.Create(CultureInfo.InvariantCulture,
            $"{bank.Name}#{rec.Index} '{sample.Name}' {meta.Channels}ch {meta.Frequency}Hz {seconds:0.###}s, info '{rec.FileName}' hash 0x{rec.Hash:x8}"));
        if (rec.ChannelCount > 0 && rec.ChannelCount != meta.Channels)
            notes.Add($"warning: .info says {rec.ChannelCount} channels, FSB says {meta.Channels}");
        if (seconds <= 0 || seconds > MaxSeconds)
            throw new InvalidDataException($"implausible length {seconds:0.###} s");

        switch (fsb.Header.AudioType)
        {
            case FmodAudioType.VORBIS:
                var crc = FmodVorbisRebuilder.GetSetupHeaderCrc(sample);
                if (crc is null) throw new InvalidDataException("Vorbis sample without a VORBISDATA chunk");
                if (!FmodVorbisRebuilder.HasSetupHeader(crc.Value))
                    throw new KeyNotFoundException($"Vorbis setup header 0x{crc:x8} is neither in the libvorbis table nor in the install's FMOD binaries");
                var ogg = FmodVorbisRebuilder.RebuildOggFile(sample);
                Verify(ogg, (int)meta.Channels, meta.Frequency, meta.SampleCount, notes);
                notes.Add("remuxed losslessly (no re-encode)");
                return ogg;
            case FmodAudioType.PCM16:
            case FmodAudioType.PCM8:
            case FmodAudioType.PCMFLOAT:
                var pcm = PcmFloat(sample.SampleBytes, fsb.Header.AudioType, (int)meta.Channels, (int)meta.SampleCount);
                var enc = OggEncode.FromPcm(pcm, meta.Frequency, out int encChannels, out var encDetail);
                notes.Add($"PCM bank: encoded to Vorbis ({encDetail})");
                Verify(enc, encChannels, meta.Frequency, meta.SampleCount, notes);
                return enc;
            default:
                throw new NotSupportedException($"FSB codec {fsb.Header.AudioType} is not supported");
        }
    }

    /// <summary>
    /// Decode the whole Ogg once (NVorbis) and check channels, rate, length (within one Vorbis block or 5%) and that the
    /// samples are finite and not all silence-or-clipping, so a misread subsound never reaches the pack.
    /// </summary>
    public static void Verify(byte[] ogg, int channels, int rate, long frames, List<string> notes)
    {
        int ch, sr; long got = 0; double peak = 0; int nonFinite = 0;
        try
        {
            using var r = new NVorbis.VorbisReader(new MemoryStream(ogg), true);
            ch = r.Channels; sr = r.SampleRate;
            var buf = new float[8192];
            int n;
            while ((n = r.ReadSamples(buf, 0, buf.Length)) > 0)
            {
                got += n;
                for (int i = 0; i < n; i++)
                {
                    var v = buf[i];
                    if (!float.IsFinite(v)) nonFinite++;
                    else if (Math.Abs(v) > peak) peak = Math.Abs(v);
                }
            }
        }
        catch (Exception e)
        {
            throw new InvalidDataException($"rebuilt Ogg does not decode: {e.GetType().Name}: {e.Message}", e);
        }
        if (ch != channels || sr != rate) throw new InvalidDataException($"rebuilt Ogg is {ch}ch {sr}Hz, expected {channels}ch {rate}Hz");
        long decodedFrames = got / Math.Max(1, ch);
        long tolerance = Math.Max(4096, frames / 20);
        if (Math.Abs(decodedFrames - frames) > tolerance)
            throw new InvalidDataException($"rebuilt Ogg decodes to {decodedFrames} frames, expected {frames}");
        if (nonFinite > 0) throw new InvalidDataException($"{nonFinite} non-finite samples");
        if (peak < 1e-6) notes.Add("warning: the sound is silent");
        if (peak > 4) throw new InvalidDataException($"peak {peak:0.##} is far beyond full scale: not this subsound's data");
        notes.Add(string.Create(CultureInfo.InvariantCulture, $"decode check {decodedFrames} frames, peak {peak:0.###}"));
    }

    /// <summary>Interleaved PCM → per-channel float samples.</summary>
    public static float[][] PcmFloat(byte[] data, FmodAudioType type, int channels, int frames)
    {
        int width = type switch { FmodAudioType.PCM8 => 1, FmodAudioType.PCM16 => 2, FmodAudioType.PCMFLOAT => 4, _ => throw new NotSupportedException(type.ToString()) };
        channels = Math.Max(1, channels);
        frames = Math.Min(frames, data.Length / (width * channels));
        var o = new float[channels][];
        for (int c = 0; c < channels; c++) o[c] = new float[frames];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < channels; c++)
            {
                int i = (f * channels + c) * width;
                o[c][f] = type switch
                {
                    FmodAudioType.PCM8 => unchecked((sbyte)data[i]) / 128f, // FMOD PCM8 is signed
                    FmodAudioType.PCM16 => BitConverter.ToInt16(data, i) / 32768f,
                    _ => BitConverter.ToSingle(data, i),
                };
            }
        return o;
    }
}
