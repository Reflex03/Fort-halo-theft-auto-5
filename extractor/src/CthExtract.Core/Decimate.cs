using MeshDecimator.Algorithms;
using MDMesh = MeshDecimator.Mesh;
using MDVector2 = MeshDecimator.Math.Vector2;
using MDVector3 = MeshDecimator.Math.Vector3;
using MDVector3d = MeshDecimator.Math.Vector3d;

namespace CthExtract.Core;

/// <summary>
/// Bring a mesh under a triangle budget. Quadric edge collapse (vendored Whinarn/MeshDecimator, MIT) per pivot node,
/// with UV seams and material borders linked so the surface stays closed; falls back to vertex clustering when the
/// quadric pass fails, misses the budget or produces bad numbers. Keeps materials, nodes and each submesh's node.
/// </summary>
public static class Decimate
{
    public static MeshData ToBudget(MeshData mesh, int maxTris) => ToBudget(mesh, maxTris, out _);

    /// <param name="method">What was done, for logs and the manifest detail ("quadric", "quadric+cluster", ...).</param>
    public static MeshData ToBudget(MeshData mesh, int maxTris, out string method)
    {
        method = "none";
        if (maxTris <= 0 || mesh.TriangleCount <= maxTris) return mesh;

        var clean = Clean(mesh);
        int total = clean.TriangleCount;
        if (total <= maxTris) { method = "cleaned"; return clean; }

        // Group submeshes by pivot node: wheels decimate on their own so no body vertex is ever linked to a wheel.
        var groups = new List<(int Node, List<int> Subs, int Tris)>();
        for (int i = 0; i < clean.Submeshes.Count; i++)
        {
            var s = clean.Submeshes[i];
            int gi = groups.FindIndex(g => g.Node == s.Node);
            if (gi < 0) { groups.Add((s.Node, new List<int>(), 0)); gi = groups.Count - 1; }
            var g = groups[gi];
            g.Subs.Add(i);
            groups[gi] = (g.Node, g.Subs, g.Tris + s.TriangleCount);
        }
        var budgets = Budgets(groups.Select(g => g.Tris).ToArray(), maxTris);

        var replaced = new MeshData.Submesh?[clean.Submeshes.Count];
        var used = new SortedSet<string>();
        for (int gi = 0; gi < groups.Count; gi++)
        {
            var (node, subs, tris) = groups[gi];
            int budget = budgets[gi];
            if (tris <= budget) { foreach (var i in subs) replaced[i] = clean.Submeshes[i]; continue; }
            var input = subs.Select(i => clean.Submeshes[i]).ToList();
            List<MeshData.Submesh>? result = null;
            string how = "quadric";
            try
            {
                var cur = input;
                for (int pass = 0; pass < 3; pass++)
                {
                    cur = Quadric(cur, budget);
                    if (cur.Sum(s => s.TriangleCount) <= budget) { result = cur; break; }
                    how = "quadric x" + (pass + 2);
                }
                if (result != null && !Sane(input, result)) result = null;
            }
            catch (Exception) { result = null; }
            if (result == null)
            {
                how = "cluster";
                result = ClusterToBudget(input, budget);
            }
            used.Add(how);
            for (int k = 0; k < subs.Count; k++) replaced[subs[k]] = k < result.Count ? result[k] : null;
        }

        var outMesh = new MeshData { Nodes = clean.Nodes };
        foreach (var s in replaced)
            if (s != null && s.Indices.Count > 0) outMesh.Submeshes.Add(s);
        method = used.Count == 0 ? "none" : string.Join("+", used);
        return outMesh;
    }

    /// <summary>Split maxTris over groups in proportion to their size; small groups keep at least a few triangles.</summary>
    internal static int[] Budgets(int[] tris, int maxTris)
    {
        long total = tris.Sum(t => (long)t);
        var b = new int[tris.Length];
        for (int i = 0; i < tris.Length; i++)
            b[i] = (int)Math.Max(Math.Min(tris[i], 8), maxTris * (long)tris[i] / Math.Max(1, total));
        int excess = b.Sum() - maxTris;
        while (excess > 0)
        {
            int big = Array.IndexOf(b, b.Max());
            int take = Math.Min(excess, b[big] - 1);
            if (take <= 0) break;
            b[big] -= take; excess -= take;
        }
        return b;
    }

    /// <summary>Drop triangles with repeated or out-of-range indices, non-finite vertices, or zero area (e.g. the
    /// collapsed rows at a UV sphere's poles: they cannot be collapsed and stall the quadric pass).</summary>
    private static MeshData Clean(MeshData mesh)
    {
        var (min, max) = Bounds(mesh.Submeshes.Where(s => s.Vertices.All(float.IsFinite)));
        double ext = Math.Max(max[0] - min[0], Math.Max(max[1] - min[1], max[2] - min[2]));
        double minCross = double.IsFinite(ext) && ext > 0 ? ext * ext * 1e-10 : 0; // |cross| = 2 * area
        var o = new MeshData { Nodes = mesh.Nodes };
        foreach (var s in mesh.Submeshes)
        {
            var c = new MeshData.Submesh { Material = s.Material, Node = s.Node, Vertices = s.Vertices };
            int vc = s.VertexCount;
            for (int t = 0; t + 2 < s.Indices.Count; t += 3)
            {
                uint a = s.Indices[t], b = s.Indices[t + 1], d = s.Indices[t + 2];
                if (a >= vc || b >= vc || d >= vc || a == b || b == d || a == d) continue;
                if (!Finite(s, a) || !Finite(s, b) || !Finite(s, d)) continue;
                if (TwiceArea(s, a, b, d) <= minCross) continue;
                c.Indices.Add(a); c.Indices.Add(b); c.Indices.Add(d);
            }
            o.Submeshes.Add(c);
        }
        return o;

        static bool Finite(MeshData.Submesh s, uint v)
        {
            int i = (int)v * 8;
            for (int k = 0; k < 8; k++) if (!float.IsFinite(s.Vertices[i + k])) return false;
            return true;
        }
    }

    private static double TwiceArea(MeshData.Submesh s, uint ia, uint ib, uint ic)
    {
        int a = (int)ia * 8, b = (int)ib * 8, c = (int)ic * 8;
        var v = s.Vertices;
        double ux = v[b] - v[a], uy = v[b + 1] - v[a + 1], uz = v[b + 2] - v[a + 2];
        double wx = v[c] - v[a], wy = v[c + 1] - v[a + 1], wz = v[c + 2] - v[a + 2];
        double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
        return Math.Sqrt(nx * nx + ny * ny + nz * nz);
    }

    private static (float[] Min, float[] Max) Bounds(IEnumerable<MeshData.Submesh> subs)
    {
        var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
        var max = new[] { float.MinValue, float.MinValue, float.MinValue };
        foreach (var s in subs)
            for (int i = 0; i < s.Vertices.Count; i += 8)
                for (int k = 0; k < 3; k++)
                {
                    min[k] = Math.Min(min[k], s.Vertices[i + k]);
                    max[k] = Math.Max(max[k], s.Vertices[i + k]);
                }
        return (min, max);
    }

    /// <summary>One quadric pass over a group of submeshes sharing a pivot. Returns the same number of submeshes, in order.</summary>
    private static List<MeshData.Submesh> Quadric(List<MeshData.Submesh> subs, int target)
    {
        var (min, max) = Bounds(subs);
        double extent = Math.Max(max[0] - min[0], Math.Max(max[1] - min[1], max[2] - min[2]));
        if (!(extent > 0)) extent = 1;
        double cx = (min[0] + max[0]) / 2.0, cy = (min[1] + max[1]) / 2.0, cz = (min[2] + max[2]) / 2.0;

        int vertexTotal = subs.Sum(s => s.VertexCount);
        var pos = new MDVector3d[vertexTotal];
        var nrm = new MDVector3[vertexTotal];
        var uv = new MDVector2[vertexTotal];
        var idx = new int[subs.Count][];
        int baseV = 0;
        for (int si = 0; si < subs.Count; si++)
        {
            var s = subs[si];
            for (int v = 0; v < s.VertexCount; v++)
            {
                int b = v * 8;
                // the decimator's error thresholds are absolute, so work in a unit-sized, centred copy
                pos[baseV + v] = new MDVector3d((s.Vertices[b] - cx) / extent, (s.Vertices[b + 1] - cy) / extent, (s.Vertices[b + 2] - cz) / extent);
                nrm[baseV + v] = new MDVector3(s.Vertices[b + 3], s.Vertices[b + 4], s.Vertices[b + 5]);
                uv[baseV + v] = new MDVector2(s.Vertices[b + 6], s.Vertices[b + 7]);
            }
            idx[si] = new int[s.Indices.Count];
            for (int i = 0; i < s.Indices.Count; i++) idx[si][i] = (int)s.Indices[i] + baseV;
            baseV += s.VertexCount;
        }

        var md = new MDMesh(pos, idx) { Normals = nrm, UV1 = uv };
        var alg = new FastQuadricMeshSimplification
        {
            PreserveBorders = false,
            PreserveSeams = false,
            PreserveFoldovers = false,
            EnableSmartLink = true,
            VertexLinkDistanceSqr = 1e-12, // (1e-6 of the mesh size)^2: links split vertices at UV seams and material borders
            MaxIterationCount = 100,
            Agressiveness = 7.0,
        };
        alg.Initialize(md);
        alg.DecimateMesh(Math.Max(1, target));
        var r = alg.ToMesh();

        var rp = r.Vertices;
        var rn = r.Normals;
        var ruv = r.UV1;
        var result = new List<MeshData.Submesh>(subs.Count);
        for (int si = 0; si < subs.Count; si++)
        {
            var o = new MeshData.Submesh { Material = subs[si].Material, Node = subs[si].Node };
            var ri = si < r.SubMeshCount ? r.GetIndices(si) : Array.Empty<int>();
            var map = new Dictionary<int, uint>();
            for (int t = 0; t + 2 < ri.Length; t += 3)
            {
                int a = ri[t], b = ri[t + 1], c = ri[t + 2];
                if (a == b || b == c || a == c) continue;
                uint la = Local(a), lb = Local(b), lc = Local(c);
                if (la == lb || lb == lc || la == lc) continue;
                o.Indices.Add(la); o.Indices.Add(lb); o.Indices.Add(lc);
            }
            FixNormals(o);
            result.Add(o);

            uint Local(int g)
            {
                if (map.TryGetValue(g, out var l)) return l;
                l = (uint)o.VertexCount;
                map[g] = l;
                var p = rp[g];
                o.Vertices.Add((float)(p.x * extent + cx));
                o.Vertices.Add((float)(p.y * extent + cy));
                o.Vertices.Add((float)(p.z * extent + cz));
                var n = rn != null && g < rn.Length ? rn[g] : default;
                o.Vertices.Add(n.x); o.Vertices.Add(n.y); o.Vertices.Add(n.z);
                var u = ruv != null && g < ruv.Length ? ruv[g] : default;
                o.Vertices.Add(u.x); o.Vertices.Add(u.y);
                return l;
            }
        }
        return result;
    }

    /// <summary>Normalise averaged normals; vertices whose normal collapsed to zero get their faces' normal.</summary>
    private static void FixNormals(MeshData.Submesh s)
    {
        int vc = s.VertexCount;
        var acc = new double[vc * 3];
        for (int t = 0; t + 2 < s.Indices.Count; t += 3)
        {
            int a = (int)s.Indices[t] * 8, b = (int)s.Indices[t + 1] * 8, c = (int)s.Indices[t + 2] * 8;
            double ux = s.Vertices[b] - s.Vertices[a], uy = s.Vertices[b + 1] - s.Vertices[a + 1], uz = s.Vertices[b + 2] - s.Vertices[a + 2];
            double vx = s.Vertices[c] - s.Vertices[a], vy = s.Vertices[c + 1] - s.Vertices[a + 1], vz = s.Vertices[c + 2] - s.Vertices[a + 2];
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            foreach (var i in new[] { s.Indices[t], s.Indices[t + 1], s.Indices[t + 2] })
            { acc[i * 3] += nx; acc[i * 3 + 1] += ny; acc[i * 3 + 2] += nz; }
        }
        for (int v = 0; v < vc; v++)
        {
            int b = v * 8;
            double x = s.Vertices[b + 3], y = s.Vertices[b + 4], z = s.Vertices[b + 5];
            double len = Math.Sqrt(x * x + y * y + z * z);
            if (!(len > 1e-6) || !double.IsFinite(len))
            {
                x = acc[v * 3]; y = acc[v * 3 + 1]; z = acc[v * 3 + 2];
                len = Math.Sqrt(x * x + y * y + z * z);
                if (!(len > 1e-12) || !double.IsFinite(len)) { x = 0; y = 1; z = 0; len = 1; }
            }
            s.Vertices[b + 3] = (float)(x / len); s.Vertices[b + 4] = (float)(y / len); s.Vertices[b + 5] = (float)(z / len);
        }
    }

    /// <summary>Quadric output must be finite, non-empty and stay inside the input's bounds (plus 5% slack).</summary>
    private static bool Sane(List<MeshData.Submesh> input, List<MeshData.Submesh> output)
    {
        if (output.Sum(s => s.TriangleCount) == 0) return false;
        foreach (var s in output)
            foreach (var f in s.Vertices)
                if (!float.IsFinite(f)) return false;
        var (imin, imax) = Bounds(input);
        var (omin, omax) = Bounds(output);
        float ext = Math.Max(imax[0] - imin[0], Math.Max(imax[1] - imin[1], imax[2] - imin[2]));
        float slack = Math.Max(1e-4f, ext * 0.05f);
        for (int k = 0; k < 3; k++)
            if (omin[k] < imin[k] - slack || omax[k] > imax[k] + slack) return false;
        return true;
    }

    // ------------------------------------------------------------------ vertex clustering (fallback)

    /// <summary>Vertex clustering on a grid coarsened until the budget holds: first keeping UV seams apart, then (if
    /// that cannot get there) by position only; as a last resort the largest triangles are kept.</summary>
    internal static List<MeshData.Submesh> ClusterToBudget(List<MeshData.Submesh> subs, int maxTris)
    {
        var (min, max) = Bounds(subs);
        float extent = Math.Max(max[0] - min[0], Math.Max(max[1] - min[1], max[2] - min[2]));
        if (!(extent > 0)) return subs;
        List<MeshData.Submesh> result = subs;
        foreach (bool keepUv in new[] { true, false })
        {
            int cells = 256;
            while (cells >= 2)
            {
                result = Cluster(subs, min[0], min[1], min[2], extent / cells, keepUv);
                if (result.Sum(s => s.TriangleCount) <= maxTris) return result;
                cells = (int)(cells * 0.8);
            }
        }
        // last resort: keep the largest triangles so the budget always holds and the silhouette survives
        var all = new List<(int Sub, int Tri, double Area)>();
        for (int si = 0; si < result.Count; si++)
            for (int t = 0; t + 2 < result[si].Indices.Count; t += 3)
                all.Add((si, t, TwiceArea(result[si], result[si].Indices[t], result[si].Indices[t + 1], result[si].Indices[t + 2])));
        var keep = all.OrderByDescending(x => x.Area).Take(Math.Max(0, maxTris)).Select(x => (x.Sub, x.Tri)).ToHashSet();
        for (int si = 0; si < result.Count; si++)
        {
            var kept = new List<uint>();
            for (int t = 0; t + 2 < result[si].Indices.Count; t += 3)
                if (keep.Contains((si, t))) kept.AddRange(result[si].Indices.GetRange(t, 3));
            result[si].Indices = kept;
        }
        return result;
    }

    private static List<MeshData.Submesh> Cluster(List<MeshData.Submesh> subs, float ox, float oy, float oz, float cell, bool keepUv)
    {
        var outList = new List<MeshData.Submesh>(subs.Count);
        foreach (var s in subs)
        {
            var map = new Dictionary<(int, int, int, int, int), uint>(); // cell (+ quantised uv: keeps texture seams)
            var o = new MeshData.Submesh { Material = s.Material, Node = s.Node };
            var remap = new uint[s.VertexCount];
            for (int v = 0; v < s.VertexCount; v++)
            {
                int b = v * 8;
                var key = ((int)((s.Vertices[b] - ox) / cell), (int)((s.Vertices[b + 1] - oy) / cell), (int)((s.Vertices[b + 2] - oz) / cell),
                           keepUv ? (int)(s.Vertices[b + 6] * 64) : 0, keepUv ? (int)(s.Vertices[b + 7] * 64) : 0);
                if (!map.TryGetValue(key, out var idx))
                {
                    idx = (uint)o.VertexCount;
                    map[key] = idx;
                    for (int k = 0; k < 8; k++) o.Vertices.Add(s.Vertices[b + k]);
                }
                remap[v] = idx;
            }
            var seen = new HashSet<(uint, uint, uint)>();
            for (int t = 0; t + 2 < s.Indices.Count; t += 3)
            {
                uint a = remap[s.Indices[t]], bb = remap[s.Indices[t + 1]], c = remap[s.Indices[t + 2]];
                if (a == bb || bb == c || a == c) continue;
                if (!seen.Add((a, bb, c))) continue;
                o.Indices.Add(a); o.Indices.Add(bb); o.Indices.Add(c);
            }
            outList.Add(o);
        }
        return outList;
    }
}
