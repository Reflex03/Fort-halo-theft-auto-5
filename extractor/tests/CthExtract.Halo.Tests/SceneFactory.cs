using System.Numerics;
using Reclaimer.Drawing;
using Reclaimer.Geometry;
using Reclaimer.Geometry.Vectors;
using Reclaimer.Utilities;

namespace CthExtract.Halo.Tests;

/// <summary>Builds Reclaimer Scenes by hand (what ContentFactory returns for a 'mode' tag on a real map).</summary>
public static class SceneFactory
{
    /// <summary>
    /// A box "gun" in Halo axes: barrel along +X from x0 to x1, y (left) ±halfWidth, z (up) ±halfHeight.
    /// Triangles wound counter-clockwise seen from outside, with outward normals.
    /// </summary>
    public static (List<Vector3> P, List<Vector3> N, List<Vector2> UV, List<int> I) Box(float x0, float x1, float halfWidth, float halfHeight)
    {
        var p = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var idx = new List<int>();
        void Face(Vector3 normal, Vector3 u, Vector3 v, Vector3 centre, float hu, float hv)
        {
            int b = p.Count;
            // corners counter-clockwise around normal: u × v = normal
            p.Add(centre - u * hu - v * hv); p.Add(centre + u * hu - v * hv); p.Add(centre + u * hu + v * hv); p.Add(centre - u * hu + v * hv);
            for (int k = 0; k < 4; k++) n.Add(normal);
            uv.Add(new(0, 1)); uv.Add(new(1, 1)); uv.Add(new(1, 0)); uv.Add(new(0, 0));
            idx.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
        }
        float cx = (x0 + x1) / 2, hx = (x1 - x0) / 2;
        var c = new Vector3(cx, 0, 0);
        Face(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, c + Vector3.UnitX * hx, halfWidth, halfHeight);
        Face(-Vector3.UnitX, -Vector3.UnitY, Vector3.UnitZ, c - Vector3.UnitX * hx, halfWidth, halfHeight);
        Face(Vector3.UnitY, -Vector3.UnitX, Vector3.UnitZ, c + Vector3.UnitY * halfWidth, hx, halfHeight);
        Face(-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, c - Vector3.UnitY * halfWidth, hx, halfHeight);
        Face(Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, c + Vector3.UnitZ * halfHeight, hx, halfWidth);
        Face(-Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitY, c - Vector3.UnitZ * halfHeight, hx, halfWidth);
        return (p, n, uv, idx);
    }

    public static Mesh MakeMesh(IList<Vector3> positions, IList<Vector3>? normals, IList<Vector2>? uvs, IList<int> indices, Material? material,
                                IndexFormat layout = IndexFormat.TriangleList, RealBounds3D posBounds = default, RealBounds2D texBounds = default)
    {
        var vb = new VertexBuffer();
        vb.PositionChannels.Add(positions.Select(v => (IVector)new RealVector3(v.X, v.Y, v.Z)).ToList());
        if (normals != null) vb.NormalChannels.Add(normals.Select(v => (IVector)new RealVector3(v.X, v.Y, v.Z)).ToList());
        if (uvs != null) vb.TextureCoordinateChannels.Add(uvs.Select(v => (IVector)new RealVector2(v.X, v.Y)).ToList());
        var mesh = new Mesh
        {
            VertexBuffer = vb,
            IndexBuffer = IndexBuffer.FromArray(indices.ToArray(), layout),
            PositionBounds = posBounds,
            TextureBounds = texBounds,
        };
        mesh.Segments.Add(new MeshSegment { IndexStart = 0, IndexLength = indices.Count, Material = material });
        return mesh;
    }

    public static Material MakeMaterial(int id, string name, string alphaMode, IBitmap? diffuse, Vector2? tiling = null)
    {
        var m = new Material { Id = id, Name = name, AlphaMode = alphaMode };
        if (diffuse != null)
            m.TextureMappings.Add(new TextureMapping
            {
                Usage = TextureUsage.Diffuse,
                Tiling = tiling ?? Vector2.One,
                Texture = new Texture { Id = id + 1000, ContentProvider = new BitmapProvider(name + "_diffuse", diffuse) },
            });
        return m;
    }

    /// <summary>A model with one region "default" / permutation "base" holding the given meshes.</summary>
    public static Model Model(string name, params Mesh[] meshes)
    {
        var model = new Model { Name = name };
        model.Meshes.AddRange(meshes);
        var region = new ModelRegion { Name = "default" };
        region.Permutations.Add(new ModelPermutation { Name = "base", MeshRange = (0, meshes.Length) });
        model.Regions.Add(region);
        return model;
    }

    public static Scene Wrap(Model model, float unitScaleMm = 3048f) => Scene.WrapSingleModel(model, unitScaleMm);

    public static void AddMarker(Model model, string name, Vector3 haloPosition, int bone = -1)
    {
        var mk = new Marker { Name = name };
        mk.Instances.Add(new MarkerInstance { Position = haloPosition, Rotation = Quaternion.Identity, RegionIndex = -1, PermutationIndex = -1, BoneIndex = bone });
        model.Markers.Add(mk);
    }

    /// <summary>Uncompressed BGRA bitmap.</summary>
    public sealed class Bgra : IBitmap
    {
        private readonly int w, h; private readonly byte[] px;
        public Bgra(int w, int h, byte[] bgra) { this.w = w; this.h = h; px = bgra; }
        public static Bgra Solid(int w, int h, byte b, byte g, byte r, byte a)
        {
            var d = new byte[w * h * 4];
            for (int i = 0; i < d.Length; i += 4) { d[i] = b; d[i + 1] = g; d[i + 2] = r; d[i + 3] = a; }
            return new Bgra(w, h, d);
        }
        public string SourceFile => "synthetic";
        public int Id => 1;
        public string Name => "bgra";
        public string Class => "bitm";
        public int SubmapCount => 1;
        public CubemapLayout CubeLayout => null!;
        public DdsImage ToDds(int index) => new(h, w, DxgiFormat.B8G8R8A8_UNorm, px);
    }

    /// <summary>BC1 (DXT1) compressed bitmap, decoded by Reclaimer's managed decoder.</summary>
    public sealed class Bc1 : IBitmap
    {
        private readonly int w, h; private readonly byte[] blocks;
        public Bc1(int w, int h, byte[] blocks) { this.w = w; this.h = h; this.blocks = blocks; }
        /// <summary>Every 4x4 block = colour0 (RGB565), indices 0.</summary>
        public static Bc1 Solid(int w, int h, ushort rgb565)
        {
            int n = Math.Max(1, w / 4) * Math.Max(1, h / 4);
            var b = new byte[n * 8];
            for (int i = 0; i < n; i++) { b[i * 8] = (byte)rgb565; b[i * 8 + 1] = (byte)(rgb565 >> 8); }
            return new Bc1(w, h, b);
        }
        public string SourceFile => "synthetic";
        public int Id => 2;
        public string Name => "bc1";
        public string Class => "bitm";
        public int SubmapCount => 1;
        public CubemapLayout CubeLayout => null!;
        public DdsImage ToDds(int index) => new(h, w, DxgiFormat.BC1_UNorm, blocks);
    }

    /// <summary>Throws on decode, like a bitmap whose resource page is missing.</summary>
    public sealed class Broken : IBitmap
    {
        public string SourceFile => "synthetic";
        public int Id => 3;
        public string Name => "broken";
        public string Class => "bitm";
        public int SubmapCount => 1;
        public CubemapLayout CubeLayout => null!;
        public DdsImage ToDds(int index) => throw new InvalidDataException("resource page missing");
    }

    public sealed class BitmapProvider : IContentProvider<IBitmap>
    {
        private readonly IBitmap bmp;
        public BitmapProvider(string name, IBitmap bmp) { Name = name; this.bmp = bmp; }
        public IBitmap GetContent() => bmp;
        public string SourceFile => "synthetic";
        public int Id => 1;
        public string Name { get; }
        public string Class => "bitm";
    }
}
