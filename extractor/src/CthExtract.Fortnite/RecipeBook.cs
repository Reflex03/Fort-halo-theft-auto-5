using System.Text.RegularExpressions;
using CthExtract.Core.Generated;

namespace CthExtract.Fortnite;

/// <summary>
/// Reads the lookup hints out of the sources sheet's locator/fallback text, so the sheet stays the source of truth
/// (asset names and regexes live there). Every parse has a built-in default for when the text changes shape; the
/// module logs which one it used.
/// </summary>
public static class RecipeBook
{
    /// <summary>Extra item-definition names from the research notes (wiki item IDs), tried after the sheet's names.</summary>
    private static readonly Dictionary<string, string[]> ExtraItemNames = new()
    {
        ["fn_icon_bandages"] = ["Clyde_Athena_Bandage"],
        ["fn_icon_med_kit"] = ["Clyde_Athena_Medkit"],
        ["fn_icon_small_shield_potion"] = ["Clyde_Athena_ShieldSmall", "Figment_Athena_ShieldSmall"],
        ["fn_icon_shield_potion"] = ["Clyde_Athena_Shields"],
        ["fn_icon_chug_jug"] = ["WID_FoamFlood"],
    };

    /// <summary>Names inside "named (A|B|C)" (or "named A / B ->") of the locator, then the built-in extras.</summary>
    public static List<string> ItemNames(SourceRow row)
    {
        var names = new List<string>();
        var m = Regex.Match(row.Locator, @"named \(([^)]*)\)");
        if (m.Success) names.AddRange(m.Groups[1].Value.Split('|').Select(s => s.Trim()).Where(s => s.Length > 0));
        else
        {
            var m2 = Regex.Match(row.Locator, @"named (.+?)(?:\s+->|$)");
            if (m2.Success) names.AddRange(m2.Groups[1].Value.Split('/', '|', ',').Select(s => s.Trim()).Where(s => Regex.IsMatch(s, @"^\w+$")));
        }
        if (ExtraItemNames.TryGetValue(row.Id, out var extra))
            foreach (var e in extra) if (!names.Contains(e, StringComparer.OrdinalIgnoreCase)) names.Add(e);
        return names;
    }

    /// <summary>Display name for the "any ItemDefinition whose DisplayName matches the item name" fallback:
    /// fn_icon_med_kit -> "Med Kit".</summary>
    public static string DisplayName(SourceRow row)
    {
        var stem = Path.GetFileNameWithoutExtension(row.Out);
        return string.Join(' ', stem.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    /// <summary>Letters and digits only, lower case ("Med-Kit" == "medkit").</summary>
    public static string Squash(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Asset-name keywords that narrow the display-name fallback before anything is loaded.</summary>
    public static string[] Keywords(SourceRow row) => DisplayName(row).ToLowerInvariant() switch
    {
        "bandages" => ["bandage"],
        "med kit" => ["medkit", "medbox", "med_kit"],
        "small shield potion" => ["shieldsmall", "smallshield", "minishield", "shield"],
        "shield potion" => ["shieldpot", "shields", "shield"],
        "chug jug" => ["supermedkit", "chug", "foamflood"],
        var d => d.Split(' ').Where(w => w.Length > 2).ToArray(),
    };

    /// <summary>The first identifier with the given prefix in the locator (e.g. BBID_DefaultBus).</summary>
    public static string? PreferredName(SourceRow row, string prefix)
    {
        var m = Regex.Match(row.Locator, $@"\b({Regex.Escape(prefix)}\w+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>"not matching A|B|C" / "excluding A|B|C" in the fallback text, as a case-insensitive regex.</summary>
    public static Regex? Exclusion(SourceRow row, string fallbackDefault)
    {
        var m = Regex.Match(row.Fallback, @"(?:not matching|excluding)\s+([\w|]+)");
        var pattern = m.Success ? m.Groups[1].Value : fallbackDefault;
        return pattern.Length == 0 ? null : new Regex($"(?i)({pattern})");
    }

    /// <summary>A "(?i)..." regex in the text (up to the next comma or "->").</summary>
    public static Regex? InlineRegex(string text)
    {
        var m = Regex.Match(text, @"(\(\?i\)[^,]+?)(?:,|\s+->|\s+-\s|$)");
        if (!m.Success) return null;
        try { return new Regex(m.Groups[1].Value.Trim()); } catch { return null; }
    }
}
