using CthExtract.Core;

namespace CthExtract.Halo;

/// <summary>Reads halo-mcc content from the player's own install. (Stub: replaced by the real module.)</summary>
public sealed class HaloModule : IGameModule
{
    public string Game => "halo-mcc";
    public string Short => "halo";
    public IEnumerable<string> FingerprintFiles(string gameDir) => Array.Empty<string>();
    public void Extract(string gameDir, ExtractContext ctx, CancellationToken ct) { }
}
