using CthExtract.Core;

namespace CthExtract.Fortnite;

/// <summary>Reads fortnite content from the player's own install. (Stub: replaced by the real module.)</summary>
public sealed class FortniteModule : IGameModule
{
    public string Game => "fortnite";
    public string Short => "fortnite";
    public IEnumerable<string> FingerprintFiles(string gameDir) => Array.Empty<string>();
    public void Extract(string gameDir, ExtractContext ctx, CancellationToken ct) { }
}
