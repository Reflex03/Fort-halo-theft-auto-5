using System.Numerics;
using CthExtract.Core;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Dto;

namespace CthExtract.Fortnite.Conversion;

/// <summary>One mesh to place into the output (a component of a blueprint, or the asset itself). <paramref name="Chain"/>
/// is applied in order (component's own relative transform first, then its parents'), in UE space.</summary>
public sealed record MeshSource(string Name, UObject Mesh, IReadOnlyList<FTransform> Chain)
{
    public MeshSource(string name, UObject mesh) : this(name, mesh, []) { }

    public FVector TransformPosition(FVector v)
    {
        foreach (var t in Chain) v = t.TransformPosition(v);
        return v;
    }

    public FVector TransformVector(FVector v)
    {
        foreach (var t in Chain) v = t.TransformVector(v);
        return v;
    }

    /// <summary>True when the chain contains an odd number of mirroring (negative-determinant) scales.</summary>
    public bool Mirrored => Chain.Count(t => t.Scale3D.X * t.Scale3D.Y * t.Scale3D.Z < 0) % 2 == 1;
}

/// <summary>One output material: a texture path plus the UE material it came from.</summary>
public sealed record MaterialSlot(string Name, string SlotName, FPackageIndex? Material);

public sealed class MeshBuild
{
    public required MeshData Mesh { get; init; }
    public required List<MaterialSlot> Materials { get; init; }
    public required List<string> Notes { get; init; }
}

/// <summary>
/// UE static/skeletal mesh LODs -> Minecraft-space <see cref="MeshData"/>.
/// Axes: UE is left-handed (X forward, Y right, Z up, centimetres); Minecraft space is right-handed metres, so
/// mc = (-UE.Y, UE.Z, UE.X) / 100. That map has determinant -1, which turns UE's clockwise front faces into
/// counter-clockwise ones, so the UE index order is kept. Skeletal meshes are taken in their bind pose.
/// </summary>
public static class MeshConvert
{
    public const float CmToM = 0.01f;

    public static Vector3 ToMc(FVector v) => new(-v.Y * CmToM, v.Z * CmToM, v.X * CmToM);
    public static Vector3 DirToMc(FVector v) => new(-v.Y, v.Z, v.X);

    /// <summary>Convert and merge <paramref name="parts"/>. Picks, per part, the most detailed LOD within its share of
    /// <paramref name="maxTris"/> (game LODs beat our decimator); materials become "<paramref name="materialPrefix"/>/n".</summary>
    public static MeshBuild Build(IReadOnlyList<MeshSource> parts, int maxTris, string materialPrefix)
    {
        var notes = new List<string>();
        var loaded = new List<(MeshSource Src, MeshLodDto<MeshVertex>? S, MeshLodDto<SkinnedMeshVertex>? K, MeshMaterialDto[] Mats, int[] LodTris, IDisposable Dto)>();
        foreach (var part in parts)
        {
            switch (part.Mesh)
            {
                case UStaticMesh sm:
                {
                    var dto = new StaticMeshDto(sm);
                    if (dto.LODs.Count == 0) { notes.Add($"{part.Name}: static mesh {sm.Name} has no LOD with data"); dto.Dispose(); continue; }
                    loaded.Add((part, null, null, dto.Materials, dto.LODs.Select(l => l.Indices.Length / 3).ToArray(), dto));
                    break;
                }
                case USkeletalMesh sk:
                {
                    var dto = new SkeletalMeshDto(sk);
                    if (dto.LODs.Count == 0) { notes.Add($"{part.Name}: skeletal mesh {sk.Name} has no LOD with data (wrong mapping file?)"); dto.Dispose(); continue; }
                    loaded.Add((part, null, null, dto.Materials, dto.LODs.Select(l => l.Indices.Length / 3).ToArray(), dto));
                    break;
                }
                default:
                    notes.Add($"{part.Name}: {part.Mesh.ExportType} is not a mesh");
                    break;
            }
        }

        var mesh = new MeshData();
        var materials = new List<MaterialSlot>();
        var subByMaterial = new Dictionary<string, MeshData.Submesh>();
        long lod0Total = loaded.Sum(l => (long)l.LodTris[0]);
        try
        {
            foreach (var (src, _, _, mats, lodTris, dto) in loaded)
            {
                // this part's share of the budget
                int share = maxTris <= 0 || lod0Total == 0 ? int.MaxValue : (int)Math.Max(1, (long)maxTris * lodTris[0] / lod0Total);
                int lodIndex = Array.FindIndex(lodTris, t => t > 0 && t <= share);
                if (lodIndex < 0) lodIndex = Array.FindLastIndex(lodTris, t => t > 0);
                if (lodIndex < 0) { notes.Add($"{src.Name}: every LOD is empty"); continue; }
                notes.Add($"{src.Name}: {Describe(src.Mesh)} LOD{lodIndex} ({lodTris[lodIndex]} tris; LODs [{string.Join(",", lodTris)}], budget {(share == int.MaxValue ? "none" : share.ToString())})");

                switch (dto)
                {
                    case StaticMeshDto s:
                        Append(mesh, subByMaterial, materials, s.LODs[lodIndex].Vertices, s.LODs[lodIndex].Indices, s.LODs[lodIndex].Sections, mats, src, materialPrefix);
                        break;
                    case SkeletalMeshDto k:
                        Append(mesh, subByMaterial, materials, k.LODs[lodIndex].Vertices, k.LODs[lodIndex].Indices, k.LODs[lodIndex].Sections, mats, src, materialPrefix);
                        break;
                }
            }
        }
        finally
        {
            foreach (var l in loaded) l.Dto.Dispose();
        }

        int fixedNormals = FixNormals(mesh);
        if (fixedNormals > 0) notes.Add($"recomputed normals of {fixedNormals} submeshes (packed normals missing)");
        return new MeshBuild { Mesh = mesh, Materials = materials, Notes = notes };
    }

    private static string Describe(UObject m) => $"{m.ExportType} {m.GetPathName()}";

    private static void Append<TVertex>(MeshData mesh, Dictionary<string, MeshData.Submesh> subs, List<MaterialSlot> materials,
        TVertex[] verts, uint[] indices, MeshSectionDto[] sections, MeshMaterialDto[] mats, MeshSource src, string prefix)
        where TVertex : struct, IMeshVertex
    {
        bool mirrored = src.Mirrored;
        foreach (var sec in sections)
        {
            if (sec.NumFaces <= 0) continue;
            int mi = sec.MaterialIndex;
            var mat = mi >= 0 && mi < mats.Length ? mats[mi] : default;
            var key = mat.Material is { IsNull: false } pi ? PropertyKey(pi) : $"slot:{src.Name}:{mi}";
            if (!subs.TryGetValue(key, out var sub))
            {
                var name = $"{prefix}/{materials.Count}";
                materials.Add(new MaterialSlot(name, mat.SlotName ?? $"slot {mi}", mat.Material is { IsNull: false } ? mat.Material : null));
                sub = new MeshData.Submesh { Material = name };
                subs[key] = sub;
                mesh.Submeshes.Add(sub);
            }

            var remap = new Dictionary<uint, uint>();
            int first = sec.FirstIndex, end = Math.Min(indices.Length, first + sec.NumFaces * 3);
            for (int i = first; i + 2 < end; i += 3)
            {
                uint a = Map(indices[i]), b = Map(indices[i + 1]), c = Map(indices[i + 2]);
                if (mirrored) (b, c) = (c, b);
                sub.Indices.Add(a); sub.Indices.Add(b); sub.Indices.Add(c);
            }

            uint Map(uint vi)
            {
                if (remap.TryGetValue(vi, out var o)) return o;
                if (vi >= verts.Length) throw new InvalidDataException($"index {vi} >= {verts.Length} vertices in {src.Name}");
                var v = verts[vi];
                var p = src.TransformPosition(v.Position);
                var nv = src.TransformVector(new FVector(v.Normal.X, v.Normal.Y, v.Normal.Z));
                var mp = ToMc(p);
                var mn = DirToMc(nv);
                float len = mn.Length();
                if (len > 1e-6f) mn /= len; else mn = Vector3.Zero;
                o = (uint)sub.VertexCount;
                sub.Vertices.Add(mp.X); sub.Vertices.Add(mp.Y); sub.Vertices.Add(mp.Z);
                sub.Vertices.Add(mn.X); sub.Vertices.Add(mn.Y); sub.Vertices.Add(mn.Z);
                sub.Vertices.Add(v.Uv.U); sub.Vertices.Add(v.Uv.V);
                remap[vi] = o;
                return o;
            }
        }
    }

    private static string PropertyKey(FPackageIndex pi)
    {
        try { return pi.ResolvedObject?.GetFullName() ?? pi.Name; } catch { return pi.Name; }
    }

    /// <summary>When most normals of a submesh are zero/garbage, replace them with area-weighted face normals.</summary>
    public static int FixNormals(MeshData mesh)
    {
        int fixedCount = 0;
        foreach (var s in mesh.Submeshes)
        {
            int vc = s.VertexCount, bad = 0;
            for (int v = 0; v < vc; v++)
            {
                float x = s.Vertices[v * 8 + 3], y = s.Vertices[v * 8 + 4], z = s.Vertices[v * 8 + 5];
                float l = x * x + y * y + z * z;
                if (!(l > 0.25f && l < 4f)) bad++;
            }
            if (vc == 0 || bad * 10 < vc) continue;
            var acc = new Vector3[vc];
            for (int i = 0; i + 2 < s.Indices.Count; i += 3)
            {
                int a = (int)s.Indices[i], b = (int)s.Indices[i + 1], c = (int)s.Indices[i + 2];
                var pa = P(s, a); var pb = P(s, b); var pc = P(s, c);
                var n = Vector3.Cross(pb - pa, pc - pa); // CCW front faces in Minecraft space
                acc[a] += n; acc[b] += n; acc[c] += n;
            }
            for (int v = 0; v < vc; v++)
            {
                var n = acc[v].LengthSquared() > 1e-20f ? Vector3.Normalize(acc[v]) : Vector3.UnitY;
                s.Vertices[v * 8 + 3] = n.X; s.Vertices[v * 8 + 4] = n.Y; s.Vertices[v * 8 + 5] = n.Z;
            }
            fixedCount++;
        }
        return fixedCount;
    }

    private static Vector3 P(MeshData.Submesh s, int v) => new(s.Vertices[v * 8], s.Vertices[v * 8 + 1], s.Vertices[v * 8 + 2]);

    /// <summary>Fraction of triangles whose counter-clockwise geometric normal agrees with the vertex normals
    /// (1.0 = correct facing for OpenGL/Minecraft).</summary>
    public static (int Agree, int Total) WindingAgreement(MeshData mesh)
    {
        int agree = 0, total = 0;
        foreach (var s in mesh.Submeshes)
            for (int i = 0; i + 2 < s.Indices.Count; i += 3)
            {
                int a = (int)s.Indices[i], b = (int)s.Indices[i + 1], c = (int)s.Indices[i + 2];
                var g = Vector3.Cross(P(s, b) - P(s, a), P(s, c) - P(s, a));
                var vn = N(s, a) + N(s, b) + N(s, c);
                if (g.LengthSquared() < 1e-14f || vn.LengthSquared() < 1e-6f) continue;
                total++;
                if (Vector3.Dot(g, vn) > 0) agree++;
            }
        return (agree, total);
    }

    private static Vector3 N(MeshData.Submesh s, int v) => new(s.Vertices[v * 8 + 3], s.Vertices[v * 8 + 4], s.Vertices[v * 8 + 5]);
}
