using System.Text.Json.Nodes;
using CthExtract.Core;

namespace CthExtract;

/// <summary>
/// Synthetic stand-in for a game module, used only by tests (in-process, or through the CTHEXTRACT_TEST_MODULES
/// environment variable for end-to-end runs of the real executable). Writes content generated here, never game data.
/// Behaviours: ok, partial, missing, throw, failfast, exit, hang (ignores cancellation), coop (waits for
/// cancellation), slow:&lt;ms&gt;, badfile (reports Ok for a file it never wrote), nostats (StatsDone without Stats).
/// </summary>
internal sealed class FakeModule(string game, string shortName, string behaviour) : IGameModule
{
    public string Game => game;
    public string Short => shortName;

    /// <summary>gameDir/fake/&lt;short&gt;.txt: tests touch it to simulate a game update.</summary>
    public IEnumerable<string> FingerprintFiles(string gameDir) => new[] { $"fake/{shortName}.txt" };

    public void Extract(string gameDir, ExtractContext ctx, CancellationToken ct)
    {
        var rows = ctx.Rows;
        ctx.Log.Info($"[{shortName}] FAKE module '{behaviour}' over {rows.Count} rows from {gameDir}");
        var parts = behaviour.Split(':', 2);
        switch (parts[0])
        {
            case "ok":
                foreach (var r in rows) SyntheticContent.Write(ctx, r.Id);
                break;
            case "slow":
                Thread.Sleep(parts.Length > 1 && int.TryParse(parts[1], out var ms) ? ms : 1000);
                foreach (var r in rows) SyntheticContent.Write(ctx, r.Id);
                break;
            case "partial":
                for (int i = 0; i < rows.Count; i++)
                    if (i % 2 == 0) SyntheticContent.Write(ctx, rows[i].Id);
                    else ctx.Missing(rows[i].Id, "fake: not in this install");
                break;
            case "missing":
                foreach (var r in rows) ctx.Missing(r.Id, $"fake: {shortName} is not installed");
                break;
            case "throw":
                SyntheticContent.Write(ctx, rows[0].Id);
                SyntheticContent.Write(ctx, rows[1].Id);
                throw new InvalidDataException("fake parser failure");
            case "failfast":
                SyntheticContent.Write(ctx, rows[0].Id);
                SyntheticContent.Write(ctx, rows[1].Id);
                Environment.FailFast("fake fatal crash (stands in for an access violation or stack overflow)");
                break;
            case "exit":
                SyntheticContent.Write(ctx, rows[0].Id);
                Environment.Exit(42);
                break;
            case "hang":
                SyntheticContent.Write(ctx, rows[0].Id);
                Thread.Sleep(Timeout.Infinite);
                break;
            case "coop":
                SyntheticContent.Write(ctx, rows[0].Id);
                ct.WaitHandle.WaitOne();
                ct.ThrowIfCancellationRequested();
                break;
            case "badfile":
                foreach (var r in rows) SyntheticContent.Write(ctx, r.Id);
                // report a mesh as written although the file is garbage
                var mesh = rows.First(r => r.Kind == "mesh");
                File.WriteAllText(Path.Combine(ctx.AssetsDir, mesh.Out), "not a mesh");
                break;
            default:
                throw new ArgumentException($"unknown fake behaviour '{behaviour}'");
        }
    }
}

/// <summary>Small synthetic assets made by our own code (a box mesh, a gradient icon, a fake Ogg header, stats).</summary>
internal static class SyntheticContent
{
    public static void Write(ExtractContext ctx, string id, string tag = "fake")
    {
        var row = ctx.Row(id);
        switch (row.Kind)
        {
            case "mesh":
                {
                    var material = $"{ctx.Short}/{tag}_{id}/0";
                    ctx.MaterialTexture(material, Icon(8, 8, id.Length));
                    ctx.Mesh(id, Box(material), tag);
                    break;
                }
            case "icon":
                ctx.Texture(id, Icon(16, 16, id.Length), detail: tag);
                break;
            case "sound":
                ctx.Sound(id, OggLike(id), tag);
                break;
            case "stats":
                ctx.Stats(id, tag, new JsonObject { ["value"] = id.Length, ["tag"] = tag });
                ctx.StatsDone(id, tag);
                break;
            default:
                ctx.Failed(id, $"fake: unknown kind {row.Kind}");
                break;
        }
    }

    /// <summary>A unit cube: 24 vertices, 12 triangles, outward normals, one material.</summary>
    public static MeshData Box(string material, float size = 1f)
    {
        var s = new MeshData.Submesh { Material = material };
        float h = size / 2;
        (float nx, float ny, float nz, float[] corners)[] faces =
        {
            (1, 0, 0, new[] { h, -h, -h, h, h, -h, h, h, h, h, -h, h }),
            (-1, 0, 0, new[] { -h, -h, h, -h, h, h, -h, h, -h, -h, -h, -h }),
            (0, 1, 0, new[] { -h, h, -h, -h, h, h, h, h, h, h, h, -h }),
            (0, -1, 0, new[] { -h, -h, h, -h, -h, -h, h, -h, -h, h, -h, h }),
            (0, 0, 1, new[] { h, -h, h, h, h, h, -h, h, h, -h, -h, h }),
            (0, 0, -1, new[] { -h, -h, -h, -h, h, -h, h, h, -h, h, -h, -h }),
        };
        float[] us = { 0, 0, 1, 1 }, vs = { 1, 0, 0, 1 };
        foreach (var (nx, ny, nz, c) in faces)
        {
            uint b = (uint)s.VertexCount;
            for (int k = 0; k < 4; k++)
                s.Vertices.AddRange(new[] { c[k * 3], c[k * 3 + 1] + h, c[k * 3 + 2], nx, ny, nz, us[k], vs[k] });
            s.Indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
        }
        return new MeshData { Submeshes = { s } };
    }

    public static Rgba Icon(int w, int h, int seed)
    {
        var p = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                p[i] = (byte)(x * 255 / Math.Max(1, w - 1));
                p[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                p[i + 2] = (byte)(seed * 37);
                p[i + 3] = 255;
            }
        return new Rgba(w, h, p);
    }

    /// <summary>Bytes that pass Ogg.LooksLikeVorbis (an Ogg page header and a Vorbis id packet start); not playable.</summary>
    public static byte[] OggLike(string id)
    {
        var b = new byte[160];
        "OggS"u8.CopyTo(b);
        b[5] = 2;      // beginning of stream
        b[26] = 1;     // one segment
        b[27] = 30;    // of 30 bytes
        "\u0001vorbis"u8.CopyTo(b.AsSpan(28));
        for (int i = 0; i < id.Length && 40 + i < b.Length; i++) b[40 + i] = (byte)id[i];
        return b;
    }
}
