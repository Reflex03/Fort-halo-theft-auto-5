using System.Text.Json.Nodes;

namespace CthExtract.E2E;

/// <summary>
/// <code>
///   CthExtract.E2E make &lt;root&gt;                      fake game folders under root/games (+ root/seed/keys/fortnite)
///   CthExtract.E2E verify &lt;cache&gt; [--sheets &lt;dir&gt;] [--report &lt;file.json&gt;]
/// </code>
/// verify exits 0 when the cache follows docs/CONTRACTS.md, 1 otherwise, and prints every fact it checked.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "make")
        {
            var made = FakeInstalls.Make(args[1]);
            foreach (var n in made.Notes) Console.WriteLine(n);
            Console.WriteLine($"HALO={made.Halo}");
            Console.WriteLine($"GTA={made.Gta}");
            Console.WriteLine($"FORTNITE={made.Fortnite}");
            Console.WriteLine($"FORTNITE_KEYS_SEED={made.FortniteKeysSeed ?? ""}");
            return 0;
        }
        if (args.Length >= 2 && args[0] == "verify")
        {
            string sheets = DefaultSheets(), report = "";
            for (int i = 2; i + 1 < args.Length; i += 2)
            {
                if (args[i] == "--sheets") sheets = args[i + 1];
                else if (args[i] == "--report") report = args[i + 1];
            }
            var c = CacheContract.Verify(args[1], sheets);
            foreach (var f in c.Facts) Console.WriteLine("  fact: " + f);
            foreach (var w in c.Warnings) Console.WriteLine("  WARN: " + w);
            foreach (var p in c.Problems) Console.WriteLine("  CONTRACT VIOLATION: " + p);
            Console.WriteLine(c.Problems.Count == 0 ? "contract check: PASS" : $"contract check: FAIL ({c.Problems.Count} problem(s))");
            if (report.Length > 0)
                File.WriteAllText(report, new JsonObject
                {
                    ["pass"] = c.Problems.Count == 0,
                    ["problems"] = new JsonArray(c.Problems.Select(p => (JsonNode)p).ToArray()),
                    ["warnings"] = new JsonArray(c.Warnings.Select(p => (JsonNode)p).ToArray()),
                    ["facts"] = new JsonArray(c.Facts.Select(p => (JsonNode)p).ToArray()),
                }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return c.Problems.Count == 0 ? 0 : 1;
        }
        Console.Error.WriteLine("usage: CthExtract.E2E make <root> | verify <cache> [--sheets <dir>] [--report <file>]");
        return 3;
    }

    /// <summary>The repository's sheets folder, found upwards from the binary.</summary>
    public static string DefaultSheets()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "sheets", "weapons.json"))) return Path.Combine(d.FullName, "sheets");
        return "sheets";
    }
}
