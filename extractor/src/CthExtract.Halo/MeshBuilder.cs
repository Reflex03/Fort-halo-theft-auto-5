using System.Globalization;
using System.Numerics;
using CthExtract.Core;
using Reclaimer.Geometry;

namespace CthExtract.Halo;

/// <summary>What kind of held object a mesh is; decides the held-size range.</summary>
public enum HeldClass { Rifle, Pistol, Grenade }

public sealed class MeshBuildOptions
{
    /// <summary>Material names become "&lt;prefix&gt;/&lt;n&gt;" (textures/halo/assault_rifle/0.png).</summary>
    public required string MaterialPrefix { get; init; }
    public HeldClass Held { get; init; } = HeldClass.Rifle;
    /// <summary>weap "Handle Node" (the render-model node the hand holds), if the weapon names one.</summary>
    public string? HandleNode { get; init; }
    /// <summary>Region name → permutation name chosen by the object's default model variant (hlmt Variants).</summary>
    public IReadOnlyDictionary<string, string>? VariantPermutations { get; init; }

    /// <summary>Held-size range in metres along the barrel axis (+Z), per <see cref="HeldClass"/>.</summary>
    public (float Min, float Max) LengthRange => Held switch
    {
        HeldClass.Pistol => (0.3f, 0.6f),
        HeldClass.Grenade => (0.12f, 0.3f),
        _ => (0.6f, 1.2f),
    };
}

public sealed class BuiltMesh
{
    public required MeshData Mesh { get; init; }
    /// <summary>Material of submesh i (null = no material; a flat texture is written for it).</summary>
    public required List<Material?> Materials { get; init; }
    public List<string> Notes { get; } = new();
    public float MetresPerUnit { get; set; }
    public float NativeLength { get; set; }     // metres along +Z before the held-size scale
    public float Scale { get; set; }            // held-size factor applied after the 1 wu = 3.048 m conversion
    public string OriginSource { get; set; } = "";
    /// <summary>The grip point that became (0,0,0): Minecraft axes, metres, before the held-size scale.</summary>
    public Vector3 Origin { get; set; }
    public bool Rotated180 { get; set; }
    public bool WindingFlipped { get; set; }
    public int SourceTriangles { get; set; }

    public string Summary() => string.Create(CultureInfo.InvariantCulture,
        $"{MetresPerUnit:0.###} m/wu, native length {NativeLength:0.###} m, held scale x{Scale:0.###}, origin {OriginSource}" +
        $"{(Rotated180 ? ", turned 180° (muzzle was behind the grip)" : "")}{(WindingFlipped ? ", winding flipped to match normals" : "")}");
}

/// <summary>
/// Converts a Reclaimer render-model Scene into a .cthm-ready mesh in Minecraft space.
/// Per vertex: decompress (Mesh.PositionBounds / TextureBounds expansion), permutation transform
/// (ModelPermutation.GetFinalTransform: scale, rotation, translation, as Reclaimer's own RMF writer does), object
/// placement transform (ObjectPlacement.Transform), scene coordinate system → Halo default (X forward, Y left, Z up),
/// then Halo → Minecraft axes (x_mc, y_mc, z_mc) = (y, z, x) and world units → metres (1 wu = 3.048 m, from the
/// scene's UnitScale). Then: winding made consistent with the stored normals, barrel checked to point +Z, the mesh
/// scaled to held size, and the origin moved to the grip.
/// </summary>
public static class MeshBuilder
{
    public const float DefaultMetresPerUnit = 3.048f;
    public static readonly string[] GripMarkers = { "right_hand", "grip", "hand", "right_hand_grip", "primary_grip", "handle" };
    public static readonly string[] MuzzleMarkers = { "primary_trigger", "primary_barrel", "barrel", "muzzle", "primary_muzzle", "fp_primary_trigger" };
    public const string InstancesRegion = "<Instances>";      // Reclaimer BlamConstants.ModelInstancesGroupName
    public const string InstanceOwnersProperty = "permutation_name"; // Reclaimer BlamConstants.PermutationNamePropertyName

    /// <summary>Halo (x forward, y left, z up) → Minecraft (x left, y up, z forward). A proper rotation (det +1).</summary>
    public static Vector3 HaloToMc(Vector3 h) => new(h.Y, h.Z, h.X);

    public static BuiltMesh Build(Scene scene, MeshBuildOptions opt)
    {
        var notes = new List<string>();
        float mpu = scene.CoordinateSystem.UnitScale / 1000f;
        if (!float.IsFinite(mpu) || mpu < 0.01f || mpu > 100f)
        {
            notes.Add($"scene unit scale {scene.CoordinateSystem.UnitScale} mm/unit implausible; using 3.048 m/wu");
            mpu = DefaultMetresPerUnit;
        }
        var coord = CoordinateSystem.GetTransform(scene.CoordinateSystem, CoordinateSystem.Default, false);
        if (!coord.IsIdentity) notes.Add("scene uses a non-default coordinate system; converted to Halo default axes");

        var materials = new List<Material?>();
        var materialIds = new Dictionary<string, int>();
        var buckets = new List<Bucket>();
        var markerPositions = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        Vector3? handlePos = null;
        int skippedFx = 0, badIndices = 0, unsupportedLayouts = 0, sourceTris = 0, emptyMeshes = 0;
        int context = 0;

        foreach (var placement in Placements(scene.RootNode))
        {
            if (placement.Object is not Model model) continue;
            var placeXf = placement.Transform * coord;
            var chosen = ChoosePermutations(model, opt.VariantPermutations, notes);

            foreach (var (_, _, perm) in chosen)
            {
                var permXf = perm.GetFinalTransform();
                foreach (var mi in perm.MeshIndices)
                {
                    var mesh = model.Meshes.ElementAtOrDefault(mi);
                    if (mesh?.VertexBuffer is null || !mesh.VertexBuffer.HasPositions || mesh.IndexBuffer is null) { emptyMeshes++; continue; }
                    if (mesh.IndexBuffer.Layout is not (IndexFormat.Default or IndexFormat.TriangleStrip or IndexFormat.TriangleList))
                    {
                        unsupportedLayouts++;
                        continue;
                    }
                    context++;
                    var posXf = mesh.PositionBounds.CreateExpansionMatrix() * permXf * placeXf;
                    var nrmXf = permXf * placeXf;
                    if (Matrix4x4.Invert(nrmXf, out var inv)) nrmXf = Matrix4x4.Transpose(inv);
                    var uvXf = mesh.TextureBounds.CreateExpansionMatrix();
                    var pos = mesh.GetPositions().ToArray();
                    var uvs = mesh.GetTexCoords()?.ToArray();
                    var nrm = mesh.GetNormals()?.ToArray();

                    foreach (var seg in mesh.Segments)
                    {
                        var mat = seg.Material;
                        if (mat?.AlphaMode is AlphaMode.Add or AlphaMode.Multiply) { skippedFx++; continue; }
                        string key = mat is null ? "<none>" : mat.Id.ToString(CultureInfo.InvariantCulture) + ":" + mat.Name;
                        if (!materialIds.TryGetValue(key, out int matIndex))
                        {
                            matIndex = materials.Count;
                            materialIds[key] = matIndex;
                            materials.Add(mat);
                            buckets.Add(new Bucket());
                        }
                        var bucket = buckets[matIndex];
                        var tiling = Diffuse(mat)?.Tiling ?? Vector2.One;
                        if (!float.IsFinite(tiling.X) || !float.IsFinite(tiling.Y) || tiling.X == 0 || tiling.Y == 0) tiling = Vector2.One;

                        List<int> idx;
                        try { idx = mesh.GetTriangleIndicies(seg).ToList(); }
                        catch { badIndices++; continue; }
                        for (int t = 0; t + 2 < idx.Count; t += 3)
                        {
                            int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                            if ((uint)a >= pos.Length || (uint)b >= pos.Length || (uint)c >= pos.Length) { badIndices++; continue; }
                            sourceTris++;
                            bucket.Tris.Add((Vert(a), Vert(b), Vert(c)));
                        }

                        int Vert(int i)
                        {
                            if (bucket.Map.TryGetValue((context, i), out var o)) return o;
                            var p = HaloToMc(Vector3.Transform(pos[i], posXf)) * mpu;
                            var n = nrm != null && i < nrm.Length ? Vector3.TransformNormal(nrm[i], nrmXf) : Vector3.Zero;
                            n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(HaloToMc(n)) : Vector3.Zero;
                            var uv = uvs != null && i < uvs.Length ? Vector2.Transform(uvs[i], uvXf) * tiling : Vector2.Zero;
                            o = bucket.P.Count;
                            bucket.P.Add(p); bucket.N.Add(n); bucket.UV.Add(uv);
                            bucket.Map[(context, i)] = o;
                            return o;
                        }
                    }
                }
            }

            // markers (bone-relative) and the handle node, in the same space as the vertices
            foreach (var marker in model.Markers)
            {
                if (marker?.Name is null || markerPositions.ContainsKey(marker.Name)) continue;
                var inst = marker.Instances.FirstOrDefault(m => MarkerMatches(m, chosen)) ?? marker.Instances.FirstOrDefault();
                if (inst is null) continue;
                var bone = inst.BoneIndex >= 0 && inst.BoneIndex < model.Bones.Count ? SafeBoneWorld(model, inst.BoneIndex) : Matrix4x4.Identity;
                markerPositions[marker.Name] = HaloToMc(Vector3.Transform(Vector3.Transform(inst.Position, bone), placeXf)) * mpu;
            }
            if (opt.HandleNode is { Length: > 0 } hn && handlePos is null)
            {
                int bi = model.Bones.FindIndex(b => string.Equals(b.Name, hn, StringComparison.OrdinalIgnoreCase));
                if (bi >= 0) handlePos = HaloToMc(Vector3.Transform(SafeBoneWorld(model, bi).Translation, placeXf)) * mpu;
            }
        }

        if (skippedFx > 0) notes.Add($"{skippedFx} additive/multiply (effect) segments left out");
        if (badIndices > 0) notes.Add($"{badIndices} triangles with bad indices dropped");
        if (unsupportedLayouts > 0) notes.Add($"{unsupportedLayouts} meshes with non-triangle index layouts skipped");
        if (emptyMeshes > 0) notes.Add($"{emptyMeshes} empty mesh slots");

        var built = new BuiltMesh { Mesh = new MeshData(), Materials = materials, MetresPerUnit = mpu, SourceTriangles = sourceTris };
        built.Notes.AddRange(notes);
        if (buckets.All(b => b.Tris.Count == 0)) return built;

        // ---- winding: make the triangle order agree with the stored normals (area-weighted vote) ----
        double agree = 0, disagree = 0;
        foreach (var bk in buckets)
            foreach (var (a, b, c) in bk.Tris)
            {
                var g = Vector3.Cross(bk.P[b] - bk.P[a], bk.P[c] - bk.P[a]);
                var n = bk.N[a] + bk.N[b] + bk.N[c];
                if (n.LengthSquared() < 1e-12f || g.LengthSquared() < 1e-20f) continue;
                double d = Vector3.Dot(g, n);
                if (d > 0) agree += g.Length(); else disagree += g.Length();
            }
        bool flipWinding = disagree > agree;
        built.WindingFlipped = flipWinding;

        // fill normals that were missing from the face normal
        foreach (var bk in buckets)
        {
            var acc = new Vector3[bk.P.Count];
            foreach (var (a, b, c) in bk.Tris)
            {
                var g = Vector3.Cross(bk.P[b] - bk.P[a], bk.P[c] - bk.P[a]);
                if (flipWinding) g = -g;
                acc[a] += g; acc[b] += g; acc[c] += g;
            }
            for (int i = 0; i < bk.N.Count; i++)
                if (bk.N[i] == Vector3.Zero && acc[i].LengthSquared() > 1e-20f) bk.N[i] = Vector3.Normalize(acc[i]);
        }

        // ---- orientation, size and origin ----
        var (min, max) = Bounds(buckets);
        var centre = (min + max) / 2;

        // barrel must point +Z: if a muzzle marker sits behind the grip/centre, turn the mesh 180° about +Y
        var gripCandidate = PickOrigin(opt, markerPositions, handlePos, centre, out _);
        var muzzleName = MuzzleMarkers.FirstOrDefault(markerPositions.ContainsKey);
        bool turn = false;
        if (muzzleName != null && opt.Held != HeldClass.Grenade)
        {
            var muzzle = markerPositions[muzzleName];
            turn = muzzle.Z < gripCandidate.Z - 1e-4f && muzzle.Z < centre.Z;
        }
        if (turn)
        {
            static Vector3 Turn(Vector3 v) => new(-v.X, v.Y, -v.Z);
            foreach (var bk in buckets)
                for (int i = 0; i < bk.P.Count; i++) { bk.P[i] = Turn(bk.P[i]); bk.N[i] = Turn(bk.N[i]); }
            foreach (var k in markerPositions.Keys.ToList()) markerPositions[k] = Turn(markerPositions[k]);
            if (handlePos is { } hp) handlePos = Turn(hp);
            (min, max) = Bounds(buckets);
            centre = (min + max) / 2;
            built.Rotated180 = true;
        }

        float length = max.Z - min.Z;
        built.NativeLength = length;
        var (lo, hi) = opt.LengthRange;
        float scale = 1f;
        if (length > 1e-5f)
        {
            if (length < lo) scale = lo / length;
            else if (length > hi) scale = hi / length;
        }
        built.Scale = scale;

        var origin = PickOrigin(opt, markerPositions, handlePos, centre, out var originSource);
        built.OriginSource = originSource;
        built.Origin = origin;

        // ---- emit ----
        for (int m = 0; m < buckets.Count; m++)
        {
            var bk = buckets[m];
            if (bk.Tris.Count == 0) continue;
            var sub = new MeshData.Submesh { Material = $"{opt.MaterialPrefix}/{m}" };
            for (int i = 0; i < bk.P.Count; i++)
            {
                var p = (bk.P[i] - origin) * scale;
                var n = bk.N[i];
                sub.Vertices.AddRange(new[] { p.X, p.Y, p.Z, n.X, n.Y, n.Z, bk.UV[i].X, bk.UV[i].Y });
            }
            foreach (var (a, b, c) in bk.Tris)
            {
                sub.Indices.Add((uint)a);
                if (flipWinding) { sub.Indices.Add((uint)c); sub.Indices.Add((uint)b); }
                else { sub.Indices.Add((uint)b); sub.Indices.Add((uint)c); }
            }
            built.Mesh.Submeshes.Add(sub);
        }
        // submesh m keeps material index m even when an earlier bucket was empty
        if (built.Mesh.Submeshes.Count != buckets.Count)
        {
            var used = buckets.Select((b, i) => (b, i)).Where(x => x.b.Tris.Count > 0).Select(x => x.i).ToList();
            var remapped = used.Select(i => materials[i]).ToList();
            for (int s = 0; s < built.Mesh.Submeshes.Count; s++) built.Mesh.Submeshes[s].Material = $"{opt.MaterialPrefix}/{s}";
            built.Materials.Clear();
            built.Materials.AddRange(remapped);
        }
        return built;
    }

    private static Vector3 PickOrigin(MeshBuildOptions opt, Dictionary<string, Vector3> markers, Vector3? handle, Vector3 centre, out string source)
    {
        if (opt.Held != HeldClass.Grenade)
        {
            foreach (var name in GripMarkers)
                if (markers.TryGetValue(name, out var p)) { source = $"marker '{name}'"; return p; }
            if (handle is { } h) { source = $"handle node '{opt.HandleNode}'"; return h; }
        }
        source = "bounding-box centre";
        return centre;
    }

    private static Matrix4x4 SafeBoneWorld(Model model, int index)
    {
        try { return model.GetBoneWorldTransform(index); }
        catch { return Matrix4x4.Identity; }
    }

    private static bool MarkerMatches(MarkerInstance m, List<(int Region, int Perm, ModelPermutation P)> chosen)
    {
        if (m.RegionIndex < 0 || m.RegionIndex == 255) return true;
        if (m.PermutationIndex < 0 || m.PermutationIndex == 255) return chosen.Any(c => c.Region == m.RegionIndex);
        return chosen.Any(c => c.Region == m.RegionIndex && c.Perm == m.PermutationIndex);
    }

    /// <summary>
    /// One permutation per region: the default model variant's choice, else "default"/"base", else the first one with
    /// geometry. Instanced geometry is kept when one of its owner permutations was chosen (or it has no owners).
    /// </summary>
    public static List<(int Region, int Perm, ModelPermutation P)> ChoosePermutations(Model model, IReadOnlyDictionary<string, string>? variant, List<string> notes)
    {
        var chosen = new List<(int, int, ModelPermutation)>();
        var chosenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int r = 0; r < model.Regions.Count; r++)
        {
            var region = model.Regions[r];
            if (region is null || region.Name == InstancesRegion || region.Permutations.Count == 0) continue;
            int pick = -1;
            if (variant != null && region.Name != null && variant.TryGetValue(region.Name, out var vp))
                pick = region.Permutations.FindIndex(p => string.Equals(p.Name, vp, StringComparison.OrdinalIgnoreCase) && p.MeshIndices.Any());
            if (pick < 0) pick = region.Permutations.FindIndex(p => p.Name is "default" or "base" && p.MeshIndices.Any());
            if (pick < 0) pick = region.Permutations.FindIndex(p => p.MeshIndices.Any());
            if (pick < 0) continue;
            chosen.Add((r, pick, region.Permutations[pick]));
            chosenNames.Add($"{region.Name}\\{region.Permutations[pick].Name}");
            if (region.Permutations.Count > 1)
                notes.Add($"region '{region.Name}': permutation '{region.Permutations[pick].Name}' of {region.Permutations.Count}");
        }
        for (int r = 0; r < model.Regions.Count; r++)
        {
            var region = model.Regions[r];
            if (region?.Name != InstancesRegion) continue;
            for (int p = 0; p < region.Permutations.Count; p++)
            {
                var perm = region.Permutations[p];
                var owners = perm.CustomProperties.TryGetValue(InstanceOwnersProperty, out var o) && o is string[] arr ? arr : Array.Empty<string>();
                if (owners.Length == 0 || owners.Any(chosenNames.Contains)) chosen.Add((r, p, perm));
            }
        }
        return chosen;
    }

    public static TextureMapping? Diffuse(Material? m)
    {
        if (m is null) return null;
        return m.TextureMappings.FirstOrDefault(t => t.Usage == TextureUsage.Diffuse && t.BlendChannel == ChannelMask.Default && t.Texture != null)
            ?? m.TextureMappings.FirstOrDefault(t => t.Usage == TextureUsage.Diffuse && t.Texture != null)
            ?? m.TextureMappings.FirstOrDefault(t => t.Usage == TextureUsage.ColorChange && t.Texture != null);
    }

    private static IEnumerable<ObjectPlacement> Placements(SceneGroup group)
    {
        foreach (var o in group.ChildObjects) if (o.Export) yield return o;
        foreach (var g in group.ChildGroups)
            if (g.Export)
                foreach (var o in Placements(g)) yield return o;
    }

    private static (Vector3 Min, Vector3 Max) Bounds(List<Bucket> buckets)
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (var bk in buckets)
            if (bk.Tris.Count > 0)
                foreach (var p in bk.P) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        return (min, max);
    }

    private sealed class Bucket
    {
        public readonly List<Vector3> P = new(), N = new();
        public readonly List<Vector2> UV = new();
        public readonly List<(int, int, int)> Tris = new();
        public readonly Dictionary<(int, int), int> Map = new();
    }
}
