using System.Numerics;

namespace CthExtract.Gta.Resources;

/// <summary>A physics child of a fragment (doors, wheels, ...): bone tag, object-space placement and its drawable.</summary>
public sealed class FragChild
{
    public int Index { get; init; }
    public ushort BoneTag { get; init; }
    /// <summary>Object-space transform (row-vector convention) with the physics LOD's position offset applied, or null.</summary>
    public Matrix4x4? Transform { get; init; }
    public DrawableData? Drawable { get; init; }
}

public sealed class FragmentData
{
    public string? Name { get; init; }
    public required DrawableData Drawable { get; init; }
    public Matrix4x4[] BonePose { get; init; } = Array.Empty<Matrix4x4>();
    public List<FragChild> Children { get; } = new();
    public Vector3 PositionOffset { get; init; }
    public List<string> Problems { get; } = new();
}

/// <summary>
/// Reads a legacy (gen8) PC fragment (.yft root block, FragType 0x130 bytes): Drawable 0x30, Name 0x58,
/// bone pose transforms 0xA8, PhysicsLODGroup 0xF0. Layouts from gta-toolkit's Resources/PC/Fragments (MIT);
/// the child bone tag at 0x12 and the meaning of the pose/physics matrices are format facts from public notes.
/// </summary>
public static class FragmentReader
{
    public const int Gen8YftVersion = 162;

    public static FragmentData Read(Resource r)
    {
        const long frag = 0; // the root block is at the start of the system pages
        string? name = r.CString(r.U64(frag + 0x58));
        var pose = ReadBonePose(r, r.Ptr(frag + 0xA8));
        long drw = r.Ptr(frag + 0x30);
        if (drw < 0) throw new InvalidDataException($"{r.What}: fragment has no drawable");
        var main = DrawableReader.Read(r, drw, pose, name);

        long grp = r.Ptr(frag + 0xF0);
        long lod = grp >= 0 ? r.Ptr(grp + 0x10) : -1;
        Vector3 posOffset = Vector3.Zero;
        if (lod >= 0)
        {
            posOffset = new Vector3(r.F32(lod + 0x30), r.F32(lod + 0x34), r.F32(lod + 0x38));
            if (!float.IsFinite(posOffset.X + posOffset.Y + posOffset.Z) || posOffset.Length() > 100) posOffset = Vector3.Zero;
        }
        var result = new FragmentData { Name = name, Drawable = main, BonePose = pose, PositionOffset = posOffset };
        if (lod < 0) return result;
        try
        {
            long children = r.Ptr(lod + 0xD0);
            long xforms = r.Ptr(lod + 0x100);
            int count = r.U8(lod + 0x11D);
            int xcount = xforms >= 0 ? (int)r.U32(xforms + 0x10) : 0;
            for (int i = 0; i < count && children >= 0; i++)
            {
                long c = r.Ptr(children + i * 8);
                if (c < 0) continue;
                ushort tag = r.U16(c + 0x12);
                Matrix4x4? xf = null;
                if (i < xcount)
                {
                    long m = xforms + 0x20 + i * 64;
                    var mat = new Matrix4x4(
                        r.F32(m), r.F32(m + 4), r.F32(m + 8), r.F32(m + 12),
                        r.F32(m + 16), r.F32(m + 20), r.F32(m + 24), r.F32(m + 28),
                        r.F32(m + 32), r.F32(m + 36), r.F32(m + 40), r.F32(m + 44),
                        r.F32(m + 48), r.F32(m + 52), r.F32(m + 56), r.F32(m + 60));
                    mat.M14 = 0; mat.M24 = 0; mat.M34 = 0; mat.M44 = 1;
                    mat.M41 += posOffset.X; mat.M42 += posOffset.Y; mat.M43 += posOffset.Z;
                    if (IsFinite(mat)) xf = mat;
                }
                DrawableData? cd = null;
                long cdrw = r.Ptr(c + 0xA0);
                if (cdrw >= 0)
                {
                    try { cd = DrawableReader.Read(r, cdrw, null, $"child{i}"); }
                    catch (InvalidDataException e) { result.Problems.Add($"child {i}: {e.Message}"); }
                }
                result.Children.Add(new FragChild { Index = i, BoneTag = tag, Transform = xf, Drawable = cd });
            }
        }
        catch (InvalidDataException e) { result.Problems.Add($"physics LOD: {e.Message}"); }
        return result;
    }

    /// <summary>Pose block: u8 count at 0x10, then 3x float4 per bone at 0x20 (rows of a 3x4 affine matrix:
    /// row k = (basis_x.k, basis_y.k, basis_z.k, translation.k)). Returned as row-vector matrices.</summary>
    public static Matrix4x4[] ReadBonePose(Resource r, long bt)
    {
        if (bt < 0) return Array.Empty<Matrix4x4>();
        int n = r.U8(bt + 0x10);
        var m = new Matrix4x4[n];
        for (int i = 0; i < n; i++)
        {
            long o = bt + 0x20 + i * 48;
            float g(int row, int col) => r.F32(o + row * 16 + col * 4);
            m[i] = new Matrix4x4(
                g(0, 0), g(1, 0), g(2, 0), 0,
                g(0, 1), g(1, 1), g(2, 1), 0,
                g(0, 2), g(1, 2), g(2, 2), 0,
                g(0, 3), g(1, 3), g(2, 3), 1);
            if (!IsFinite(m[i])) m[i] = Matrix4x4.Identity;
        }
        return m;
    }

    private static bool IsFinite(Matrix4x4 m) =>
        float.IsFinite(m.M11 + m.M12 + m.M13 + m.M14 + m.M21 + m.M22 + m.M23 + m.M24 +
                       m.M31 + m.M32 + m.M33 + m.M34 + m.M41 + m.M42 + m.M43 + m.M44);
}
