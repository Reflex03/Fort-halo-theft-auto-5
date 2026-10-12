namespace CthExtract.Core;

/// <summary>
/// Bring a mesh under a triangle budget. Vertex clustering on a grid that is coarsened until the budget is met;
/// keeps materials and nodes. (A quadric decimator can replace this behind the same signature.)
/// </summary>
public static class Decimate
{
    public static MeshData ToBudget(MeshData mesh, int maxTris)
    {
        if (mesh.TriangleCount <= maxTris) return mesh;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (var s in mesh.Submeshes)
            for (int i = 0; i < s.Vertices.Count; i += 8)
            {
                minX = Math.Min(minX, s.Vertices[i]); maxX = Math.Max(maxX, s.Vertices[i]);
                minY = Math.Min(minY, s.Vertices[i + 1]); maxY = Math.Max(maxY, s.Vertices[i + 1]);
                minZ = Math.Min(minZ, s.Vertices[i + 2]); maxZ = Math.Max(maxZ, s.Vertices[i + 2]);
            }
        float extent = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
        if (extent <= 0) return mesh;
        int cells = 256;
        MeshData result = mesh;
        while (cells >= 4)
        {
            result = Cluster(mesh, minX, minY, minZ, extent / cells);
            if (result.TriangleCount <= maxTris) return result;
            cells = (int)(cells * 0.8);
        }
        return result;
    }

    private static MeshData Cluster(MeshData mesh, float ox, float oy, float oz, float cell)
    {
        var outMesh = new MeshData { Nodes = mesh.Nodes };
        foreach (var s in mesh.Submeshes)
        {
            var map = new Dictionary<(int, int, int, int, int), uint>(); // cell + quantised uv keeps texture seams
            var o = new MeshData.Submesh { Material = s.Material, Node = s.Node };
            var remap = new uint[s.VertexCount];
            for (int v = 0; v < s.VertexCount; v++)
            {
                int b = v * 8;
                var key = ((int)((s.Vertices[b] - ox) / cell), (int)((s.Vertices[b + 1] - oy) / cell), (int)((s.Vertices[b + 2] - oz) / cell),
                           (int)(s.Vertices[b + 6] * 64), (int)(s.Vertices[b + 7] * 64));
                if (!map.TryGetValue(key, out var idx))
                {
                    idx = (uint)o.VertexCount;
                    map[key] = idx;
                    for (int k = 0; k < 8; k++) o.Vertices.Add(s.Vertices[b + k]);
                }
                remap[v] = idx;
            }
            var seen = new HashSet<(uint, uint, uint)>();
            for (int t = 0; t < s.Indices.Count; t += 3)
            {
                uint a = remap[s.Indices[t]], bb = remap[s.Indices[t + 1]], c = remap[s.Indices[t + 2]];
                if (a == bb || bb == c || a == c) continue;
                if (!seen.Add((a, bb, c))) continue;
                o.Indices.Add(a); o.Indices.Add(bb); o.Indices.Add(c);
            }
            if (o.Indices.Count > 0) outMesh.Submeshes.Add(o);
        }
        return outMesh;
    }
}
