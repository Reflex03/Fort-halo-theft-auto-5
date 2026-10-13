using System.Security.Cryptography;
using System.Text;
using CthExtract.Core;
using CthExtract.Gta.Crypto;
using CthExtract.Gta.Resources;

namespace CthExtract.Gta.Tests.Synth;

/// <summary>Builds a complete synthetic GTA V folder (store-root layout) from our own writers and random test keys.</summary>
public sealed class SynthGame : IDisposable
{
    public sealed class Options
    {
        public bool PlantAesKey { get; init; } = true;
        public bool IncludeAwc { get; init; } = true;
        public bool EncryptAwcChunks { get; init; }
        public bool IncludeStar { get; init; } = true;
        public bool IncludeMeta { get; init; } = true;
        public int PoliceYftVersion { get; init; } = 162;
        public RpfEnc BaseEnc { get; init; } = RpfEnc.Ng;
        public RpfEnc DlcEnc { get; init; } = RpfEnc.Ng;
        public RpfEnc AudioEnc { get; init; } = RpfEnc.Ng;
        public RpfEnc TxdEnc { get; init; } = RpfEnc.Ng;
        /// <summary>common.rpf and update/update.rpf (AES in the game); OPEN lets a run without any key read them.</summary>
        public RpfEnc CommonEnc { get; init; } = RpfEnc.Aes;
        public RpfEnc UpdateEnc { get; init; } = RpfEnc.Aes;
        public bool SubfolderLayout { get; init; } = true;
        public bool CorruptAwc { get; init; }
    }

    public string Root { get; }
    public string GameDir { get; }
    public SynthKeys Keys { get; }
    public byte[] Magic { get; }
    public static readonly short[] SirenPcm = AwcWriter.Sweep(32000, 1.5, 700, 1500);
    public static readonly short[] HornPcm = AwcWriter.Sweep(24000, 0.6, 440, 460, 0.5);

    public SynthGame(Options? o = null, int seed = 1234)
    {
        o ??= new Options();
        Root = Path.Combine(Path.GetTempPath(), "cth-gta-test-" + Guid.NewGuid().ToString("N")[..10]);
        GameDir = o.SubfolderLayout ? Path.Combine(Root, "Library", "Grand Theft Auto V") : Root;
        Directory.CreateDirectory(GameDir);
        Keys = new SynthKeys(seed);
        Magic = Keys.MagicBlob();

        // exe with the AES key planted 8-aligned in .rdata
        var exe = FakeExe.Build(seed);
        if (o.PlantAesKey) Keys.Aes.CopyTo(exe.Bytes, exe.RdataOffset + 0x1238);
        File.WriteAllBytes(Path.Combine(GameDir, "GTA5.exe"), exe.Bytes);

        // x64e.rpf -> levels/gta5/vehicles.rpf
        var veh = new RpfWriter(o.BaseEnc, Keys)
            .AddResource("infernus.yft", SynthCar.Yft("infernus"))
            .AddResource("infernus.ytd", SynthCar.ModelYtd("infernus"))
            .AddResource("police.yft", SynthCar.Yft("police", o.PoliceYftVersion))
            .AddResource("police.ytd", SynthCar.ModelYtd("police"))
            .AddResource("vehshare.ytd", SynthCar.VehshareYtd())
            .AddResource("vehicles_synth_interior.ytd", SynthCar.InteriorYtd(), hugeSizeMarker: true);
        Write("x64e.rpf", new RpfWriter(o.BaseEnc, Keys).AddNested("levels/gta5/vehicles.rpf", veh).AddBinary("readme.txt", "x64e"u8.ToArray()));

        // common.rpf: vehicles.meta (encrypted + compressed binary)
        var common = new RpfWriter(o.CommonEnc, Keys);
        if (o.IncludeMeta) common.AddBinary("data/levels/gta5/vehicles.meta", Encoding.UTF8.GetBytes(SynthCar.VehiclesMeta("infernus", "police", "sultan")), compress: true, encrypt: true);
        else common.AddBinary("data/other.txt", "x"u8.ToArray());
        Write("common.rpf", common);

        // update/update.rpf (AES) -> x64/textures/script_txds.rpf (NG) -> commonmenu.ytd
        var update = new RpfWriter(o.UpdateEnc, Keys).AddBinary("common/data/dlclist.xml", "<SMandatoryPacksData/>"u8.ToArray(), compress: true);
        if (o.IncludeStar)
        {
            var txds = new RpfWriter(o.TxdEnc, Keys).AddResource("commonmenu.ytd", TextureWriter.Ytd(new[]
            {
                new SynthTexture("shop_box_tick", TextureWriter.Solid(16, 16, 255, 255, 255), TextureFormats.DXT1),
                new SynthTexture("shop_new_star", Star(32), TextureFormats.DXT5),
            }));
            update.AddNested("x64/textures/script_txds.rpf", txds);
        }
        Write("update/update.rpf", update);

        // a patch DLC with sultan in an OPEN nested vehicles.rpf
        var dlcVeh = new RpfWriter(RpfEnc.Open, Keys).AddResource("sultan.yft", SynthCar.Yft("sultan")).AddResource("sultan.ytd", SynthCar.ModelYtd("sultan"));
        Write("update/x64/dlcpacks/patchday9ng/dlc.rpf", new RpfWriter(o.DlcEnc, Keys).AddNested("x64/levels/gta5/vehicles.rpf", dlcVeh));

        // x64/audio/sfx/RESIDENT.rpf -> vehicles.awc
        if (o.IncludeAwc)
        {
            var awc = AwcWriter.SfxPack(new[]
            {
                new AwcWriter.Wave("engine_idle_01", AwcWriter.Sweep(16000, 0.3, 100, 120), 16000, Codec: 4),
                new AwcWriter.Wave("siren_pa20a_wail", SirenPcm, 32000, Codec: 4, LoopPoint: 0),
                new AwcWriter.Wave("horn_std_1", HornPcm, 24000, Codec: 4),
                new AwcWriter.Wave("door_close", AwcWriter.Sweep(22050, 0.2, 200, 100), 22050, Codec: 0),
            }, o.EncryptAwcChunks ? Keys.Ng.AwcKey : null, chunkIndices: true);
            if (o.CorruptAwc) BitConverter.GetBytes(0x7FFFFFFF).CopyTo(awc, 8);
            Write("x64/audio/sfx/RESIDENT.rpf", new RpfWriter(o.AudioEnc, Keys).AddBinary("vehicles.awc", awc, compress: true, encrypt: true));
        }

        // an unrelated OPEN archive
        Write("x64a.rpf", new RpfWriter(RpfEnc.Open, Keys).AddBinary("data/x.txt", "x"u8.ToArray()));
    }

    public static Rgba Star(int size)
    {
        var px = new byte[size * size * 4];
        var c = (size - 1) / 2f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double a = Math.Atan2(y - c, x - c), r = Math.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                double edge = c * (0.55 + 0.45 * Math.Cos(5 * a)) ;
                bool inside = r <= edge;
                int i = (y * size + x) * 4;
                px[i] = 255; px[i + 1] = 255; px[i + 2] = 255; px[i + 3] = (byte)(inside ? 255 : 0);
            }
        return new Rgba(size, size, px);
    }

    private void Write(string rel, RpfWriter w)
    {
        var path = Path.Combine(GameDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, w.Build(Path.GetFileName(rel)));
    }

    public GtaOptions ModuleOptions(string keysDir, byte[]? magic = null, string? sha = "auto") => new()
    {
        Hashes = Keys.Hashes,
        Magic = new BytesMagicSource(magic ?? Magic, "synthetic magic"),
        MagicSha256 = sha == "auto" ? Convert.ToHexStringLower(SHA256.HashData(magic ?? Magic)) : sha,
        KeysDir = keysDir,
    };

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { }
    }
}

/// <summary>An ExtractContext in a temp cache folder.</summary>
public sealed class TempCache : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cth-gta-cache-" + Guid.NewGuid().ToString("N")[..10]);
    public Log Log { get; }
    public ExtractContext Ctx { get; }
    public string KeysDir => Path.Combine(Dir, "keys");
    public string LogPath => Path.Combine(Dir, "extract.log");

    public TempCache()
    {
        Directory.CreateDirectory(Dir);
        Log = new Log(LogPath);
        Ctx = new ExtractContext("gta-v", "gta", Path.Combine(Dir, "staging", "gta"), Path.Combine(Dir, "stats"), Path.Combine(Dir, "work", "gta"), Log);
    }

    public string Asset(string rel) => Path.Combine(Ctx.AssetsDir, rel.Replace('/', Path.DirectorySeparatorChar));

    public void Dispose()
    {
        Log.Dispose();
        try { Directory.Delete(Dir, true); } catch { }
    }
}
