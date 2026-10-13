using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace CthExtract.Cli.Tests;

public class ArgsTests
{
    [Fact]
    public void Contract_arguments_parse()
    {
        var o = CliOptions.Parse(new[] { "--halo", "C:/Games/MCC", "--gta", "D:/GTA V", "--fortnite", "E:/Epic/Fortnite", "--out", "C:/cache" });
        Assert.Null(o.Error);
        Assert.Equal("C:/Games/MCC", o.Dirs["halo"]);
        Assert.Equal("D:/GTA V", o.Dirs["gta"]);
        Assert.Equal("E:/Epic/Fortnite", o.Dirs["fortnite"]);
        Assert.Equal("C:/cache", o.Out);
        Assert.Equal(new[] { "halo", "gta", "fortnite" }, o.SelectedGames());
        Assert.Equal(CliOptions.DefaultTimeoutSec, o.TimeoutSec);
    }

    /// <summary>--offline means no network for every module: GTA must not fetch the NG key blob.</summary>
    [Fact]
    public void Offline_gta_module_never_downloads_the_key_blob()
    {
        var cache = Path.Combine(Path.GetTempPath(), "cth-offline-" + Guid.NewGuid().ToString("N")[..8]);
        var offline = (CthExtract.Gta.GtaModule)ModuleFactory.Spec("gta", cache, offline: true).Create();
        Assert.IsType<CthExtract.Gta.Crypto.BytesMagicSource>(offline.Options.Magic);
        Assert.Null(offline.Options.Magic.Fetch(new CthExtract.Core.Log(null), CancellationToken.None, out var why));
        Assert.NotNull(why);
        var online = (CthExtract.Gta.GtaModule)ModuleFactory.Spec("gta", cache, offline: false).Create();
        Assert.IsType<CthExtract.Gta.Crypto.PinnedHttpMagicSource>(online.Options.Magic);
        Assert.Equal(Path.Combine(cache, "keys"), offline.Options.KeysDir);
    }

    [Fact]
    public void Equals_form_only_lists_and_flags()
    {
        var o = CliOptions.Parse(new[] { "--out=x", "--only=fortnite,halo", "--only", "gta-v", "--timeout-sec=60", "--game-timeout-sec", "30",
                                         "--grace-sec=0", "--lock-wait-sec", "5", "--in-process", "--no-keep", "--offline", "--console" });
        Assert.Null(o.Error);
        Assert.Equal(new[] { "halo", "gta", "fortnite" }, o.SelectedGames()); // canonical order
        Assert.Equal((60, 30, 0, 5), (o.TimeoutSec, o.GameTimeoutSec, o.GraceSec, o.LockWaitSec));
        Assert.True(o.InProcess && o.NoKeep && o.Offline && o.Console);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("stray")]
    [InlineData("--halo")]
    [InlineData("--only", "doom")]
    [InlineData("--only", ",")]
    [InlineData("--timeout-sec", "0")]
    [InlineData("--timeout-sec", "ten")]
    [InlineData("--check=yes")]
    [InlineData("--config", "/definitely/not/here.properties")]
    public void Bad_arguments_are_errors(params string[] argv)
    {
        var o = CliOptions.Parse(argv);
        Assert.NotNull(o.Error);
    }

    [Fact]
    public void Placeholders_and_empty_values_count_as_not_given()
    {
        var o = CliOptions.Parse(new[] { "--halo", "{game:halo-mcc}", "--gta", "  ", "--fortnite", "\"C:/Epic Games/Fortnite\"" });
        Assert.Null(o.Error);
        Assert.Null(o.Dirs["halo"]);
        Assert.Null(o.Dirs["gta"]);
        Assert.Equal("C:/Epic Games/Fortnite", o.Dirs["fortnite"]);
        Assert.Contains(o.Notes, n => n.Contains("unexpanded placeholder"));
    }

    [Fact]
    public void Config_reads_utf8_properties_and_command_line_wins()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cth-cli-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        try
        {
            var p = Path.Combine(dir, "crafttheft.properties");
            // as java.util.Properties.store writes it (escaped ':' and '\'), plus a non-ASCII user name, BOM and continuation
            File.WriteAllText(p,
                "#Craft Theft Halo\n! comment\n" +
                "halo_dir=C\\:\\\\Users\\\\Jos\u00e9\\\\MCC\n" +
                "gta_dir = D:/Rockstar Games/Grand Theft \\\n    Auto V\n" +
                "fortnite_dir:E:/Epic/Fortnite\n" +
                "cache_dir=C:/Users/Jos\\u00e9/AppData/Local/CraftTheftHalo/cache\n" +
                "extractor=C:/x/cthextract.exe\n", new UTF8Encoding(true));
            var o = CliOptions.Parse(new[] { "--config", p, "--fortnite", "F:/Fortnite" });
            Assert.Null(o.Error);
            Assert.Equal("C:\\Users\\Jos\u00e9\\MCC", o.Dirs["halo"]);
            Assert.Equal("D:/Rockstar Games/Grand Theft Auto V", o.Dirs["gta"]);
            Assert.Equal("F:/Fortnite", o.Dirs["fortnite"]);
            Assert.Equal(DirSource.Argument, o.DirSources["fortnite"]);
            Assert.Equal(DirSource.Config, o.DirSources["halo"]);
            Assert.Equal("C:/Users/Jos\u00e9/AppData/Local/CraftTheftHalo/cache", o.Out);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Properties_parser_follows_java_rules()
    {
        var p = JavaProperties.Parse("a=1\n  b : 2\nc 3\nd\\=e=4\nf=line\\nbreak\ng=\\u0041\\t\nh=trailing\\\\\n#x=9\nempty=\n");
        Assert.Equal("1", p["a"]);
        Assert.Equal("2", p["b"]);
        Assert.Equal("3", p["c"]);
        Assert.Equal("4", p["d=e"]);
        Assert.Equal("line\nbreak", p["f"]);
        Assert.Equal("A\t", p["g"]);
        Assert.Equal("trailing\\", p["h"]);
        Assert.Equal("", p["empty"]);
        Assert.False(p.ContainsKey("#x"));
    }

    [Fact]
    public void Version_and_help_exit_0()
    {
        var so = new StringWriter();
        Assert.Equal(0, Program.Cli(new[] { "--version" }, so, new StringWriter()));
        Assert.StartsWith("cthextract 0.1.0", so.ToString());
        Assert.Contains("cacheSchema 1", so.ToString());
        so = new StringWriter();
        Assert.Equal(0, Program.Cli(new[] { "--help" }, so, new StringWriter()));
        Assert.Contains("--only", so.ToString());
    }

    [Fact]
    public void Bad_arguments_exit_3_and_unblock_melty_only_when_no_manifest_exists()
    {
        using var sb = new Sandbox();
        var se = new StringWriter();
        Assert.Equal(3, Program.Cli(new[] { "--out", sb.Cache, "--bogus" }, new StringWriter(), se));
        Assert.Contains("unknown option --bogus", se.ToString());
        var m = sb.Manifest();
        Assert.StartsWith("{\"cacheSchema\":1,", sb.ManifestText());
        Assert.Equal("failed", (string?)m["status"]);
        Assert.Contains("bad arguments", (string?)m["games"]!["halo"]!["error"]);

        // an existing manifest is never replaced by a bad-arguments one
        Assert.Equal(0, InProc.Run(sb.AllArgs()).Exit);
        var good = sb.ManifestText();
        Assert.Equal(3, Program.Cli(new[] { "--out", sb.Cache, "--only", "doom" }, new StringWriter(), new StringWriter()));
        Assert.Equal(good, sb.ManifestText());
    }
}
