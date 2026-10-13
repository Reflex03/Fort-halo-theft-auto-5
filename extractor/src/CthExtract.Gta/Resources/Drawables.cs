using System.Numerics;

namespace CthExtract.Gta.Resources;

/// <summary>A shader of a drawable's shader group: its name hash and the parameters we care about.</summary>
public sealed class ShaderInfo
{
    public uint NameHash { get; init; }
    public uint FileNameHash { get; init; }
    /// <summary>Texture parameters: joaat(lower-case parameter name) -> texture name (null pointers skipped).</summary>
    public Dictionary<uint, string> Textures { get; } = new();
    /// <summary>Texture parameters that point at a full embedded texture (in the drawable's own dictionary).</summary>
    public Dictionary<uint, long> TextureBlocks { get; } = new();
    /// <summary>Vector parameters: hash -> 4*n floats.</summary>
    public Dictionary<uint, float[]> Vectors { get; } = new();
    public string? KnownName => ShaderNames.Lookup(NameHash) ?? ShaderNames.Lookup(FileNameHash);
    public override string ToString() => KnownName ?? $"0x{NameHash:X8}";
}

/// <summary>One geometry (draw call) with its vertices already in the model's object space.</summary>
public sealed class GeometryData
{
    public int ShaderIndex { get; init; }
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector2[] Uvs { get; init; }
    public required int[] Indices { get; init; }
    public int TriangleCount => Indices.Length / 3;
}

public sealed class ModelData
{
    public int BoneIndex { get; init; }
    public bool Skinned { get; init; }
    public List<GeometryData> Geometries { get; } = new();
    public int TriangleCount => Geometries.Sum(g => g.TriangleCount);
}

public sealed class BoneInfo
{
    public required string Name { get; init; }
    public ushort Tag { get; init; }
    public int Parent { get; init; } = -1;
    public Quaternion Rotation { get; init; } = Quaternion.Identity;
    public Vector3 Translation { get; init; }
    public Vector3 Scale { get; init; } = Vector3.One;
}

public sealed class DrawableData
{
    public string? Name { get; init; }
    public List<ShaderInfo> Shaders { get; } = new();
    /// <summary>LOD levels: 0 High, 1 Medium, 2 Low, 3 Very low. Missing levels are empty lists.</summary>
    public List<ModelData>[] Lods { get; } = { new(), new(), new(), new() };
    public List<TextureEntry> EmbeddedTextures { get; } = new();
    public List<BoneInfo> Bones { get; } = new();
    public List<string> Problems { get; } = new();
    public int TriangleCount(int lod) => Lods[lod].Sum(m => m.TriangleCount);
    public bool HasGeometry => Lods.Any(l => l.Count > 0 && l.Any(m => m.Geometries.Count > 0));
}

/// <summary>
/// Reads a legacy (gen8) PC drawable block: ShaderGroup (0x10), Skeleton (0x18), model lists High/Med/Low/VLow
/// (0x50..0x68) -> DrawableModel -> DrawableGeometry -> vertex / index buffers. Struct layouts from gta-toolkit's
/// Resources/PC/Drawables (MIT).
/// </summary>
public static class DrawableReader
{
    public static readonly string[] LodNames = { "High", "Medium", "Low", "VeryLow" };

    /// <param name="pose">object-space default pose per bone (the fragment's pose block); bones it does not cover use
    /// the drawable's own skeleton (local transforms multiplied up the parent chain), else identity.</param>
    public static DrawableData Read(Resource r, long drw, Matrix4x4[]? pose = null, string? name = null)
    {
        var d = new DrawableData { Name = name };
        long sg = r.Ptr(drw + 0x10);
        if (sg >= 0) ReadShaderGroup(r, sg, d);
        long skel = r.Ptr(drw + 0x18);
        if (skel >= 0)
        {
            try { ReadSkeleton(r, skel, d.Bones); }
            catch (InvalidDataException e) { d.Problems.Add($"skeleton: {e.Message}"); }
        }
        Matrix4x4? poseOf(int bone) => pose is not null && bone >= 0 && bone < pose.Length ? pose[bone] : BonePose(d.Bones, bone);

        for (int lod = 0; lod < 4; lod++)
        {
            long hdr = r.Ptr(drw + 0x50 + lod * 8);
            if (hdr < 0) continue;
            var (arr, count) = r.List(hdr);
            if (count > 512) { d.Problems.Add($"{LodNames[lod]}: implausible model count {count}"); continue; }
            for (int mi = 0; mi < count; mi++)
            {
                long model = r.Ptr(arr + mi * 8);
                if (model < 0) continue;
                try { d.Lods[lod].Add(ReadModel(r, model, poseOf, d.Problems, $"{LodNames[lod]}[{mi}]")); }
                catch (InvalidDataException e) { d.Problems.Add($"{LodNames[lod]}[{mi}]: {e.Message}"); }
            }
        }
        return d;
    }

    private static void ReadShaderGroup(Resource r, long sg, DrawableData d)
    {
        long td = r.Ptr(sg + 0x08);
        if (td >= 0)
        {
            try { d.EmbeddedTextures.AddRange(TextureDictionaryReader.Read(r, td, d.Problems)); }
            catch (InvalidDataException e) { d.Problems.Add($"embedded textures: {e.Message}"); }
        }
        long arr = r.Ptr(sg + 0x10);
        int count = r.U16(sg + 0x18);
        if (arr < 0 || count > 1024) return;
        for (int i = 0; i < count; i++)
        {
            long s = r.Ptr(arr + i * 8);
            if (s < 0) { d.Shaders.Add(new ShaderInfo()); continue; }
            d.Shaders.Add(ReadShader(r, s));
        }
    }

    public static ShaderInfo ReadShader(Resource r, long s)
    {
        var info = new ShaderInfo { NameHash = r.U32(s + 0x08), FileNameHash = r.U32(s + 0x18) };
        long prm = r.Ptr(s + 0x00);
        int count = r.U8(s + 0x10);
        if (prm < 0 || count == 0) return info;
        int embedded = 0;
        var types = new byte[count];
        var ptrs = new ulong[count];
        for (int i = 0; i < count; i++)
        {
            types[i] = r.U8(prm + i * 16);
            ptrs[i] = r.U64(prm + i * 16 + 8);
            embedded += 16 * types[i];
        }
        long hashes = prm + count * 16 + embedded;
        for (int i = 0; i < count; i++)
        {
            uint h = r.U32(hashes + i * 4);
            if (ptrs[i] == 0) continue;
            long p = r.Off(ptrs[i]);
            if (types[i] == 0)
            {
                var tn = r.CString(r.U64(p + 0x28));
                if (!string.IsNullOrEmpty(tn)) info.Textures[h] = tn;
                info.TextureBlocks[h] = p;
            }
            else
            {
                var f = new float[4 * types[i]];
                for (int k = 0; k < f.Length; k++) f[k] = r.F32(p + k * 4);
                info.Vectors[h] = f;
            }
        }
        return info;
    }

    private static void ReadSkeleton(Resource r, long sk, List<BoneInfo> bones)
    {
        long arr = r.Ptr(sk + 0x20);
        long parents = r.Ptr(sk + 0x38);
        int count = r.U16(sk + 0x5E);
        if (arr < 0 || count == 0 || count > 1024) return;
        for (int i = 0; i < count; i++)
        {
            long b = arr + i * 0x50;
            // parent: the skeleton's parent-index array (gta-toolkit layout), else the bone's own s16 at 0x32
            int p = parents >= 0 ? r.S16(parents + i * 2) : r.S16(b + 0x32);
            int parent = p >= 0 && p < count && p != i ? p : -1;
            bones.Add(new BoneInfo
            {
                Rotation = new Quaternion(r.F32(b), r.F32(b + 4), r.F32(b + 8), r.F32(b + 12)),
                Translation = new Vector3(r.F32(b + 0x10), r.F32(b + 0x14), r.F32(b + 0x18)),
                Scale = new Vector3(r.F32(b + 0x20), r.F32(b + 0x24), r.F32(b + 0x28)),
                Name = r.CString(r.U64(b + 0x38)) ?? $"bone{i}",
                Tag = r.U16(b + 0x44),
                Parent = parent,
            });
        }
    }

    /// <summary>Object-space default pose of a bone from the skeleton (local TRS multiplied up the parent chain).</summary>
    public static Matrix4x4? BonePose(List<BoneInfo> bones, int index)
    {
        if (index < 0 || index >= bones.Count) return null;
        var m = Matrix4x4.Identity;
        int guard = 0;
        for (int i = index; i >= 0 && guard++ < 256; i = bones[i].Parent)
        {
            var b = bones[i];
            var q = b.Rotation;
            if (!float.IsFinite(q.X) || q.LengthSquared() < 1e-6f) q = Quaternion.Identity; else q = Quaternion.Normalize(q);
            var local = Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(b.Translation);
            m *= local; // row vectors: v * child * parent * ...
        }
        return m;
    }

    private static ModelData ReadModel(Resource r, long model, Func<int, Matrix4x4?> poseOf, List<string> problems, string where)
    {
        long geoms = r.Ptr(model + 0x08);
        int gcount = r.U16(model + 0x10);
        long shmap = r.Ptr(model + 0x20);
        uint bind = r.U32(model + 0x28);
        int bone = (int)(bind >> 24);
        bool skinned = ((bind >> 8) & 0xFF) != 0;
        var m = new ModelData { BoneIndex = bone, Skinned = skinned };
        if (geoms < 0 || gcount > 256) return m;
        var xf = skinned ? Matrix4x4.Identity : poseOf(bone) ?? Matrix4x4.Identity;
        for (int gi = 0; gi < gcount; gi++)
        {
            long g = r.Ptr(geoms + gi * 8);
            if (g < 0) continue;
            int shader = shmap >= 0 ? r.U16(shmap + gi * 2) : 0;
            try
            {
                var geo = ReadGeometry(r, g, shader, xf);
                if (geo is not null) m.Geometries.Add(geo);
            }
            catch (InvalidDataException e) { problems.Add($"{where} geometry {gi}: {e.Message}"); }
        }
        return m;
    }

    // vertex component sizes in bytes. The common types (1 half2, 3 half4, 5-7 float2-4, 8 ubyte4, 9 colour,
    // 10 rgba8 snorm) are agreed on; the rare ones are read differently by different tools (gta-toolkit: 0 half,
    // 2 half3, 4 float; others: 0 none, 2 float, 4 none), so every reading is tried and the declared stride decides.
    private static readonly int[][] SizeCandidates =
    {
        new[] { 2, 0 }, new[] { 4 }, new[] { 6, 4 }, new[] { 8 }, new[] { 4, 0 }, new[] { 8 }, new[] { 12 }, new[] { 16 },
        new[] { 4 }, new[] { 4 }, new[] { 4 }, new[] { 4, 0 }, new[] { 4, 0 }, new[] { 4, 0 }, new[] { 4, 0 }, new[] { 4, 0 },
    };

    /// <summary>Byte offset of every semantic in a vertex for the declaration (flags, types), checked against stride.</summary>
    public static int[]? Layout(uint flags, ulong types, int stride, out int[] sizes)
    {
        var used = Enumerable.Range(0, 16).Where(s => ((flags >> s) & 1) != 0).ToArray();
        var choice = new int[16];
        sizes = new int[16];
        // try every combination of candidate sizes for the semantics in use (at most a handful are ambiguous)
        var ambiguous = used.Where(s => SizeCandidates[(int)((types >> (s * 4)) & 0xF)].Length > 1).ToArray();
        int combos = 1 << ambiguous.Length;
        for (int c = 0; c < combos; c++)
        {
            int total = 0;
            foreach (var s in used)
            {
                var cand = SizeCandidates[(int)((types >> (s * 4)) & 0xF)];
                int ai = Array.IndexOf(ambiguous, s);
                sizes[s] = cand.Length > 1 ? cand[(c >> ai) & 1] : cand[0];
                total += sizes[s];
            }
            if (total != stride && stride != 0) continue;
            var off = new int[16];
            int o = 0;
            for (int s = 0; s < 16; s++) { off[s] = ((flags >> s) & 1) != 0 ? o : -1; if (off[s] >= 0) o += sizes[s]; }
            return off;
        }
        return null;
    }

    private static GeometryData? ReadGeometry(Resource r, long g, int shader, Matrix4x4 xf)
    {
        long vb = r.Ptr(g + 0x18), ib = r.Ptr(g + 0x38);
        if (vb < 0 || ib < 0) return null;
        int stride = r.U16(vb + 0x08);
        long vdata = r.Ptr(vb + 0x10);
        if (vdata < 0) vdata = r.Ptr(vb + 0x20);
        int vcount = (int)r.U32(vb + 0x18);
        long decl = r.Ptr(vb + 0x30);
        if (vdata < 0 || decl < 0) throw new InvalidDataException("vertex buffer without data or declaration");
        if (vcount <= 0 || vcount > 65535 * 4) throw new InvalidDataException($"implausible vertex count {vcount}");
        uint flags = r.U32(decl);
        int declStride = r.U16(decl + 4);
        ulong types = r.U64(decl + 8);
        if (stride == 0) stride = declStride;
        var off = Layout(flags, types, stride, out var sizes)
                  ?? throw new InvalidDataException($"vertex declaration 0x{flags:X}/0x{types:X16} does not add up to stride {stride}");
        if (off[0] < 0) throw new InvalidDataException("vertex declaration has no position");
        if (vdata + (long)vcount * stride > r.Data.Length) throw new InvalidDataException("vertex data out of range");
        int T(int s) => (int)((types >> (s * 4)) & 0xF);

        var pos = new Vector3[vcount];
        var nrm = new Vector3[vcount];
        var uv = new Vector2[vcount];
        var nxf = xf with { M41 = 0, M42 = 0, M43 = 0 };
        for (int v = 0; v < vcount; v++)
        {
            long b = vdata + (long)v * stride;
            var p = ReadVec(r, b + off[0], T(0), sizes[0]);
            pos[v] = Vector3.Transform(new Vector3(p.X, p.Y, p.Z), xf);
            if (off[3] >= 0)
            {
                var n = ReadVec(r, b + off[3], T(3), sizes[3], normal: true);
                var nn = Vector3.TransformNormal(new Vector3(n.X, n.Y, n.Z), nxf);
                nrm[v] = nn.LengthSquared() > 1e-12f ? Vector3.Normalize(nn) : Vector3.UnitZ;
            }
            else nrm[v] = Vector3.UnitZ;
            if (off[6] >= 0)
            {
                var t = ReadVec(r, b + off[6], T(6), sizes[6]);
                uv[v] = new Vector2(t.X, t.Y);
            }
        }

        int icount = (int)r.U32(ib + 0x08);
        long idata = r.Ptr(ib + 0x10);
        if (idata < 0 || icount < 0 || icount % 3 != 0 || idata + icount * 2L > r.Data.Length)
            throw new InvalidDataException($"bad index buffer (count {icount})");
        var idx = new int[icount];
        for (int i = 0; i < icount; i++)
        {
            int k = r.U16(idata + i * 2);
            if (k >= vcount) throw new InvalidDataException($"index {k} >= vertex count {vcount}");
            idx[i] = k;
        }
        foreach (var p in pos)
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z) || MathF.Abs(p.X) > 1e4f || MathF.Abs(p.Y) > 1e4f || MathF.Abs(p.Z) > 1e4f)
                throw new InvalidDataException("vertex position not finite or out of range");
        return new GeometryData { ShaderIndex = shader, Positions = pos, Normals = nrm, Uvs = uv, Indices = idx };
    }

    private static Vector4 ReadVec(Resource r, long o, int type, int size, bool normal = false)
    {
        float H(long a) => (float)BitConverter.UInt16BitsToHalf(r.U16(a));
        switch (type)
        {
            case 0: return size == 2 ? new Vector4(H(o), 0, 0, 0) : Vector4.Zero;
            case 1: return new Vector4(H(o), H(o + 2), 0, 0);
            case 2: return size == 6 ? new Vector4(H(o), H(o + 2), H(o + 4), 0) : new Vector4(r.F32(o), 0, 0, 0);
            case 3: return new Vector4(H(o), H(o + 2), H(o + 4), H(o + 6));
            case 4: return size == 4 ? new Vector4(r.F32(o), 0, 0, 0) : Vector4.Zero;
            case 5: return new Vector4(r.F32(o), r.F32(o + 4), 0, 0);
            case 6: return new Vector4(r.F32(o), r.F32(o + 4), r.F32(o + 8), 0);
            case 7: return new Vector4(r.F32(o), r.F32(o + 4), r.F32(o + 8), r.F32(o + 12));
            case 10: // RGBA8 SNORM
                return new Vector4((sbyte)r.U8(o) / 127f, (sbyte)r.U8(o + 1) / 127f, (sbyte)r.U8(o + 2) / 127f, (sbyte)r.U8(o + 3) / 127f);
            case 8:
            case 9: // UBYTE4 / colour (UNORM); as a normal map 0..1 to -1..1
            {
                var c = new Vector4(r.U8(o), r.U8(o + 1), r.U8(o + 2), r.U8(o + 3)) / 255f;
                return normal ? c * 2 - Vector4.One : c;
            }
            default: return Vector4.Zero;
        }
    }
}

/// <summary>Names for the shader-name hashes we treat specially (joaat of the lower-case name).</summary>
public static class ShaderNames
{
    private static readonly Dictionary<uint, string> Known = BuildKnown();

    private static Dictionary<uint, string> BuildKnown()
    {
        var names = new List<string>
        {
            "vehicle_mesh", "vehicle_mesh_enveff", "vehicle_mesh2_enveff", "vehicle_tire", "vehicle_tire_emissive", "vehicle_vehglass",
            "vehicle_vehglass_inner", "vehicle_shuts", "vehicle_interior", "vehicle_interior2", "vehicle_badges", "vehicle_decal",
            "vehicle_decal2", "vehicle_detail", "vehicle_detail2", "vehicle_dash_emissive", "vehicle_dash_emissive_opaque",
            "vehicle_lights", "vehicle_lightsemissive", "vehicle_licenseplate", "vehicle_basic", "vehicle_blurredrotor",
            "vehicle_blurredrotor_emissive", "vehicle_cloth", "vehicle_cloth2", "vehicle_cutout", "vehicle_emissive_alpha",
            "vehicle_emissive_opaque", "vehicle_generic", "vehicle_track", "vehicle_track_ammo", "vehicle_track2",
            "vehicle_track2_emissive", "vehicle_track_cutout", "vehicle_track_emissive", "vehicle_track_siren",
            "vehicle_mesh_alpha_enveff",
        };
        foreach (var n in new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9" })
            foreach (var suffix in new[] { "", "_enveff", "_lvr", "_emissive", "_enveff_lvr", "_lvr_enveff" })
                names.Add("vehicle_paint" + n + suffix);
        var d = new Dictionary<uint, string>();
        foreach (var n in names)
        {
            d.TryAdd(Hashes.Joaat(n), n);
            d.TryAdd(Hashes.Joaat(n + ".sps"), n);
        }
        return d;
    }

    public static string? Lookup(uint hash) => Known.TryGetValue(hash, out var n) ? n : null;
}

/// <summary>Shader parameter name hashes (joaat of the lower-case parameter name).</summary>
public static class ShaderParams
{
    public static readonly uint DiffuseSampler = Hashes.Joaat("diffusesampler");
    public static readonly uint DiffuseSampler2 = Hashes.Joaat("diffusesampler2");
    public static readonly uint DiffuseSampler3 = Hashes.Joaat("diffusesampler3");
    public static readonly uint BumpSampler = Hashes.Joaat("bumpsampler");
    public static readonly uint SpecSampler = Hashes.Joaat("specsampler");
    public static readonly uint DirtSampler = Hashes.Joaat("dirtsampler");
    public static readonly uint DiffuseColor = Hashes.Joaat("matdiffusecolor");
    public static readonly uint DiffuseTex = Hashes.Joaat("diffusetex");
    public static readonly uint TextureSampler = Hashes.Joaat("texturesampler");
}
