using CthExtract.Core;
using CthExtract.Fortnite;
using CthExtract.Gta;
using CthExtract.Gta.Crypto;
using CthExtract.Halo;

namespace CthExtract;

/// <summary>One game the extractor can run: its short name, Melty slug and how to make its module.</summary>
internal sealed record GameSpec(string Short, string Slug, Func<IGameModule> Create);

/// <summary>Builds the game modules with cache-relative options (key caches live in cache/keys, outside the staging folders).</summary>
internal static class ModuleFactory
{
    /// <summary>Test hook for end-to-end runs of the real executable: "halo=ok,gta=throw,..." swaps in
    /// <see cref="FakeModule"/>s. Ignored unless set; logged loudly when used.</summary>
    public const string TestModulesEnv = "CTHEXTRACT_TEST_MODULES";

    public static IReadOnlyList<GameSpec> Specs(string cacheDir, bool offline)
    {
        var layout = new CacheLayout(cacheDir);
        var fakes = FakeBehaviours();
        var list = new List<GameSpec>();
        foreach (var g in AppInfo.Games)
        {
            var slug = Manifest.GameSlug(g)!;
            if (fakes.TryGetValue(g, out var behaviour))
            {
                list.Add(new GameSpec(g, slug, () => new FakeModule(slug, g, behaviour)));
                continue;
            }
            list.Add(g switch
            {
                "halo" => new GameSpec(g, slug, () => new HaloModule()),
                "gta" => new GameSpec(g, slug, () => new GtaModule(new GtaOptions
                {
                    KeysDir = layout.KeysDir,
                    // --offline means no network at all: the NG key blob is not fetched (NG archives report Missing)
                    Magic = offline ? new BytesMagicSource(null, "nothing (--offline: the NG key blob is not downloaded)")
                                    : new PinnedHttpMagicSource(),
                })),
                _ => new GameSpec(g, slug, () => new FortniteModule(new FortniteOptions
                {
                    KeysDir = Path.Combine(layout.KeysDir, "fortnite"),
                    Offline = offline,
                })),
            });
        }
        return list;
    }

    public static GameSpec Spec(string shortName, string cacheDir, bool offline) =>
        Specs(cacheDir, offline).FirstOrDefault(s => s.Short == shortName)
        ?? throw new ArgumentException($"unknown game '{shortName}'");

    public static Dictionary<string, string> FakeBehaviours()
    {
        var map = new Dictionary<string, string>();
        var env = Environment.GetEnvironmentVariable(TestModulesEnv);
        if (string.IsNullOrWhiteSpace(env)) return map;
        foreach (var part in env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && AppInfo.Games.Contains(kv[0])) map[kv[0]] = kv[1];
        }
        return map;
    }
}
