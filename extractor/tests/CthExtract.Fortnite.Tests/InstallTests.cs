using Xunit;

namespace CthExtract.Fortnite.Tests;

public class InstallTests
{
    [Theory]
    [InlineData("++Fortnite+Release-42.30-CL-58557680-Windows", "42.30", "++Fortnite+Release-42.30-CL-58557680", 58557680L)]
    [InlineData("++Fortnite+Release-42.30-CL-58557680", "42.30", "++Fortnite+Release-42.30-CL-58557680", 58557680L)]
    [InlineData("++Fortnite+Release-9.10-CL-10000000-Windows", "9.10", "++Fortnite+Release-9.10-CL-10000000", 10000000L)]
    [InlineData("++Fortnite+Release-40.00-CL-51746096-Windows", "40.00", "++Fortnite+Release-40.00-CL-51746096", 51746096L)]
    public void Parses_epic_version_strings(string raw, string mm, string build, long cl)
    {
        var v = FortniteVersion.Parse(raw);
        Assert.Equal(mm, v.MajorMinor);
        Assert.Equal(build, v.BuildString);
        Assert.Equal(cl, v.Changelist);
    }

    [Fact]
    public void Unknown_versions_parse_to_nulls()
    {
        Assert.Equal(FortniteVersion.Unknown, FortniteVersion.Parse(null));
        var v = FortniteVersion.Parse("something else");
        Assert.Null(v.MajorMinor);
        Assert.Null(v.BuildString);
    }

    private static string MakeInstall(TempDir t, string name = "Fortnite")
    {
        var root = t.Sub("Epic Games", name);
        Directory.CreateDirectory(Path.Combine(root, "FortniteGame", "Content", "Paks"));
        Directory.CreateDirectory(Path.Combine(root, "FortniteGame", "Binaries", "Win64"));
        File.WriteAllBytes(Path.Combine(root, "FortniteGame", "Content", "Paks", "global.utoc"), new byte[16]);
        return root;
    }

    private static string Manifest(TempDir t, string installLocation, string version, string appName = "Fortnite", string file = "ABC123.item")
    {
        var dir = t.Sub("Manifests");
        File.WriteAllText(Path.Combine(dir, file), $$"""
            {"FormatVersion":0,"AppName":"{{appName}}","DisplayName":"Fortnite","InstallLocation":{{System.Text.Json.JsonSerializer.Serialize(installLocation)}},
             "LaunchExecutable":"FortniteGame/Binaries/Win64/FortniteLauncher.exe","AppVersionString":"{{version}}","MainGameAppName":"Fortnite"}
            """);
        return dir;
    }

    [Fact]
    public void Normalises_any_folder_the_player_may_pick()
    {
        using var t = new TempDir();
        var root = MakeInstall(t);
        var tried = new List<string>();
        Assert.Equal(root, FortniteInstall.Normalise(root, tried));
        Assert.Equal(root, FortniteInstall.Normalise(Path.Combine(root, "FortniteGame"), tried));
        Assert.Equal(root, FortniteInstall.Normalise(Path.Combine(root, "FortniteGame", "Content", "Paks"), tried));
        Assert.Equal(root, FortniteInstall.Normalise(Path.Combine(root, "FortniteGame", "Binaries", "Win64"), tried));
        Assert.Equal(root, FortniteInstall.Normalise(Path.Combine(t.Path, "Epic Games"), tried)); // library folder
        Assert.Equal(root, FortniteInstall.Normalise(Path.Combine(t.Path), tried)); // two levels above
        Assert.Equal(root, FortniteInstall.Normalise(Path.Combine(root, "FortniteGame", "Content", "Paks", "global.utoc"), tried)); // a file
        Assert.Null(FortniteInstall.Normalise(Path.Combine(t.Path, "missing"), tried));
        Assert.Contains(tried, s => s.Contains("does not exist"));
        Assert.Null(FortniteInstall.Normalise("", tried));
    }

    [Fact]
    public void Version_comes_from_the_epic_manifest_of_that_folder()
    {
        using var t = new TempDir();
        var root = MakeInstall(t);
        var manifests = Manifest(t, root, "++Fortnite+Release-42.30-CL-58557680-Windows");
        var i = FortniteInstall.Find(Path.Combine(root, "FortniteGame"), manifests, out _);
        Assert.NotNull(i);
        Assert.Equal(root, i!.Root);
        Assert.Equal("42.30", i.Version.MajorMinor);
        Assert.Contains("ABC123.item", i.VersionSource);
        Assert.Equal(Path.Combine(root, "FortniteGame", "Content", "Paks"), i.PaksDir);
    }

    [Fact]
    public void Without_a_folder_the_manifest_install_location_is_used()
    {
        using var t = new TempDir();
        var root = MakeInstall(t);
        var manifests = Manifest(t, root + Path.DirectorySeparatorChar, "++Fortnite+Release-41.10-CL-1-Windows");
        // another game's manifest is ignored
        File.WriteAllText(Path.Combine(manifests, "other.item"), """{"AppName":"Sugar","DisplayName":"Rocket League","InstallLocation":"C:/nope","AppVersionString":"1"}""");
        File.WriteAllText(Path.Combine(manifests, "broken.item"), "{not json");
        var i = FortniteInstall.Find(null, manifests, out var tried);
        Assert.NotNull(i);
        Assert.Equal(root, i!.Root);
        Assert.Equal("41.10", i.Version.MajorMinor);
        Assert.Contains("unreadable manifest broken.item", tried);
    }

    [Fact]
    public void Folder_without_manifest_has_unknown_version_and_missing_install_is_reported()
    {
        using var t = new TempDir();
        var root = MakeInstall(t);
        var i = FortniteInstall.Find(root, Path.Combine(t.Path, "no-manifests"), out _);
        Assert.NotNull(i);
        Assert.Null(i!.Version.MajorMinor);
        Assert.Null(FortniteInstall.Find(Path.Combine(t.Path, "nothing-here"), Path.Combine(t.Path, "no-manifests"), out var why));
        Assert.Contains("does not exist", why);
        Assert.Contains("no Fortnite entry", why);
    }

    [Fact]
    public void Fingerprint_files_are_relative_to_the_given_folder()
    {
        using var t = new TempDir();
        var root = MakeInstall(t);
        File.WriteAllBytes(Path.Combine(root, "FortniteGame", "Binaries", "Win64", "FortniteClient-Win64-Shipping.exe"), new byte[4]);
        var m = new FortniteModule();
        var files = m.FingerprintFiles(root).ToList();
        Assert.Contains("FortniteGame/Content/Paks/global.utoc", files);
        Assert.Contains("FortniteGame/Binaries/Win64/FortniteClient-Win64-Shipping.exe", files);
        Assert.Empty(m.FingerprintFiles(Path.Combine(t.Path, "missing")));
    }
}
