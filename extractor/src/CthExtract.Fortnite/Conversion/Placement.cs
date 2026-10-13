using System.Numerics;
using CthExtract.Core;

namespace CthExtract.Fortnite.Conversion;

public readonly record struct Bounds(Vector3 Min, Vector3 Max)
{
    public Vector3 Size => Max - Min;
    public Vector3 Centre => (Min + Max) * 0.5f;
    public float Largest => MathF.Max(Size.X, MathF.Max(Size.Y, Size.Z));
    public override string ToString() =>
        $"[{Min.X:0.###},{Min.Y:0.###},{Min.Z:0.###}]..[{Max.X:0.###},{Max.Y:0.###},{Max.Z:0.###}] m (size {Size.X:0.##} x {Size.Y:0.##} x {Size.Z:0.##})";
}

/// <summary>The CONTRACTS.md placement rules for Fortnite meshes.</summary>
public static class Placement
{
    public static Bounds Measure(MeshData m)
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        bool any = false;
        foreach (var s in m.Submeshes)
            for (int i = 0; i < s.Vertices.Count; i += 8)
            {
                var p = new Vector3(s.Vertices[i], s.Vertices[i + 1], s.Vertices[i + 2]);
                min = Vector3.Min(min, p); max = Vector3.Max(max, p); any = true;
            }
        return any ? new Bounds(min, max) : new Bounds(Vector3.Zero, Vector3.Zero);
    }

    public static void Translate(MeshData m, Vector3 d)
    {
        foreach (var s in m.Submeshes)
            for (int i = 0; i < s.Vertices.Count; i += 8)
            {
                s.Vertices[i] += d.X; s.Vertices[i + 1] += d.Y; s.Vertices[i + 2] += d.Z;
            }
        foreach (var n in m.Nodes) { n.X += d.X; n.Y += d.Y; n.Z += d.Z; }
    }

    /// <summary>Rotate about +Y by a multiple of 90 degrees (x,z) -> (z,-x) per quarter turn; a proper rotation, so
    /// winding stays valid.</summary>
    public static void RotateY90(MeshData m, int quarterTurns)
    {
        int q = ((quarterTurns % 4) + 4) % 4;
        if (q == 0) return;
        foreach (var s in m.Submeshes)
            for (int i = 0; i < s.Vertices.Count; i += 8)
            {
                (s.Vertices[i], s.Vertices[i + 2]) = Rot(s.Vertices[i], s.Vertices[i + 2], q);
                (s.Vertices[i + 3], s.Vertices[i + 5]) = Rot(s.Vertices[i + 3], s.Vertices[i + 5], q);
            }
        foreach (var n in m.Nodes) (n.X, n.Z) = Rot(n.X, n.Z, q);

        static (float, float) Rot(float x, float z, int q) => q switch
        {
            1 => (z, -x),
            2 => (-x, -z),
            _ => (-z, x),
        };
    }

    /// <summary>Vehicles and the bus: centred in X/Z, lowest point at y = 0, front towards +Z.
    /// UE forward (+X) already maps to +Z. When the long axis ends up along X instead, the mesh is turned a quarter
    /// so the length runs along Z (which end is the front is then unverified, and logged).</summary>
    public static string PlaceVehicle(MeshData m, bool longAxisAlongZ)
    {
        var notes = new List<string>();
        var b = Measure(m);
        if (longAxisAlongZ && b.Size.X > b.Size.Z * 1.25f)
        {
            RotateY90(m, 1);
            notes.Add("turned 90° so the long axis is Z (front end unverified)");
            b = Measure(m);
        }
        var d = new Vector3(-b.Centre.X, -b.Min.Y, -b.Centre.Z);
        Translate(m, d);
        notes.Add($"moved by ({d.X:0.###}, {d.Y:0.###}, {d.Z:0.###}) m to centre X/Z and floor at y=0");
        return string.Join("; ", notes);
    }

    /// <summary>Glider: origin at the attach point (the UE mesh origin after the item's GliderMeshTransform), wing span
    /// along X. If the span lies along Z it is turned a quarter about Y.</summary>
    public static string PlaceGlider(MeshData m)
    {
        var b = Measure(m);
        if (b.Size.Z > b.Size.X * 1.25f)
        {
            RotateY90(m, 1);
            return "turned 90° so the wing span is along X";
        }
        return "origin kept at the attach point";
    }
}
