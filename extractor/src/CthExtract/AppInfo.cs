using System.Reflection;
using CthExtract.Core;

namespace CthExtract;

internal static class AppInfo
{
    /// <summary>Plain version ("0.1.0"), written to manifest.json "extractor" and compared by --check.</summary>
    public static string Version { get; } = typeof(AppInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    /// <summary>Informational version, e.g. "0.1.0+3f2c1ab" when built from git; for logs only.</summary>
    public static string Build { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? Version;

    public static string Banner => $"cthextract {Build} (cacheSchema {Manifest.CacheSchema})";

    /// <summary>Canonical game order: one at a time, Halo (smallest) first.</summary>
    public static readonly string[] Games = { "halo", "gta", "fortnite" };
}

/// <summary>Process exit codes (docs/CONTRACTS.md, plus --check's codes from docs/requests/extractor.md).</summary>
internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Partial = 1;
    public const int NothingExtracted = 2;
    public const int BadArguments = 3;
    public const int Locked = 4;
    public const int Watchdog = 5;

    public const int CheckFresh = 0;
    public const int CheckFullRunNeeded = 10;
    public const int CheckRerunGames = 11;
    public const int CheckGamesUpdated = 12;

    public static string Describe(int code) => code switch
    {
        Ok => "all ok",
        Partial => "partial",
        NothingExtracted => "nothing extracted",
        BadArguments => "bad arguments",
        Locked => "another extractor holds the cache lock",
        Watchdog => "watchdog timeout",
        _ => "exit " + code,
    };
}
