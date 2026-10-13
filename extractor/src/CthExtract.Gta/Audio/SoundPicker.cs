using CthExtract.Core;

namespace CthExtract.Gta.Audio;

/// <summary>
/// Chooses the siren and horn waves in vehicles.awc and logs a census of every stream. Stream ids are
/// joaat(name) &amp; 0x1FFFFFFF, so candidate names are hashed and looked up.
/// The sheet's three siren ids were identified here by hash: 0x0D329446 = siren_pa20a_wail,
/// 0x159C9182 = siren_firetruck_wail_01, 0x161768E7 = siren_firetruck_quick_01.
/// </summary>
public static class SoundPicker
{
    public static readonly uint[] SheetSirenIds = { 0x0D329446, 0x159C9182, 0x161768E7 };

    public static readonly string[] SirenNames = BuildSirenNames();
    public static readonly string[] HornNames = BuildHornNames();

    private static string[] BuildSirenNames()
    {
        var l = new List<string> { "siren_pa20a_wail", "siren_firetruck_wail_01", "siren_firetruck_quick_01" };
        foreach (var kind in new[] { "wail", "quick", "yelp", "priority", "warning", "hilo", "hi_lo", "phaser", "powercall", "hyper", "airhorn", "horn" })
            foreach (var n in new[] { "", "_01", "_02", "_03", "_04", "_1", "_2", "_3" })
                foreach (var who in new[] { "", "police_", "ambulance_", "firetruck_", "fbi_", "sheriff_", "bike_", "police_bike_", "pa20a_" })
                    l.Add($"siren_{who}{kind}{n}");
        foreach (var n in new[] { "1", "2", "3", "4", "5", "_1", "_2", "_3", "_01", "_02" }) l.Add("siren" + n);
        foreach (var w in new[] { "police_warning", "ambulance_warning", "firetruck_warning", "police_siren", "siren_police" }) l.Add(w);
        return l.Distinct().ToArray();
    }

    private static string[] BuildHornNames()
    {
        var kinds = new[] { "std", "standard", "car", "normal", "default", "generic", "regular", "sedan", "compact", "coupe", "sports", "sport",
            "super", "muscle", "suv", "van", "truck", "bus", "cop", "police", "bike", "moped", "lowrider", "hipster", "classic", "old", "posh",
            "cheap", "small", "medium", "large", "big", "high", "low", "hi", "lo", "short", "long", "loop", "taxi", "ambulance", "firetruck",
            "army", "air", "airhorn", "tractor", "boat", "train", "euro", "us", "musical", "jazz", "clown", "festive", "lux", "luxury" };
        var nums = new List<string> { "", "_a", "_b", "_c" };
        for (int i = 1; i <= 15; i++) { nums.Add($"_{i}"); nums.Add($"_{i:00}"); nums.Add($"{i}"); }
        var l = new List<string> { "horn", "horns", "car_horn", "vehicle_horn" };
        foreach (var k in kinds)
            foreach (var n in nums)
            {
                l.Add($"horn_{k}{n}");
                l.Add($"{k}_horn{n}");
                l.Add($"horn{n}");
            }
        return l.Distinct().ToArray();
    }

    /// <summary>Preference rank of a horn name (lower is better): plain car horns first.</summary>
    private static int HornRank(string name)
    {
        string[] best = { "std", "standard", "car", "normal", "default", "generic", "regular", "sedan" };
        for (int i = 0; i < best.Length; i++) if (name.Contains(best[i])) return i;
        if (name.Contains("musical") || name.Contains("jazz") || name.Contains("clown") || name.Contains("festive") || name.Contains("air")) return 50;
        return 20;
    }

    public static Dictionary<uint, string> NameTable()
    {
        var d = new Dictionary<uint, string>();
        foreach (var n in SirenNames.Concat(HornNames)) d.TryAdd(Hashes.AwcStreamId(n), n);
        return d;
    }

    public static void Census(AwcFile awc, string where, Log log)
    {
        var names = NameTable();
        log.Info($"[gta] AWC census {where}: {awc.Streams.Count} streams, flags 0x{awc.Flags:X8}{(awc.Multichannel ? ", multichannel" : "")}" +
                 $"{(awc.ChunksEncrypted ? ", data encrypted" : "")}{(awc.WholeFileWasEncrypted ? ", file was encrypted" : "")}" +
                 $"{(awc.Notes.Count > 0 ? "; " + string.Join("; ", awc.Notes) : "")}");
        int shown = 0;
        foreach (var s in awc.Streams.OrderByDescending(s => s.Seconds))
        {
            if (shown++ >= 4000) { log.Info($"[gta]   ... {awc.Streams.Count - 4000} more"); break; }
            log.Info($"[gta]   stream 0x{s.Id:X8} {(names.TryGetValue(s.Id, out var n) ? n : "?"),-28} {s.CodecName,-9} {s.SampleRate,6} Hz {s.Samples,9} smp {s.Seconds,7:F2} s{(s.Loops ? $" loop@{s.LoopPoint}" : "")}");
        }
    }

    public static (AwcStream stream, string why)? PickSiren(AwcFile awc)
    {
        var byId = awc.Streams.Where(s => s.CanDecode && s.Samples > 0).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var id in SheetSirenIds)
            if (byId.TryGetValue(id, out var s))
                return (s, $"sheet id 0x{id:X8} ({NameTable().GetValueOrDefault(id, "?")})");
        var names = SirenNames.Select(n => (n, id: Hashes.AwcStreamId(n))).Where(x => byId.ContainsKey(x.id)).ToList();
        var best = names.Select(x => (x.n, s: byId[x.id])).OrderByDescending(x => x.s.Loops).ThenByDescending(x => x.s.Samples).FirstOrDefault();
        if (best.s is not null) return (best.s, $"fallback: {(best.s.Loops ? "longest looping" : "longest")} siren-named stream '{best.n}' (0x{best.s.Id:X8})");
        return null;
    }

    public static (AwcStream stream, string why)? PickHorn(AwcFile awc)
    {
        var byId = awc.Streams.Where(s => s.CanDecode && s.Samples > 0).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
        var hits = HornNames.Select(n => (n, id: Hashes.AwcStreamId(n))).Where(x => byId.ContainsKey(x.id))
            .Select(x => (x.n, s: byId[x.id])).DistinctBy(x => x.s.Id)
            .OrderBy(x => HornRank(x.n)).ThenByDescending(x => x.s.Loops).ThenBy(x => x.n, StringComparer.Ordinal).ToList();
        if (hits.Count == 0) return null;
        var (name, stream) = hits[0];
        return (stream, $"horn-named stream '{name}' (0x{stream.Id:X8}); {hits.Count} horn name(s) matched: {string.Join(", ", hits.Take(12).Select(h => h.n))}");
    }
}
