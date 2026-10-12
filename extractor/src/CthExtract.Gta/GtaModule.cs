using CthExtract.Core;

namespace CthExtract.Gta;

/// <summary>Reads gta-v content from the player's own install. (Stub: replaced by the real module.)</summary>
public sealed class GtaModule : IGameModule
{
    public string Game => "gta-v";
    public string Short => "gta";
    public IEnumerable<string> FingerprintFiles(string gameDir) => Array.Empty<string>();
    public void Extract(string gameDir, ExtractContext ctx, CancellationToken ct) { }
}
