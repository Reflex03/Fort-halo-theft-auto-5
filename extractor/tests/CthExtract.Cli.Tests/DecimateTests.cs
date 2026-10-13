using System.Diagnostics;
using CthExtract.Core;
using Xunit;

namespace CthExtract.Cli.Tests;

/// <summary>Synthetic meshes generated here (never game data).</summary>
internal static class Meshes
{
    /// <summary>A UV sphere of radius 1 centred at (0,1,0), split into <paramref name="materials"/> submeshes by longitude,
    /// each with its own vertices (so material borders and the UV seam are split vertices, like game meshes).</summary>
    public static MeshData Sphere(int rings, int segments, int materials = 1, string material = "test/sphere", int node = -1)
    {
        var m = new MeshData();
        int per = segments / materials;
        for (int k = 0; k < materials; k++)
        {
            var s = new MeshData.Submesh { Material = $"{material}/{k}", Node = node };
            int s0 = k * per, s1 = k == materials - 1 ? segments : (k + 1) * per;
            int cols = s1 - s0 + 1;
            for (int r = 0; r <= rings; r++)
                for (int c = 0; c < cols; c++)
                {
                    double th = Math.PI * r / rings, ph = 2 * Math.PI * (s0 + c) / segments;
                    float x = (float)(Math.Sin(th) * Math.Cos(ph)), y = (float)Math.Cos(th), z = (float)(Math.Sin(th) * Math.Sin(ph));
                    s.Vertices.AddRange(new[] { x, y + 1, z, x, y, z, (float)(s0 + c) / segments, (float)r / rings });
                }
            for (int r = 0; r < rings; r++)
                for (int c = 0; c < cols - 1; c++)
                {
                    uint a = (uint)(r * cols + c), b = (uint)((r + 1) * cols + c);
                    s.Indices.AddRange(new[] { a, a + 1, b, a + 1, b + 1, b });
                }
            m.Submeshes.Add(s);
        }
        return m;
    }

    public static void AssertFinite(MeshData m)
    {
        foreach (var s in m.Submeshes) foreach (var f in s.Vertices) Assert.True(float.IsFinite(f));
    }
}

public class DecimateTests
{
    [Fact]
    public void Sphere_30k_goes_under_8k_quickly_keeping_materials_and_shape()
    {
        var mesh = Meshes.Sphere(75, 200, materials: 2);
        Assert.Equal(30000, mesh.TriangleCount);
        var sw = Stopwatch.StartNew();
        var outMesh = Decimate.ToBudget(mesh, 8000, out var method);
        sw.Stop();
        Assert.Equal("quadric", method);
        Assert.InRange(outMesh.TriangleCount, 6000, 8000);
        Assert.InRange(sw.ElapsedMilliseconds, 0, 10_000);
        Assert.Equal(new[] { "test/sphere/0", "test/sphere/1" }, outMesh.Submeshes.Select(s => s.Material));
        Validate.Mesh(outMesh);
        Meshes.AssertFinite(outMesh);
        foreach (var s in outMesh.Submeshes)
            for (int i = 0; i < s.Vertices.Count; i += 8)
            {
                // stays on the sphere (quadric collapse keeps the surface), normals unit, UVs in range
                double r = Math.Sqrt(s.Vertices[i] * s.Vertices[i] + (s.Vertices[i + 1] - 1) * (s.Vertices[i + 1] - 1) + s.Vertices[i + 2] * s.Vertices[i + 2]);
                Assert.InRange(r, 0.95, 1.02);
                double n = Math.Sqrt(s.Vertices[i + 3] * s.Vertices[i + 3] + s.Vertices[i + 4] * s.Vertices[i + 4] + s.Vertices[i + 5] * s.Vertices[i + 5]);
                Assert.InRange(n, 0.99, 1.01);
                Assert.InRange(s.Vertices[i + 6], -0.001f, 1.001f);
                Assert.InRange(s.Vertices[i + 7], -0.001f, 1.001f);
            }
    }

    [Fact]
    public void Under_budget_meshes_are_returned_unchanged()
    {
        var mesh = Meshes.Sphere(10, 20);
        Assert.Same(mesh, Decimate.ToBudget(mesh, 1000));
        Assert.Same(mesh, Decimate.ToBudget(mesh, 0)); // 0 = no budget
    }

    [Fact]
    public void Wheels_decimate_separately_and_keep_their_nodes()
    {
        var body = Meshes.Sphere(60, 120, materials: 1, material: "car/body");
        var mesh = new MeshData { Submeshes = body.Submeshes };
        string[] names = { "wheel_lf", "wheel_rf", "wheel_lb", "wheel_rb" };
        for (int w = 0; w < 4; w++)
        {
            mesh.Nodes.Add(new MeshData.Node { Name = names[w], X = w % 2 == 0 ? -1 : 1, Y = 0.3f, Z = w < 2 ? 1.2f : -1.2f });
            var wheel = Meshes.Sphere(20, 40, material: "car/wheel", node: w).Submeshes[0];
            mesh.Submeshes.Add(wheel);
        }
        int before = mesh.TriangleCount; // 14400 + 4 * 1600
        var outMesh = Decimate.ToBudget(mesh, 8000, out _);
        Assert.InRange(outMesh.TriangleCount, 4000, 8000);
        Assert.Equal(4, outMesh.Nodes.Count);
        Assert.Equal(new[] { -1, 0, 1, 2, 3 }, outMesh.Submeshes.Select(s => s.Node));
        // each wheel got its proportional share, not starved by the body
        foreach (var w in outMesh.Submeshes.Where(s => s.Node >= 0))
            Assert.InRange(w.TriangleCount, 300, 1600 * 8000 / before + 1);
        Validate.Mesh(outMesh);
    }

    [Fact]
    public void Budgets_split_proportionally_and_never_exceed_the_total()
    {
        var b = Decimate.Budgets(new[] { 9000, 500, 500, 3 }, 1000);
        Assert.True(b.Sum() <= 1000);
        Assert.Equal(3, b[3]);              // tiny groups keep what they have (up to 8)
        Assert.True(b[0] > b[1] * 10);
        var tight = Decimate.Budgets(new[] { 100, 100, 100 }, 5);
        Assert.True(tight.Sum() <= 5 || tight.All(x => x == 1));
    }

    [Fact]
    public void Degenerate_input_is_cleaned_and_never_throws()
    {
        var s = new MeshData.Submesh { Material = "x/0" };
        var rnd = new Random(7);
        for (int i = 0; i < 3000; i++)
        {
            // a flat, very thin strip with duplicate and zero-area triangles
            s.Vertices.AddRange(new[] { (float)rnd.NextDouble(), 0f, (float)rnd.NextDouble() * 1e-4f, 0f, 0f, 0f, 0f, 0f });
        }
        for (int i = 0; i + 2 < 3000; i++)
        {
            s.Indices.AddRange(new[] { (uint)i, (uint)i + 1, (uint)i + 2 });
            s.Indices.AddRange(new[] { (uint)i, (uint)i, (uint)i + 1 }); // degenerate
        }
        s.Indices.AddRange(new uint[] { 0, 1, 99999 }); // out of range
        var mesh = new MeshData { Submeshes = { s } };
        var outMesh = Decimate.ToBudget(mesh, 500, out var method);
        Assert.True(outMesh.TriangleCount <= 500, $"{outMesh.TriangleCount} tris via {method}");
        Validate.Mesh(outMesh);
        Meshes.AssertFinite(outMesh);
    }

    [Fact]
    public void Context_mesh_writes_a_decimated_cthm_that_reads_back()
    {
        var root = Path.Combine(Path.GetTempPath(), "cth-cli-tests", Guid.NewGuid().ToString("N")[..12]);
        try
        {
            using var log = new Log(null);
            var ctx = new ExtractContext("halo-mcc", "halo", Path.Combine(root, "pack"), Path.Combine(root, "stats"), Path.Combine(root, "work"), log);
            Assert.Equal(Path.GetFullPath(root), ctx.CacheDir); // parent of a folder named "stats"
            var results = new List<AssetResult>();
            ctx.ResultRecorded = results.Add;
            ctx.Mesh("halo_mesh_magnum", Meshes.Sphere(50, 100, materials: 3, material: "halo/magnum"));
            var r = Assert.Single(results);
            Assert.Equal(AssetStatus.Ok, r.Status);
            Assert.Contains("quadric", r.Detail);
            var m = Cthm.Read(Path.Combine(ctx.AssetsDir, "meshes/halo/magnum.cthm"));
            Assert.InRange(m.TriangleCount, 2000, 3000);
            Assert.Equal(3, m.Submeshes.Count);
        }
        finally { SafeFs.TryDeleteDirectory(root); }
    }
}

public class DecimateQualityTests(ITestOutputHelper output)
{
    private static (double Volume, double Area) Measure(IEnumerable<MeshData.Submesh> subs)
    {
        double vol = 0, area = 0;
        foreach (var s in subs)
            for (int t = 0; t + 2 < s.Indices.Count; t += 3)
            {
                int a = (int)s.Indices[t] * 8, b = (int)s.Indices[t + 1] * 8, c = (int)s.Indices[t + 2] * 8;
                var v = s.Vertices;
                double ax = v[a], ay = v[a + 1] - 1, az = v[a + 2], bx = v[b], by = v[b + 1] - 1, bz = v[b + 2], cx = v[c], cy = v[c + 1] - 1, cz = v[c + 2];
                vol += (ax * (by * cz - bz * cy) - ay * (bx * cz - bz * cx) + az * (bx * cy - by * cx)) / 6.0;
                double ux = bx - ax, uy = by - ay, uz = bz - az, wx = cx - ax, wy = cy - ay, wz = cz - az;
                double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
                area += Math.Sqrt(nx * nx + ny * ny + nz * nz) / 2;
            }
        return (Math.Abs(vol), area);
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(3000)]
    [InlineData(1000)]
    public void Quadric_keeps_volume_and_area_better_than_vertex_clustering(int budget)
    {
        var mesh = Meshes.Sphere(75, 200, materials: 2);
        var (v0, a0) = Measure(mesh.Submeshes);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var q = Decimate.ToBudget(mesh, budget, out var method);
        long qms = sw.ElapsedMilliseconds;
        sw.Restart();
        var c = Decimate.ClusterToBudget(Meshes.Sphere(75, 200, materials: 2).Submeshes, budget);
        long cms = sw.ElapsedMilliseconds;
        var (qv, qa) = Measure(q.Submeshes);
        var (cv, ca) = Measure(c);
        double qVolErr = Math.Abs(qv / v0 - 1), cVolErr = Math.Abs(cv / v0 - 1);
        double qAreaErr = Math.Abs(qa / a0 - 1), cAreaErr = Math.Abs(ca / a0 - 1);
        output.WriteLine($"30000 -> budget {budget}: quadric {q.TriangleCount} tris in {qms} ms ({method}), volume err {qVolErr:P2}, area err {qAreaErr:P2}; " +
                         $"clustering {c.Sum(s => s.TriangleCount)} tris in {cms} ms, volume err {cVolErr:P2}, area err {cAreaErr:P2}");
        Assert.Equal("quadric", method);
        Assert.True(q.TriangleCount <= budget);
        Assert.True(qVolErr < cVolErr, $"quadric volume error {qVolErr:P2} vs clustering {cVolErr:P2}");
        Assert.True(qVolErr < 0.02, $"quadric volume error {qVolErr:P2}");
        Assert.True(qAreaErr < 0.02, $"quadric area error {qAreaErr:P2}");
    }
}

public class DecimateFallbackTests
{
    [Fact]
    public void Many_small_parts_below_what_edge_collapse_can_reach_still_meet_the_budget()
    {
        // 400 separate boxes (4800 tris): each closed part keeps a few triangles under edge collapse alone
        var mesh = new MeshData();
        var s = new MeshData.Submesh { Material = "parts/0" };
        for (int i = 0; i < 400; i++)
        {
            var box = SyntheticContent.Box("x", 0.05f).Submeshes[0];
            uint baseV = (uint)s.VertexCount;
            for (int v = 0; v < box.VertexCount; v++)
            {
                var b = v * 8;
                s.Vertices.AddRange(new[] { box.Vertices[b] + (i % 20) * 0.1f, box.Vertices[b + 1] + (i / 20) * 0.1f, box.Vertices[b + 2] });
                s.Vertices.AddRange(box.Vertices.GetRange(b + 3, 5));
            }
            s.Indices.AddRange(box.Indices.Select(x => x + baseV));
        }
        mesh.Submeshes.Add(s);
        var outMesh = Decimate.ToBudget(mesh, 600, out var method);
        Assert.True(outMesh.TriangleCount <= 600, $"{outMesh.TriangleCount} via {method}");
        Assert.True(outMesh.TriangleCount >= 100, $"{outMesh.TriangleCount} via {method}");
        Validate.Mesh(outMesh);
        Meshes.AssertFinite(outMesh);
    }

    [Fact]
    public void Clustering_fallback_reaches_small_budgets_without_chopping_the_mesh()
    {
        var subs = Meshes.Sphere(75, 200, materials: 2).Submeshes;
        var c = Decimate.ClusterToBudget(subs, 500);
        int tris = c.Sum(x => x.TriangleCount);
        Assert.InRange(tris, 100, 500);
        // still spans the whole sphere (not the first N triangles of it)
        float minY = c.SelectMany(x => Enumerable.Range(0, x.VertexCount).Select(i => x.Vertices[i * 8 + 1])).Min();
        float maxY = c.SelectMany(x => Enumerable.Range(0, x.VertexCount).Select(i => x.Vertices[i * 8 + 1])).Max();
        Assert.True(minY < 0.2f && maxY > 1.8f, $"y range {minY}..{maxY}");
    }
}
