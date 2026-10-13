using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;
using Reclaimer.Geometry;
using Xunit;

namespace CthExtract.Halo.Tests;

/// <summary>
/// Critique D-H1: write synthetic scenes with non-identity placement, permutation and compression transforms using
/// Reclaimer's own RMF writer, read them back with Reclaimer's own Python reader (the path the Halo field note proved on
/// real data) and require MeshBuilder to place every triangle exactly where that reader puts it, after the documented
/// (y, z, x) × 3.048 conversion, origin move and held-size scale. Skipped when python3 or the Reclaimer clone is missing.
/// </summary>
public class RmfOracleTests
{
    private static string? RmfImporterDir()
    {
        // CTH_REFS = folder with clones of the upstream repos (Reclaimer, Fmod5Sharp); reference only, never vendored
        var refs = Environment.GetEnvironmentVariable("CTH_REFS") ?? "";
        if (refs.Length == 0) return null;
        var dir = Path.Combine(refs, "Reclaimer", "Reclaimer.RMFImporter");
        return File.Exists(Path.Combine(dir, "Reclaimer", "src", "SceneReader.py")) ? dir : null;
    }

    private static JsonArray? RunOracle(string rmf)
    {
        var dir = RmfImporterDir();
        if (dir is null) return null;
        var script = Path.Combine(AppContext.BaseDirectory, "oracle", "rmf_oracle.py");
        var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-I"); psi.ArgumentList.Add(script); psi.ArgumentList.Add(dir); psi.ArgumentList.Add(rmf);
        Process p;
        try { p = Process.Start(psi)!; }
        catch { return null; }
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, "oracle failed: " + stderr);
        return JsonNode.Parse(stdout)!.AsArray();
    }

    private static Scene ComplexScene()
    {
        var model = new Model { Name = "oracle_gun" };
        // mesh 0: compressed box (0..1 positions/uvs + bounds), as a triangle strip-free list
        var (p, n, uv, idx) = SceneFactory.Box(0, 1, 0.5f, 0.5f);
        var unit = p.Select(v => new Vector3(v.X, v.Y + 0.5f, v.Z + 0.5f)).ToList();
        model.Meshes.Add(SceneFactory.MakeMesh(unit, n, uv, idx, null,
            posBounds: new RealBounds3D(new RealBounds(-0.05f, 0.27f), new RealBounds(-0.021f, 0.019f), new RealBounds(-0.043f, 0.041f)),
            texBounds: new RealBounds2D(new RealBounds(0.1f, 0.9f), new RealBounds(-0.5f, 1.5f))));
        // mesh 1: a scope as a triangle strip, uncompressed
        var sp = new List<Vector3> { new(0.0f, -0.01f, 0.05f), new(0.12f, -0.01f, 0.05f), new(0.0f, 0.01f, 0.06f), new(0.12f, 0.01f, 0.06f), new(0.0f, 0.0f, 0.08f), new(0.12f, 0.0f, 0.08f) };
        var sn = Enumerable.Repeat(Vector3.UnitZ, sp.Count).ToList();
        var su = sp.Select(v => new Vector2(v.X * 4, v.Y * 10)).ToList();
        model.Meshes.Add(SceneFactory.MakeMesh(sp, sn, su, new List<int> { 0, 1, 2, 3, 4, 5 }, null, IndexFormat.TriangleStrip));
        // mesh 2: an alternative stock (unchosen permutation)
        model.Meshes.Add(SceneFactory.MakeMesh(p.Select(v => v * 0.1f).ToList(), n, uv, idx, null));

        var body = new ModelRegion { Name = "body" };
        body.Permutations.Add(new ModelPermutation { Name = "base", MeshRange = (0, 1) });
        body.Permutations.Add(new ModelPermutation { Name = "heavy", MeshRange = (2, 1) });
        var scope = new ModelRegion { Name = "scope" };
        var rot = Matrix4x4.CreateFromYawPitchRoll(0.3f, -0.2f, 0.1f) * Matrix4x4.CreateTranslation(0.02f, -0.01f, 0.03f);
        scope.Permutations.Add(new ModelPermutation { Name = "default", MeshRange = (1, 1), Transform = rot, UniformScale = 1.25f });
        model.Regions.Add(body);
        model.Regions.Add(scope);
        SceneFactory.AddMarker(model, "right_hand", new Vector3(0.03f, 0, -0.04f));

        var scene = new Scene { CoordinateSystem = CoordinateSystem.Default.WithScale(3048) };
        scene.ChildObjects.Add(new ObjectPlacement(model)
        {
            Transform = Matrix4x4.CreateFromAxisAngle(Vector3.UnitZ, 0.15f) * Matrix4x4.CreateTranslation(1.5f, -2f, 0.25f),
        });
        return scene;
    }

    [Fact]
    public void MeshBuilderMatchesReclaimersOwnRmfReader()
    {
        Assert.SkipWhen(RmfImporterDir() is null, "Reclaimer RMF importer clone not available");
        var scene = ComplexScene();
        using var t = new TempDir();
        var rmf = Path.Combine(t.Path, "oracle.rmf");
        scene.WriteRMF(rmf);
        var oracle = RunOracle(rmf);
        Assert.SkipWhen(oracle is null, "python3 not available");

        var built = MeshBuilder.Build(scene, new MeshBuildOptions { MaterialPrefix = "halo/oracle" });
        Assert.False(built.Rotated180);
        var chosen = new HashSet<string> { "body/base", "scope/default" };
        var expected = new List<float[]>();
        foreach (var entry in oracle!)
        {
            if (!chosen.Contains($"{entry!["region"]}/{entry["permutation"]}")) continue;
            foreach (var tri in entry["triangles"]!.AsArray())
                expected.Add(Canon(tri!.AsArray().Select(v =>
                {
                    var a = v!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
                    var mc = (MeshBuilder.HaloToMc(new Vector3(a[0], a[1], a[2])) * 3.048f - built.Origin) * built.Scale;
                    return new[] { mc.X, mc.Y, mc.Z, a[3], a[4] };
                }).ToList()));
        }

        var actual = new List<float[]>();
        foreach (var s in built.Mesh.Submeshes)
            for (int i = 0; i < s.Indices.Count; i += 3)
                actual.Add(Canon(Enumerable.Range(0, 3).Select(k =>
                {
                    int v = (int)s.Indices[i + k] * 8;
                    return new[] { s.Vertices[v], s.Vertices[v + 1], s.Vertices[v + 2], s.Vertices[v + 6], s.Vertices[v + 7] };
                }).ToList()));

        Assert.Equal(12 + 4, expected.Count);   // box (12) + strip (4 triangles)
        Assert.Equal(expected.Count, actual.Count);
        var remaining = actual.ToList();
        foreach (var e in expected)
        {
            int k = remaining.FindIndex(a => a.Zip(e).All(z => MathF.Abs(z.First - z.Second) < 2e-4f));
            Assert.True(k >= 0, "no matching triangle for oracle triangle " + string.Join(",", e.Select(x => x.ToString("0.0000"))));
            remaining.RemoveAt(k);
        }
    }

    /// <summary>Triangle as 15 floats with its corners in a canonical order (winding may legitimately differ).</summary>
    private static float[] Canon(List<float[]> corners) =>
        corners.OrderBy(c => MathF.Round(c[0], 3)).ThenBy(c => MathF.Round(c[1], 3)).ThenBy(c => MathF.Round(c[2], 3)).SelectMany(c => c).ToArray();
}
