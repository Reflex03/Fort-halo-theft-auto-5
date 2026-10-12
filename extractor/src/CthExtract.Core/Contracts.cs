// Craft Theft Halo extractor: the contract between the game modules and the cache.
// See docs/CONTRACTS.md. Game modules read the player's own install and hand results to ExtractContext;
// they never write into the cache themselves.
using CthExtract.Core.Generated;

namespace CthExtract.Core;

/// <summary>One game's reader. Implementations live in CthExtract.Halo / .Gta / .Fortnite.</summary>
public interface IGameModule
{
    /// <summary>Melty game slug: "halo-mcc", "gta-v" or "fortnite" (matches the sources sheet's game column).</summary>
    string Game { get; }

    /// <summary>Short name used for the cache pack folder and --only: "halo", "gta", "fortnite".</summary>
    string Short { get; }

    /// <summary>Files whose size+mtime change means the game was updated (relative to the game folder).</summary>
    IEnumerable<string> FingerprintFiles(string gameDir);

    /// <summary>
    /// Read every row of Sources.ForGame(Game) that this module can, and report each through ctx
    /// (ctx.Mesh / ctx.Texture / ctx.Sound / ctx.Stats, or ctx.Missing / ctx.Failed). Rows never reported are recorded
    /// as "unimplemented". Throwing aborts only this game; what was already reported is kept.
    /// </summary>
    void Extract(string gameDir, ExtractContext ctx, CancellationToken ct);
}

/// <summary>A triangle mesh in Minecraft space: metres, +Y up, forward = +Z, right-handed.</summary>
public sealed class MeshData
{
    public sealed class Node
    {
        public required string Name;
        public float X, Y, Z; // pivot
    }

    public sealed class Submesh
    {
        /// <summary>Texture path under assets/crafttheft/textures without ".png", e.g. "halo/assault_rifle/0".</summary>
        public required string Material;
        /// <summary>Index into Nodes this submesh rotates around (wheels), or -1.</summary>
        public int Node = -1;
        /// <summary>8 floats per vertex: px py pz nx ny nz u v.</summary>
        public List<float> Vertices = new();
        public List<uint> Indices = new();
        public int VertexCount => Vertices.Count / 8;
        public int TriangleCount => Indices.Count / 3;
    }

    public List<Node> Nodes = new();
    public List<Submesh> Submeshes = new();
    public int TriangleCount => Submeshes.Sum(s => s.TriangleCount);
}

/// <summary>An RGBA8 image (row-major, top row first).</summary>
public sealed record Rgba(int Width, int Height, byte[] Pixels);

public enum AssetStatus { Ok, Missing, Failed, Unimplemented }

public sealed record AssetResult(string Id, AssetStatus Status, string? Detail, string? Out);
