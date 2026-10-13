using System.Numerics;
using System.Text;
using CthExtract.Core;
using CthExtract.Gta.Resources;

namespace CthExtract.Gta.Vehicles;

/// <summary>Default body colours baked into paint-shader materials (GTA picks paint at run time from carcols).</summary>
public static class VehiclePaint
{
    public readonly record struct Colour(byte R, byte G, byte B);

    private static readonly Dictionary<string, (Colour primary, Colour secondary)> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["police"] = (new(236, 236, 236), new(16, 16, 18)),     // LSPD black-and-white
        ["infernus"] = (new(176, 18, 16), new(20, 20, 20)),
        ["sultan"] = (new(28, 64, 150), new(20, 20, 20)),
        ["blista"] = (new(60, 130, 60), new(20, 20, 20)),
    };

    public static (Colour primary, Colour secondary) For(string model) =>
        Table.TryGetValue(model, out var c) ? c : (new Colour(150, 150, 150), new Colour(20, 20, 20));

    /// <summary>vehicle_paint2* take the secondary colour, every other vehicle_paint* shader the primary [inference].</summary>
    public static bool IsSecondary(string shader) => shader.StartsWith("vehicle_paint2", StringComparison.Ordinal);
    public static bool IsPaint(string? shader) => shader is not null && shader.StartsWith("vehicle_paint", StringComparison.Ordinal);
}

/// <summary>
/// Turns a vehicle fragment into a .cthm-ready mesh: body from the main drawable (bone-posed models), wheels from the
/// physics-LOD children selected by bone tag (right side rotated 180 degrees about the forward axis), a LOD near the
/// triangle budget, GTA (metres, Z up, +Y forward) to Minecraft (+Y up, +Z forward): mc = (-x, z, y); centred in X/Z
/// with the lowest point at y = 0. Materials get one texture each.
/// </summary>
public sealed class VehicleBuilder
{
    public sealed class Result
    {
        public required MeshData Mesh { get; init; }
        public Dictionary<string, Rgba> Textures { get; } = new();
        public int Lod { get; init; }
        public List<string> Notes { get; } = new();
    }

    private sealed record WheelDef(string Bone, string Node, bool Right, bool Front);

    public static readonly IReadOnlyList<(string bone, string node, bool right, bool front)> WheelBones = new[]
    {
        ("wheel_lf", "wheel_lf", false, true), ("wheel_rf", "wheel_rf", true, true),
        ("wheel_lr", "wheel_lb", false, false), ("wheel_rr", "wheel_rb", true, false),
        ("wheel_lm1", "wheel_lm1", false, false), ("wheel_rm1", "wheel_rm1", true, false),
        ("wheel_lm2", "wheel_lm2", false, false), ("wheel_rm2", "wheel_rm2", true, false),
        ("wheel_lm3", "wheel_lm3", false, false), ("wheel_rm3", "wheel_rm3", true, false),
    };

    private readonly string _model;
    private readonly FragmentData _frag;
    private readonly Func<string, TextureEntry?> _findTexture;
    private readonly int _budget;
    private readonly Log _log;

    /// <param name="findTexture">texture name -> texture (search order: embedded, model ytd, parents, vehshare).</param>
    public VehicleBuilder(string model, FragmentData frag, Func<string, TextureEntry?> findTexture, int budget, Log log)
    {
        _model = model.ToLowerInvariant(); _frag = frag; _findTexture = findTexture; _budget = budget; _log = log;
    }

    private sealed class Part
    {
        public required DrawableData Drawable;
        public required List<ModelData> Models;
        public Matrix4x4 Transform = Matrix4x4.Identity;
        public int Node = -1;
        public string Source = "";
    }

    public Result Build()
    {
        var main = _frag.Drawable;
        var bonesByTag = new Dictionary<ushort, int>();
        for (int i = 0; i < main.Bones.Count; i++) bonesByTag.TryAdd(main.Bones[i].Tag, i);

        int lod = ChooseLod(out var lodNote);
        var notes = new List<string> { lodNote };
        var mesh = new MeshData();
        var parts = new List<Part>();

        // nodes: every wheel that exists as a physics child or a skeleton bone
        var wheelNode = new Dictionary<ushort, int>();
        var wheelPivot = new Dictionary<ushort, Matrix4x4>();
        foreach (var (bone, node, right, _) in WheelBones)
        {
            ushort tag = Hashes.BoneTag(bone);
            var child = _frag.Children.FirstOrDefault(c => c.BoneTag == tag);
            Matrix4x4? placement = child?.Transform;
            if (placement is null && bonesByTag.TryGetValue(tag, out var bi))
                placement = bi < _frag.BonePose.Length ? _frag.BonePose[bi] : DrawableReader.BonePose(main.Bones, bi);
            if (placement is null) continue;
            var p = placement.Value;
            if (right) p = new Matrix4x4(-1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1, 0, p.M41, p.M42, p.M43, 1);
            wheelNode[tag] = mesh.Nodes.Count;
            wheelPivot[tag] = p;
            var mc = ToMc(new Vector3(p.M41, p.M42, p.M43));
            mesh.Nodes.Add(new MeshData.Node { Name = node, X = mc.X, Y = mc.Y, Z = mc.Z });
        }

        // body: main drawable models at the chosen LOD; models bound to a wheel bone become that wheel's part
        var wheelBoneIndex = wheelNode.Keys.Where(bonesByTag.ContainsKey).ToDictionary(t => bonesByTag[t], t => t);
        var mainModels = Lod(main, lod);
        var wheelsInMain = new HashSet<ushort>();
        foreach (var m in mainModels)
        {
            int node = -1;
            if (!m.Skinned && wheelBoneIndex.TryGetValue(m.BoneIndex, out var wt)) { node = wheelNode[wt]; wheelsInMain.Add(wt); }
            parts.Add(new Part { Drawable = main, Models = new() { m }, Node = node, Source = "main" });
        }
        var bonesWithGeometry = mainModels.Where(m => !m.Skinned && m.Geometries.Count > 0).Select(m => m.BoneIndex).ToHashSet();

        // wheels from the physics children; empty children reuse the front / rear wheel drawable
        DrawableData? Has(ushort tag) => _frag.Children.FirstOrDefault(c => c.BoneTag == tag && c.Drawable is { HasGeometry: true })?.Drawable;
        var front = Has(Hashes.BoneTag("wheel_lf")) ?? Has(Hashes.BoneTag("wheel_rf"));
        var rear = Has(Hashes.BoneTag("wheel_lr")) ?? Has(Hashes.BoneTag("wheel_rr")) ?? front;
        front ??= rear;
        foreach (var (bone, _, _, isFront) in WheelBones)
        {
            ushort tag = Hashes.BoneTag(bone);
            if (!wheelNode.TryGetValue(tag, out var node) || wheelsInMain.Contains(tag)) continue;
            var d = Has(tag) ?? (isFront ? front : rear);
            if (d is null) continue;
            parts.Add(new Part { Drawable = d, Models = Lod(d, lod), Transform = wheelPivot[tag], Node = node, Source = bone });
        }
        if (front is null && wheelsInMain.Count == 0) notes.Add("no wheel geometry found (pivots only)");

        // other children with geometry (detachable parts) unless the main drawable already has that bone's geometry
        var wheelTags = WheelBones.Select(w => Hashes.BoneTag(w.bone)).ToHashSet();
        foreach (var c in _frag.Children)
        {
            if (wheelTags.Contains(c.BoneTag) || c.Drawable is not { HasGeometry: true } || c.Transform is null) continue;
            if (bonesByTag.TryGetValue(c.BoneTag, out var bi) && bonesWithGeometry.Contains(bi)) continue;
            parts.Add(new Part { Drawable = c.Drawable, Models = Lod(c.Drawable, lod), Transform = c.Transform.Value, Source = $"child{c.Index}" });
        }

        var textures = new Dictionary<string, Rgba>();
        var groups = new Dictionary<(string material, int node), MeshData.Submesh>();
        var materialLog = new SortedDictionary<string, string>();
        foreach (var part in parts)
        {
            var nxf = part.Transform with { M41 = 0, M42 = 0, M43 = 0 };
            foreach (var model in part.Models)
                foreach (var g in model.Geometries)
                {
                    var shader = g.ShaderIndex < part.Drawable.Shaders.Count ? part.Drawable.Shaders[g.ShaderIndex] : null;
                    var material = Material(shader, part.Drawable, textures, materialLog);
                    var key = (material, part.Node);
                    if (!groups.TryGetValue(key, out var sub)) groups[key] = sub = new MeshData.Submesh { Material = material, Node = part.Node };
                    uint baseIndex = (uint)sub.VertexCount;
                    for (int v = 0; v < g.Positions.Length; v++)
                    {
                        var p = ToMc(Vector3.Transform(g.Positions[v], part.Transform));
                        var n = ToMc(Vector3.TransformNormal(g.Normals[v], nxf));
                        if (n.LengthSquared() > 1e-12f) n = Vector3.Normalize(n); else n = Vector3.UnitY;
                        sub.Vertices.AddRange(new[] { p.X, p.Y, p.Z, n.X, n.Y, n.Z, g.Uvs[v].X, g.Uvs[v].Y });
                    }
                    foreach (var i in g.Indices) sub.Indices.Add(baseIndex + (uint)i);
                }
        }
        mesh.Submeshes.AddRange(groups.Values.Where(s => s.Indices.Count > 0));
        Normalise(mesh);
        foreach (var (m, how) in materialLog) _log.Info($"[gta] {_model}: material {m} <- {how}");
        notes.Add($"{mesh.Submeshes.Count} submeshes, {mesh.Nodes.Count} wheel nodes, parts: {string.Join(",", parts.Select(p => p.Source).Distinct())}");
        var result = new Result { Mesh = mesh, Lod = lod };
        foreach (var (k, v) in textures) result.Textures[k] = v;
        result.Notes.AddRange(notes);
        return result;
    }

    private static List<ModelData> Lod(DrawableData d, int lod)
    {
        for (int l = lod; l < 4; l++) if (d.Lods[l].Count > 0) return d.Lods[l];
        for (int l = lod - 1; l >= 0; l--) if (d.Lods[l].Count > 0) return d.Lods[l];
        return new List<ModelData>();
    }

    /// <summary>The most detailed LOD whose triangle count (body + 4 wheels) is within 1.25x the budget; if none, the
    /// smallest. Decimation then trims the rest.</summary>
    private int ChooseLod(out string note)
    {
        var main = _frag.Drawable;
        var wheel = _frag.Children.FirstOrDefault(c => c.Drawable is { HasGeometry: true } && WheelBones.Any(w => Hashes.BoneTag(w.bone) == c.BoneTag))?.Drawable;
        int wheels = Math.Max(4, _frag.Children.Count(c => WheelBones.Any(w => Hashes.BoneTag(w.bone) == c.BoneTag)));
        var totals = new int[4];
        for (int l = 0; l < 4; l++)
            totals[l] = main.Lods[l].Count == 0 ? -1 : main.TriangleCount(l) + (wheel is null ? 0 : wheels * Lod(wheel, l).Sum(m => m.TriangleCount));
        int pick = -1;
        for (int l = 0; l < 4; l++) if (totals[l] > 0 && totals[l] <= _budget * 1.25) { pick = l; break; }
        if (pick < 0)
        {
            int best = int.MaxValue;
            for (int l = 0; l < 4; l++) if (totals[l] > 0 && totals[l] < best) { best = totals[l]; pick = l; }
        }
        if (pick < 0) pick = 0;
        note = $"LOD {DrawableReader.LodNames[pick]} (tris per LOD: {string.Join(" / ", totals.Select((t, i) => $"{DrawableReader.LodNames[i]} {(t < 0 ? "-" : t.ToString())}"))}, budget {_budget})";
        return pick;
    }

    /// <summary>GTA model space (x right, y forward, z up) to Minecraft (x, y up, z forward): (-x, z, y). A proper
    /// rotation, so triangle winding is kept.</summary>
    public static Vector3 ToMc(Vector3 g) => new(-g.X, g.Z, g.Y);

    /// <summary>Centre X/Z on the bounding box and put the lowest point at y = 0 (nodes move with the mesh).</summary>
    public static void Normalise(MeshData mesh)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var s in mesh.Submeshes)
            for (int i = 0; i < s.Vertices.Count; i += 8)
            {
                minX = Math.Min(minX, s.Vertices[i]); maxX = Math.Max(maxX, s.Vertices[i]);
                minY = Math.Min(minY, s.Vertices[i + 1]);
                minZ = Math.Min(minZ, s.Vertices[i + 2]); maxZ = Math.Max(maxZ, s.Vertices[i + 2]);
            }
        if (minX == float.MaxValue) return;
        float cx = (minX + maxX) / 2, cz = (minZ + maxZ) / 2;
        foreach (var s in mesh.Submeshes)
            for (int i = 0; i < s.Vertices.Count; i += 8)
            {
                s.Vertices[i] -= cx; s.Vertices[i + 1] -= minY; s.Vertices[i + 2] -= cz;
            }
        foreach (var n in mesh.Nodes) { n.X -= cx; n.Y -= minY; n.Z -= cz; }
    }

    private string Material(ShaderInfo? shader, DrawableData owner, Dictionary<string, Rgba> textures, IDictionary<string, string> log)
    {
        string shaderName = shader?.KnownName ?? (shader is null ? "none" : $"0x{shader.NameHash:X8}");
        string? diffuseName = null;
        if (shader is not null)
            foreach (var p in new[] { ShaderParams.DiffuseSampler, ShaderParams.TextureSampler, ShaderParams.DiffuseTex, ShaderParams.DiffuseSampler2 })
                if (shader.Textures.TryGetValue(p, out var n)) { diffuseName = n; break; }

        if (VehiclePaint.IsPaint(shader?.KnownName))
        {
            var (pri, sec) = VehiclePaint.For(_model);
            bool secondary = VehiclePaint.IsSecondary(shader!.KnownName!);
            var colour = secondary ? sec : pri;
            string name = $"gta/{_model}/paint_{(secondary ? "secondary" : "primary")}" + (diffuseName is null ? "" : "_" + Safe(diffuseName));
            if (textures.ContainsKey(name)) return name;
            var tex = diffuseName is null ? null : Lookup(diffuseName, owner);
            textures[name] = tex is null ? Solid(colour, 16) : Tint(TextureDecoder.Decode(tex), colour);
            log[name] = $"{shaderName}: {(secondary ? "secondary" : "primary")} paint rgb({colour.R},{colour.G},{colour.B})" + (tex is null ? "" : $" over {tex}");
            return name;
        }

        if (diffuseName is not null)
        {
            string name = $"gta/{_model}/{Safe(diffuseName)}";
            bool keepAlpha = shaderName.Contains("decal") || shaderName.Contains("badges") || shaderName.Contains("cutout") || shaderName.Contains("emissive_alpha");
            bool glass = shaderName.Contains("vehglass");
            name += glass ? "_glass" : keepAlpha ? "_alpha" : "";
            if (textures.ContainsKey(name)) return name;
            var tex = Lookup(diffuseName, owner);
            if (tex is not null)
            {
                try
                {
                    var img = TextureDecoder.Decode(tex);
                    if (glass) img = Glass(img);
                    else if (!keepAlpha) img = Images.Opaque(img);
                    textures[name] = img;
                    log[name] = $"{shaderName}: {tex}{(keepAlpha ? " (alpha kept)" : "")}";
                    return name;
                }
                catch (Exception e) when (e is NotSupportedException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
                {
                    log[name] = $"{shaderName}: {diffuseName} could not be decoded ({e.Message}); grey";
                }
            }
            else log[name] = $"{shaderName}: texture '{diffuseName}' not found in any dictionary; grey";
            textures[name] = Solid(new VehiclePaint.Colour(128, 128, 128), 16);
            return name;
        }

        // no diffuse texture: matDiffuseColor if present, else neutral grey
        string fallback = $"gta/{_model}/flat_{Safe(shaderName)}";
        if (!textures.ContainsKey(fallback))
        {
            var c = new VehiclePaint.Colour(110, 110, 110);
            if (shader is not null && shader.Vectors.TryGetValue(ShaderParams.DiffuseColor, out var v) && v.Length >= 3)
                c = new((byte)Math.Clamp(v[0] * 255, 0, 255), (byte)Math.Clamp(v[1] * 255, 0, 255), (byte)Math.Clamp(v[2] * 255, 0, 255));
            textures[fallback] = Solid(c, 16);
            log[fallback] = $"{shaderName}: no diffuse texture; flat rgb({c.R},{c.G},{c.B})";
        }
        return fallback;
    }

    private TextureEntry? Lookup(string name, DrawableData owner)
    {
        var t = owner.EmbeddedTextures.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? _frag.Drawable.EmbeddedTextures.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return t ?? _findTexture(name);
    }

    public static string Safe(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.ToLowerInvariant()) sb.Append(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.' ? ch : '_');
        return sb.Length == 0 ? "x" : sb.ToString();
    }

    public static Rgba Solid(VehiclePaint.Colour c, int size)
    {
        var px = new byte[size * size * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = c.R; px[i + 1] = c.G; px[i + 2] = c.B; px[i + 3] = 255; }
        return new Rgba(size, size, px);
    }

    /// <summary>Paint over a paint-shader diffuse: alpha-blend the texture's decals over the paint, opaque result.</summary>
    public static Rgba Tint(Rgba tex, VehiclePaint.Colour c)
    {
        var px = (byte[])tex.Pixels.Clone();
        bool anyTransparent = false;
        for (int i = 3; i < px.Length; i += 4) if (px[i] < 250) { anyTransparent = true; break; }
        for (int i = 0; i < px.Length; i += 4)
        {
            if (anyTransparent)
            {
                float a = px[i + 3] / 255f;
                px[i] = (byte)(px[i] * a + c.R * (1 - a)); px[i + 1] = (byte)(px[i + 1] * a + c.G * (1 - a)); px[i + 2] = (byte)(px[i + 2] * a + c.B * (1 - a));
            }
            else
            {
                px[i] = (byte)(px[i] * c.R / 255); px[i + 1] = (byte)(px[i + 1] * c.G / 255); px[i + 2] = (byte)(px[i + 2] * c.B / 255);
            }
            px[i + 3] = 255;
        }
        return tex with { Pixels = px };
    }

    /// <summary>Windows: Minecraft entity cut-out rendering would drop low-alpha glass, so make it an opaque dark tint.</summary>
    public static Rgba Glass(Rgba tex)
    {
        var px = (byte[])tex.Pixels.Clone();
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = (byte)(px[i] * 0.45f + 10); px[i + 1] = (byte)(px[i + 1] * 0.45f + 12); px[i + 2] = (byte)(px[i + 2] * 0.45f + 16);
            px[i + 3] = 255;
        }
        return tex with { Pixels = px };
    }
}
