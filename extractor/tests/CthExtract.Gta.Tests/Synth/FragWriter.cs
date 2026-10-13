using System.Numerics;

namespace CthExtract.Gta.Tests.Synth;

public sealed class SynthMesh
{
    public List<Vector3> P = new(), N = new();
    public List<Vector2> UV = new();
    public List<int> I = new();
    public int Tris => I.Count / 3;

    /// <summary>Axis-aligned box with outward normals; each face split into n x n quads (2 triangles each), CCW seen
    /// from outside (right-handed).</summary>
    public static SynthMesh Box(Vector3 min, Vector3 max, int n = 1)
    {
        var m = new SynthMesh();
        var c = (min + max) / 2;
        // faces: normal, u axis, v axis (u x v = normal)
        var faces = new (Vector3 nrm, Vector3 u, Vector3 v)[]
        {
            (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ), (-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
            (Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX), (-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
            (Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY), (-Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX),
        };
        var half = (max - min) / 2;
        foreach (var (nrm, u, v) in faces)
        {
            int b = m.P.Count;
            for (int j = 0; j <= n; j++)
                for (int i = 0; i <= n; i++)
                {
                    float fu = -1 + 2f * i / n, fv = -1 + 2f * j / n;
                    var p = c + nrm * half + u * half * fu + v * half * fv;
                    m.P.Add(p); m.N.Add(nrm); m.UV.Add(new Vector2((float)i / n, (float)j / n));
                }
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int a = b + j * (n + 1) + i, a1 = a + 1, a2 = a + n + 1, a3 = a2 + 1;
                    m.I.AddRange(new[] { a, a1, a3, a, a3, a2 });
                }
        }
        return m;
    }

    public SynthMesh Append(SynthMesh o)
    {
        int b = P.Count;
        P.AddRange(o.P); N.AddRange(o.N); UV.AddRange(o.UV); I.AddRange(o.I.Select(i => i + b));
        return this;
    }
}

public sealed class SynthShader
{
    public required string Name;
    public Dictionary<string, string> Textures = new();     // param name -> texture name
    public Dictionary<string, float[]> Vectors = new();     // param name -> floats (multiple of 4)
}

public sealed class SynthGeometry
{
    public required SynthMesh Mesh;
    public int Shader;
    /// <summary>0 = pos/normal/uv float; 1 = half2 uv; 2 = with a colour0 channel before the uv.</summary>
    public int VertexFormat;
}

public sealed class SynthModel
{
    public int Bone;
    public bool Skinned;
    public List<SynthGeometry> Geoms = new();
}

public sealed class SynthBone
{
    public required string Name;
    public int Parent = -1;
    public Vector3 T;
    public Quaternion R = Quaternion.Identity;
}

public sealed class SynthDrawable
{
    public List<SynthShader> Shaders = new();
    public List<SynthModel>[] Lods = { new(), new(), new(), new() };
    public List<SynthTexture> Embedded = new();
    public List<SynthBone> Bones = new();
}

public sealed class SynthChild
{
    public required string Bone;
    public Matrix4x4 Transform = Matrix4x4.Identity;
    public SynthDrawable? Drawable;
}

public sealed class SynthFrag
{
    public string Name = "synth";
    public required SynthDrawable Main;
    public Matrix4x4[] Pose = Array.Empty<Matrix4x4>();
    public List<SynthChild> Children = new();
    public Vector3 PositionOffset;
}

/// <summary>Our own writer for gen8 fragments (.yft) and drawables, using the struct layouts our reader expects
/// (gta-toolkit, MIT). Only what the reader touches is filled in.</summary>
public static class FragWriter
{
    public const ulong GtaV1Types = 0x7755555555996996;

    public static byte[] Yft(SynthFrag f, int version = 162)
    {
        var rb = new ResourceBuilder();
        var root = rb.Alloc(0x130);
        var name = rb.String(f.Name);
        var drw = WriteDrawable(rb, f.Main);
        ulong posePtr = 0;
        if (f.Pose.Length > 0)
        {
            var pb = new Blk(0x20 + 48 * f.Pose.Length).U8(0x10, f.Pose.Length).U8(0x11, f.Pose.Length);
            for (int i = 0; i < f.Pose.Length; i++)
            {
                var m = f.Pose[i];
                float[,] rows =
                {
                    { m.M11, m.M21, m.M31, m.M41 },
                    { m.M12, m.M22, m.M32, m.M42 },
                    { m.M13, m.M23, m.M33, m.M43 },
                };
                for (int r = 0; r < 3; r++) for (int c = 0; c < 4; c++) pb.F32(0x20 + i * 48 + r * 16 + c * 4, rows[r, c]);
            }
            posePtr = rb.Add(pb.B).Ptr;
        }
        ulong groupPtr = 0;
        if (f.Children.Count > 0)
        {
            var childRefs = new List<ulong>();
            foreach (var c in f.Children)
            {
                ulong cd = c.Drawable is null ? 0 : WriteDrawable(rb, c.Drawable).Ptr;
                var cb = new Blk(0x100).U32(0, 0xC1).U32(4, 1).U16(0x12, CthExtract.Gta.Hashes.BoneTag(c.Bone)).U64(0xA0, cd);
                childRefs.Add(rb.Add(cb.B).Ptr);
            }
            var arr = new Blk(8 * childRefs.Count);
            for (int i = 0; i < childRefs.Count; i++) arr.U64(i * 8, childRefs[i]);
            var arrRef = rb.Add(arr.B);
            var xf = new Blk(0x20 + 64 * f.Children.Count).U32(0x10, (uint)f.Children.Count);
            for (int i = 0; i < f.Children.Count; i++)
            {
                var m = f.Children[i].Transform;
                float[] v = { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 };
                for (int k = 0; k < 16; k++) xf.F32(0x20 + i * 64 + k * 4, v[k]);
            }
            var xfRef = rb.Add(xf.B);
            var lod = new Blk(0x130).U32(0, 0x10D).U32(4, 1)
                .Vec3(0x30, f.PositionOffset)
                .U64(0xD0, arrRef.Ptr).U64(0x100, xfRef.Ptr).U8(0x11D, f.Children.Count);
            var lodRef = rb.Add(lod.B);
            groupPtr = rb.Add(new Blk(0x30).U32(0, 0x6E0).U32(4, 1).U64(0x10, lodRef.Ptr).B).Ptr;
        }
        var ft = new Blk(0x130).U32(0, 0xF7A6).U32(4, 1)
            .U64(0x30, drw.Ptr).U64(0x58, name.Ptr).U64(0xA8, posePtr).U64(0xF0, groupPtr);
        rb.Put(root, ft.B);
        return rb.Build(version);
    }

    public static ResourceBuilder.Ref WriteDrawable(ResourceBuilder rb, SynthDrawable d)
    {
        var block = rb.Alloc(0x150);
        var b = new Blk(0x150).U32(0, 0xD7A0).U32(4, 1);

        // embedded textures + shader group
        var embedded = new Dictionary<string, ResourceBuilder.Ref>(StringComparer.OrdinalIgnoreCase);
        ulong dictPtr = 0;
        if (d.Embedded.Count > 0)
        {
            var dict = rb.Alloc(0x40);
            TextureWriter.WriteDictionary(rb, dict, d.Embedded);
            dictPtr = dict.Ptr;
            // the reader finds embedded textures by name, so the shader params may point at plain texture blocks
        }
        var shaderPtrs = new List<ulong>();
        foreach (var s in d.Shaders) shaderPtrs.Add(WriteShader(rb, s));
        var sarr = new Blk(Math.Max(8, 8 * shaderPtrs.Count));
        for (int i = 0; i < shaderPtrs.Count; i++) sarr.U64(i * 8, shaderPtrs[i]);
        var sarrRef = rb.Add(sarr.B);
        var sg = new Blk(0x40).U32(0, 0x56A).U32(4, 1).U64(0x08, dictPtr).U64(0x10, sarrRef.Ptr).U16(0x18, shaderPtrs.Count).U16(0x1A, shaderPtrs.Count);
        b.U64(0x10, rb.Add(sg.B).Ptr);

        // skeleton
        if (d.Bones.Count > 0)
        {
            var bones = new Blk(0x50 * d.Bones.Count);
            var parents = new Blk(Math.Max(2, 2 * d.Bones.Count));
            for (int i = 0; i < d.Bones.Count; i++)
            {
                var bo = d.Bones[i];
                int o = i * 0x50;
                bones.F32(o, bo.R.X).F32(o + 4, bo.R.Y).F32(o + 8, bo.R.Z).F32(o + 12, bo.R.W)
                     .Vec3(o + 0x10, bo.T).F32(o + 0x20, 1).F32(o + 0x24, 1).F32(o + 0x28, 1)
                     .S16(o + 0x30, -1).S16(o + 0x32, bo.Parent)
                     .U64(o + 0x38, rb.String(bo.Name).Ptr).U16(o + 0x42, i).U16(o + 0x44, CthExtract.Gta.Hashes.BoneTag(bo.Name));
                parents.S16(i * 2, bo.Parent);
            }
            var bonesRef = rb.Add(bones.B);
            var parRef = rb.Add(parents.B);
            var sk = new Blk(0x70).U32(0, 0x5E1).U32(4, 1).U64(0x20, bonesRef.Ptr).U64(0x38, parRef.Ptr).U16(0x5E, d.Bones.Count);
            b.U64(0x18, rb.Add(sk.B).Ptr);
        }

        // LOD model lists
        for (int lod = 0; lod < 4; lod++)
        {
            if (d.Lods[lod].Count == 0) continue;
            var modelPtrs = d.Lods[lod].Select(m => WriteModel(rb, m)).ToList();
            var arr = new Blk(8 * modelPtrs.Count);
            for (int i = 0; i < modelPtrs.Count; i++) arr.U64(i * 8, modelPtrs[i]);
            var arrRef = rb.Add(arr.B);
            var hdr = new Blk(16).U64(0, arrRef.Ptr).U16(8, modelPtrs.Count).U16(10, modelPtrs.Count);
            b.U64(0x50 + lod * 8, rb.Add(hdr.B).Ptr);
        }
        rb.Put(block, b.B);
        return block;
    }

    private static ulong WriteShader(ResourceBuilder rb, SynthShader s)
    {
        var p = new List<(byte type, ulong ptr, uint hash, float[]? vec)>();
        foreach (var (param, tex) in s.Textures)
        {
            var tb = new Blk(0x40).U32(0, 0x7E7).U32(4, 1).U64(0x28, rb.String(tex).Ptr);
            p.Add((0, rb.Add(tb.B).Ptr, CthExtract.Gta.Hashes.Joaat(param), null));
        }
        foreach (var (param, vec) in s.Vectors) p.Add(((byte)(vec.Length / 4), 0, CthExtract.Gta.Hashes.Joaat(param), vec));
        int vecBytes = p.Sum(x => x.type * 16);
        var block = rb.Alloc(p.Count * 16 + vecBytes + p.Count * 4);
        var pb = new Blk(p.Count * 16 + vecBytes + p.Count * 4);
        int vo = p.Count * 16;
        for (int i = 0; i < p.Count; i++)
        {
            ulong ptr = p[i].ptr;
            if (p[i].vec is { } v)
            {
                ptr = block.Ptr + (ulong)vo;
                for (int k = 0; k < v.Length; k++) pb.F32(vo + k * 4, v[k]);
                vo += v.Length * 4;
            }
            pb.U8(i * 16, p[i].type).U64(i * 16 + 8, ptr);
            pb.U32(p.Count * 16 + vecBytes + i * 4, p[i].hash);
        }
        rb.Put(block, pb.B);
        var fx = new Blk(0x30).U64(0, p.Count == 0 ? 0 : block.Ptr).U32(0x08, CthExtract.Gta.Hashes.Joaat(s.Name)).U8(0x10, p.Count)
            .U32(0x18, CthExtract.Gta.Hashes.Joaat(s.Name + ".sps")).U8(0x27, s.Textures.Count);
        return rb.Add(fx.B).Ptr;
    }

    private static ulong WriteModel(ResourceBuilder rb, SynthModel m)
    {
        var geoPtrs = m.Geoms.Select(g => WriteGeometry(rb, g)).ToList();
        var arr = new Blk(Math.Max(8, 8 * geoPtrs.Count));
        for (int i = 0; i < geoPtrs.Count; i++) arr.U64(i * 8, geoPtrs[i]);
        var arrRef = rb.Add(arr.B);
        var map = new Blk(Math.Max(2, 2 * m.Geoms.Count));
        for (int i = 0; i < m.Geoms.Count; i++) map.U16(i * 2, m.Geoms[i].Shader);
        var mapRef = rb.Add(map.B);
        var mb = new Blk(0x30).U32(0, 0x30D).U32(4, 1).U64(0x08, arrRef.Ptr).U16(0x10, geoPtrs.Count).U16(0x12, geoPtrs.Count)
            .U64(0x20, mapRef.Ptr).U32(0x28, ((uint)m.Bone << 24) | ((m.Skinned ? 1u : 0u) << 8));
        return rb.Add(mb.B).Ptr;
    }

    private static ulong WriteGeometry(ResourceBuilder rb, SynthGeometry g)
    {
        var mesh = g.Mesh;
        uint flags = g.VertexFormat == 2 ? 0x59u : 0x49u; // pos, normal, (colour0), texcoord0
        ulong types = g.VertexFormat == 1 ? (GtaV1Types & ~(0xFUL << 24)) | (1UL << 24) : GtaV1Types;
        int stride = 12 + 12 + (g.VertexFormat == 2 ? 4 : 0) + (g.VertexFormat == 1 ? 4 : 8);
        var vd = new Blk(stride * mesh.P.Count);
        for (int v = 0; v < mesh.P.Count; v++)
        {
            int o = v * stride;
            vd.Vec3(o, mesh.P[v]).Vec3(o + 12, mesh.N[v]);
            int uo = o + 24;
            if (g.VertexFormat == 2) { vd.U32(uo, 0xFF00FF00); uo += 4; }
            if (g.VertexFormat == 1)
            {
                vd.U16(uo, BitConverter.HalfToUInt16Bits((Half)mesh.UV[v].X)).U16(uo + 2, BitConverter.HalfToUInt16Bits((Half)mesh.UV[v].Y));
            }
            else vd.F32(uo, mesh.UV[v].X).F32(uo + 4, mesh.UV[v].Y);
        }
        var vdata = rb.Add(vd.B, graphics: true);
        var decl = rb.Add(new Blk(0x10).U32(0, flags).U16(4, stride).U8(7, System.Numerics.BitOperations.PopCount(flags)).U64(8, types).B);
        var vb = rb.Add(new Blk(0x80).U32(0, 0xB0F).U32(4, 1).U16(8, stride).U64(0x10, vdata.Ptr).U32(0x18, (uint)mesh.P.Count)
            .U64(0x20, vdata.Ptr).U64(0x30, decl.Ptr).B);
        var id = new Blk(Math.Max(2, 2 * mesh.I.Count));
        for (int i = 0; i < mesh.I.Count; i++) id.U16(i * 2, mesh.I[i]);
        var idata = rb.Add(id.B, graphics: true);
        var ib = rb.Add(new Blk(0x60).U32(0, 0x1B0F).U32(4, 1).U32(8, (uint)mesh.I.Count).U64(0x10, idata.Ptr).B);
        var gb = new Blk(0x98).U32(0, 0x6E0).U32(4, 1).U64(0x18, vb.Ptr).U64(0x38, ib.Ptr)
            .U32(0x58, (uint)mesh.I.Count).U16(0x60, mesh.P.Count).U16(0x70, stride);
        return rb.Add(gb.B).Ptr;
    }
}
