using System.IO.Hashing;
using System.Text.Json.Nodes;
using CthExtract.Core;
using CthExtract.Core.Generated;
using Fmod5Sharp;
using Fmod5Sharp.CodecRebuilders;
using Xunit;

namespace CthExtract.Halo.Tests;

public class VorbisSetupTests
{
    private static Dictionary<uint, (byte[] Header, int SeekBit)> Table(Stream json) =>
        JsonNode.Parse(json)!.AsObject().ToDictionary(kv => uint.Parse(kv.Key),
            kv => (Convert.FromBase64String(kv.Value!["headerBytes"]!.GetValue<string>()), kv.Value["seekBit"]!.GetValue<int>()));

    [Fact]
    public void ShippedTableIsLibvorbisOutputWithConsistentCrcsAndSeekBits()
    {
        using var s = typeof(FsbLoader).Assembly.GetManifestResourceStream("Fmod5Sharp.Util.vorbis_headers_converted.json")!;
        var table = Table(s);
        Assert.Equal(247, table.Count);
        Assert.True(FmodVorbisRebuilder.SetupHeaderCount >= table.Count); // other tests may register extra headers at run time
        Assert.All(table.Keys, k => Assert.True(FmodVorbisRebuilder.HasSetupHeader(k)));
        foreach (var (crc, (header, seek)) in table)
        {
            Assert.Equal(crc, Crc32.HashToUInt32(header));
            Assert.Equal(crc, VorbisSetup.Crc32(header));
            Assert.Equal(seek, VorbisSetup.FindModeSeekBit(header));
        }
    }

    [Fact]
    public void SeekBitFinderAgreesWithUpstreamTableIncludingFmodOnlyHeaders()
    {
        // upstream Fmod5Sharp table (dumped from an FMOD binary by python-fsb5): reference only, never shipped
        // CTH_REFS = folder with clones of the upstream repos (Reclaimer, Fmod5Sharp); reference only, never vendored
        var refs = Environment.GetEnvironmentVariable("CTH_REFS") ?? "";
        var path = Path.Combine(refs, "Fmod5Sharp", "Fmod5Sharp", "Util", "vorbis_headers_converted.json");
        Assert.SkipWhen(refs.Length == 0 || !File.Exists(path), "set CTH_REFS to a folder with an upstream Fmod5Sharp clone");
        using var f = File.OpenRead(path);
        var upstream = Table(f);
        Assert.Equal(161, upstream.Count);
        foreach (var (crc, (header, seek)) in upstream)
            Assert.Equal(seek, VorbisSetup.FindModeSeekBit(header));
        // every libvorbis-reproducible entry of the upstream table is byte-identical in ours
        int same = upstream.Count(kv => FmodVorbisRebuilder.HasSetupHeader(kv.Key));
        Assert.Equal(107, same);
    }

    [Fact]
    public void FindModeSeekBitRejectsNonHeaders()
    {
        Assert.Equal(-1, VorbisSetup.FindModeSeekBit(new byte[100]));
        Assert.Equal(-1, VorbisSetup.FindModeSeekBit("\u0005vorbis"u8.ToArray().Concat(new byte[50]).ToArray()));
    }

    [Fact]
    public void ScanFindsHeadersInsideBinariesAcrossChunkBoundaries()
    {
        var e = SynthAudio.Encode(2, 48000, 0.3f, 0.05);
        var e2 = SynthAudio.Encode(1, 32000, 0.6f, 0.05);
        using var t = new TempDir();
        var rnd = new Random(7);
        var blob = new byte[(16 << 20) + 50_000];
        rnd.NextBytes(blob);
        int at = (16 << 20) - 1500; // straddles the 16 MiB read boundary
        e.Setup.CopyTo(blob, at);
        e2.Setup.CopyTo(blob, 1000);
        "\u0005vorbis"u8.ToArray().CopyTo(blob, 5000); // a decoy magic with no matching header
        var bin = t.File("mcc/binaries/win64/fmodstudio.dll", blob);
        var found = VorbisSetup.Scan(new[] { bin }, new HashSet<uint> { e.SetupCrc, e2.SetupCrc, 0x12345678 });
        Assert.Equal(2, found.Count);
        Assert.Equal(e.Setup, found[e.SetupCrc].Header);
        Assert.Equal(VorbisSetup.FindModeSeekBit(e.Setup), found[e.SetupCrc].SeekBit);
        Assert.Equal(e2.Setup, found[e2.SetupCrc].Header);
    }

    [Fact]
    public void BankNeedingAnUntabledHeaderGetsItFromTheInstall()
    {
        // a setup our table does not have (libvorbis interpolates some setup values between quality steps, so an odd
        // quality gives a header that the 0.01-step table lacks); the "FMOD binary" of the fake install has it
        SynthAudio.Encoded? odd = null;
        // (OggVorbisEncoder's mono 44.1/48 kHz setups at q 0.2-0.4 differ from libvorbis' and do not decode, so they
        // are not usable here; genuine libvorbis files at those settings decode fine)
        foreach (var (ch, rate, q) in new[] { (2, 44100, 0.4573f), (2, 32000, 0.777f), (2, 48000, 0.1234f), (1, 44100, 0.0555f), (1, 32000, 0.0555f), (2, 44100, 0.0333f) })
        {
            SynthAudio.Encoded e;
            try { e = SynthAudio.Encode(ch, rate, q, 0.2); SynthAudio.Decode(e.ReferenceOgg, out _, out _); }
            catch { continue; }
            if (!FmodVorbisRebuilder.HasSetupHeader(e.SetupCrc)) { odd = e; break; }
        }
        Assert.SkipWhen(odd is null, "every candidate setup is already in the table");
        var six = odd!;

        using var t = new TempDir();
        var halo = new SyntheticHalo();
        var (items, info) = halo.Bank(six);
        t.File(@"mcc\halo3\maps\riverworld.map", new byte[16]);
        t.File(@"mcc\halo3\fmod\pc\sfx.fsb", SynthAudio.BuildFsb5(items));
        t.File(@"mcc\halo3\fmod\pc\sfx.fsb.info", FsbInfo.Write(info));
        var padding = new byte[300_000];
        new Random(1).NextBytes(padding);
        t.File(@"mcc\mcc\binaries\win64\fmod.dll", padding.Take(1234).Concat(six.Setup).Concat(padding).ToArray());

        using var log = new Log(Environment.GetEnvironmentVariable("CTH_TEST_LOG_DIR") is { Length: > 0 } d ? Path.Combine(d, "setup-fallback.log") : null);
        var ctx = new ExtractContext("halo-mcc", "halo", Path.Combine(t.Path, "pack"), Path.Combine(t.Path, "stats"), Path.Combine(t.Path, "work"), log);
        new HaloModule(p => Path.GetFileName(p) == "riverworld.map" ? halo.Map : throw new InvalidDataException("x")).Extract(Path.Combine(t.Path, "mcc"), ctx, CancellationToken.None);
        var res = ctx.Results;
        Assert.True(res["halo_snd_magnum_fire"].Status == AssetStatus.Ok, res["halo_snd_magnum_fire"].Detail);
        Assert.Contains($"{six.Channels}ch {six.Rate}Hz", res["halo_snd_magnum_fire"].Detail);
        Assert.True(FmodVorbisRebuilder.HasSetupHeader(six.SetupCrc));
    }
}
