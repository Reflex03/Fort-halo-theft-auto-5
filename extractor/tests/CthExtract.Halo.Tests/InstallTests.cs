using Xunit;

namespace CthExtract.Halo.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cth-halo-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string Sub(params string[] parts)
    {
        var p = System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());
        Directory.CreateDirectory(p);
        return p;
    }
    public string File(string rel, byte[]? content = null)
    {
        var p = System.IO.Path.Combine(Path, rel.Replace('\\', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllBytes(p, content ?? new byte[] { 1, 2, 3 });
        return p;
    }
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class InstallTests
{
    private static string MakeMcc(TempDir t, string root)
    {
        t.File($@"{root}\halo3\maps\riverworld.map");
        t.File($@"{root}\halo3\maps\shared.map");
        t.File($@"{root}\halo3\fmod\pc\sfx.fsb");
        t.File($@"{root}\halo3\fmod\pc\sfx.fsb.info");
        t.File($@"{root}\mcc\binaries\win64\MCC-Win64-Shipping.exe");
        return System.IO.Path.Combine(t.Path, root.Replace('\\', System.IO.Path.DirectorySeparatorChar));
    }

    [Theory]
    [InlineData("")]                               // MCC root
    [InlineData("halo3")]                          // halo3 folder
    [InlineData(@"halo3\maps")]                    // maps folder
    [InlineData(@"halo3\fmod\pc")]                 // fmod folder
    [InlineData(@"mcc\binaries\win64")]            // exe folder
    public void SteamLayoutFromAnySubfolder(string sub)
    {
        using var t = new TempDir();
        var root = MakeMcc(t, @"steamapps\common\Halo The Master Chief Collection");
        var given = sub == "" ? root : System.IO.Path.Combine(root, sub.Replace('\\', System.IO.Path.DirectorySeparatorChar));
        var log = new List<string>();
        var i = HaloInstall.Locate(given, log.Add);
        Assert.NotNull(i);
        Assert.Equal(root, i!.Root);
        Assert.NotNull(i.FmodDir);
        Assert.NotEmpty(log);
    }

    [Fact]
    public void SteamLibraryRootAndXboxAppLayouts()
    {
        using var t = new TempDir();
        var steam = MakeMcc(t, @"lib\steamapps\common\Halo The Master Chief Collection");
        Assert.Equal(steam, HaloInstall.Locate(System.IO.Path.Combine(t.Path, "lib"))!.Root);

        var xbox = MakeMcc(t, @"XboxGames\Halo- The Master Chief Collection\Content");
        Assert.Equal(xbox, HaloInstall.Locate(System.IO.Path.Combine(t.Path, "XboxGames", "Halo- The Master Chief Collection"))!.Root);
        Assert.Equal(xbox, HaloInstall.Locate(System.IO.Path.Combine(t.Path, "XboxGames"))!.Root);
    }

    [Fact]
    public void FilePathAndCaseInsensitiveNames()
    {
        using var t = new TempDir();
        t.File(@"MCC\HALO3\Maps\RiverWorld.MAP");
        t.File(@"MCC\HALO3\FMOD\PC\SFX.FSB");
        t.File(@"MCC\HALO3\FMOD\PC\SFX.FSB.INFO");
        var i = HaloInstall.Locate(System.IO.Path.Combine(t.Path, "MCC", "HALO3", "Maps", "RiverWorld.MAP"))!;
        Assert.NotNull(i);
        Assert.NotNull(i.MapFile("riverworld"));
        Assert.Single(i.SoundBanks());
        Assert.Equal(3, i.FingerprintFiles().Count()); // riverworld + sfx.fsb + sfx.fsb.info (no shared.map here)
    }

    [Fact]
    public void NothingThereGivesNull()
    {
        using var t = new TempDir();
        t.File(@"halo3\readme.txt");
        t.Sub("halo3", "maps"); // empty maps folder
        var log = new List<string>();
        Assert.Null(HaloInstall.Locate(t.Path, log.Add));
        Assert.Contains(log, l => l.Contains("no .map files"));
        Assert.Null(HaloInstall.Locate(System.IO.Path.Combine(t.Path, "does-not-exist")));
        Assert.Null(HaloInstall.Locate(""));
        Assert.Null(HaloInstall.Locate(null));
    }

    [Fact]
    public void MapSearchOrderStartsWithValhallaAndSkipsResourceCaches()
    {
        using var t = new TempDir();
        foreach (var m in new[] { "zanzibar", "shared", "campaign", "riverworld", "010_jungle", "guardian", "custom_thing", "mainmenu" })
            t.File($@"halo3\maps\{m}.map");
        var order = HaloInstall.Locate(t.Path)!.MapSearchOrder().Select(System.IO.Path.GetFileNameWithoutExtension).ToList();
        Assert.Equal(new[] { "riverworld", "guardian", "zanzibar", "010_jungle", "custom_thing" }, order);
    }

    [Fact]
    public void BanksNeedTheirInfoFile()
    {
        using var t = new TempDir();
        t.File(@"halo3\maps\riverworld.map");
        t.File(@"halo3\fmod\pc\sfx.fsb");
        t.File(@"halo3\fmod\pc\english.fsb");
        t.File(@"halo3\fmod\pc\english.fsb.info");
        var banks = HaloInstall.Locate(t.Path)!.SoundBanks();
        Assert.Equal("english.fsb", System.IO.Path.GetFileName(Assert.Single(banks).Fsb));
    }
}
