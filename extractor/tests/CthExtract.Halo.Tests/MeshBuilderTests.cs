using System.Numerics;
using Reclaimer.Geometry;
using Xunit;

namespace CthExtract.Halo.Tests;

public class MeshBuilderTests
{
    private const float Wu = 3.048f;

    private static List<Vector3> Positions(BuiltMesh b) =>
        b.Mesh.Submeshes.SelectMany(s => Enumerable.Range(0, s.VertexCount).Select(i => new Vector3(s.Vertices[i * 8], s.Vertices[i * 8 + 1], s.Vertices[i * 8 + 2]))).ToList();

    private static (Vector3 Min, Vector3 Max) Bounds(BuiltMesh b)
    {
        var p = Positions(b);
        return (p.Aggregate(Vector3.Min), p.Aggregate(Vector3.Max));
    }

    [Fact]
    public void HaloAxesMapToMinecraft_y_z_x()
    {
        // Halo: x forward, y left, z up  ->  Minecraft: x left, y up, z forward
        Assert.Equal(new Vector3(0, 0, 1), MeshBuilder.HaloToMc(Vector3.UnitX)); // forward -> +Z (barrel)
        Assert.Equal(new Vector3(1, 0, 0), MeshBuilder.HaloToMc(Vector3.UnitY)); // left -> +X
        Assert.Equal(new Vector3(0, 1, 0), MeshBuilder.HaloToMc(Vector3.UnitZ)); // up -> +Y
        // a proper rotation (keeps handedness, so triangle winding keeps its meaning)
        var m = new Matrix4x4(0, 0, 1, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1);
        Assert.Equal(1f, m.GetDeterminant(), 5);
        Assert.Equal(MeshBuilder.HaloToMc(new Vector3(1, 2, 3)), Vector3.Transform(new Vector3(1, 2, 3), m));
    }

    [Fact]
    public void TrueScaleRifleKeepsWorldUnitScaleAndBarrelPointsPlusZ()
    {
        // 0.3 wu long gun = 0.914 m: inside the rifle held range, so only 1 wu = 3.048 m applies
        var (p, n, uv, idx) = SceneFactory.Box(-0.05f, 0.25f, 0.02f, 0.04f);
        var model = SceneFactory.Model("ar", SceneFactory.MakeMesh(p, n, uv, idx, null));
        SceneFactory.AddMarker(model, "primary_trigger", new Vector3(0.25f, 0, 0));
        var b = MeshBuilder.Build(SceneFactory.Wrap(model), new MeshBuildOptions { MaterialPrefix = "halo/x" });
        Assert.Equal(1f, b.Scale, 4);
        Assert.Equal(3.048f, b.MetresPerUnit, 4);
        Assert.Equal(0.3f * Wu, b.NativeLength, 3);
        var (min, max) = Bounds(b);
        Assert.Equal(0.3f * Wu, max.Z - min.Z, 3);          // length along +Z
        Assert.Equal(0.04f * Wu, max.X - min.X, 3);         // width (Halo y) along X
        Assert.Equal(0.08f * Wu, max.Y - min.Y, 3);         // height (Halo z) along Y
        Assert.False(b.Rotated180);
        Assert.Equal("bounding-box centre", b.OriginSource);
        Assert.Equal(Vector3.Zero, (min + max) / 2, new Vec3Comparer(1e-4f));
        Assert.Equal(12, b.Mesh.TriangleCount);
    }

    [Fact]
    public void OriginAtRightHandMarker()
    {
        var (p, n, uv, idx) = SceneFactory.Box(-0.05f, 0.25f, 0.02f, 0.04f);
        var model = SceneFactory.Model("ar", SceneFactory.MakeMesh(p, n, uv, idx, null));
        SceneFactory.AddMarker(model, "right_hand", new Vector3(0.0f, 0.0f, -0.04f));
        var b = MeshBuilder.Build(SceneFactory.Wrap(model), new MeshBuildOptions { MaterialPrefix = "halo/x" });
        Assert.Equal("marker 'right_hand'", b.OriginSource);
        var (min, max) = Bounds(b);
        // grip at Halo (0,0,-0.04) -> origin; mesh spans z from -0.05 wu to 0.25 wu ahead of it, and sits above it
        Assert.Equal(-0.05f * Wu, min.Z, 3);
        Assert.Equal(0.25f * Wu, max.Z, 3);
        Assert.Equal(0f, min.Y, 3);
        Assert.Equal(0.08f * Wu, max.Y, 3);
    }

    [Fact]
    public void MarkerOnABoneUsesTheBoneTransform()
    {
        var (p, n, uv, idx) = SceneFactory.Box(-0.05f, 0.25f, 0.02f, 0.04f);
        var model = SceneFactory.Model("ar", SceneFactory.MakeMesh(p, n, uv, idx, null));
        model.Bones.Add(new Bone { Name = "frame_gun", ParentIndex = -1, LocalTransform = Matrix4x4.CreateTranslation(0.1f, 0, 0), WorldTransform = Matrix4x4.CreateTranslation(0.1f, 0, 0) });
        SceneFactory.AddMarker(model, "right_hand", new Vector3(-0.1f, 0, -0.04f), bone: 0); // world (0,0,-0.04)
        var b = MeshBuilder.Build(SceneFactory.Wrap(model), new MeshBuildOptions { MaterialPrefix = "halo/x" });
        var (min, _) = Bounds(b);
        Assert.Equal(-0.05f * Wu, min.Z, 3);
        Assert.Equal(0f, min.Y, 3);
    }

    [Fact]
    public void HandleNodeIsTheGripWhenThereIsNoMarker()
    {
        var (p, n, uv, idx) = SceneFactory.Box(-0.05f, 0.25f, 0.02f, 0.04f);
        var model = SceneFactory.Model("ar", SceneFactory.MakeMesh(p, n, uv, idx, null));
        model.Bones.Add(new Bone { Name = "gun", ParentIndex = -1, WorldTransform = Matrix4x4.CreateTranslation(0.05f, 0, 0) });
        var b = MeshBuilder.Build(SceneFactory.Wrap(model), new MeshBuildOptions { MaterialPrefix = "halo/x", HandleNode = "gun" });
        Assert.Equal("handle node 'gun'", b.OriginSource);
        var (min, max) = Bounds(b);
        Assert.Equal(-0.1f * Wu, min.Z, 3);
        Assert.Equal(0.2f * Wu, max.Z, 3);
    }

    [Fact]
    public void MuzzleBehindTheGripTurnsTheGunAround()
    {
        // a model authored facing -X: muzzle marker at the -X end
        var (p, n, uv, idx) = SceneFactory.Box(-0.25f, 0.05f, 0.02f, 0.04f);
        var model = SceneFactory.Model("rev", SceneFactory.MakeMesh(p, n, uv, idx, null));
        SceneFactory.AddMarker(model, "primary_trigger", new Vector3(-0.25f, 0, 0));
        SceneFactory.AddMarker(model, "right_hand", new Vector3(0, 0, 0));
        var b = MeshBuilder.Build(SceneFactory.Wrap(model), new MeshBuildOptions { MaterialPrefix = "halo/x" });
        Assert.True(b.Rotated180);
        var (min, max) = Bounds(b);
        Assert.Equal(0.25f * Wu, max.Z, 3); // muzzle now ahead (+Z)
        Assert.Equal(-0.05f * Wu, min.Z, 3);
        Assert.False(b.WindingFlipped);   // a rotation keeps winding
    }

    [Fact]
    public void PistolIsScaledUpToHeldSizeAndRifleDown()
    {
        var (p, n, uv, idx) = SceneFactory.Box(0, 0.05f, 0.01f, 0.02f); // 0.1524 m
        var pistol = MeshBuilder.Build(SceneFactory.Wrap(SceneFactory.Model("p", SceneFactory.MakeMesh(p, n, uv, idx, null))),
            new MeshBuildOptions { MaterialPrefix = "halo/x", Held = HeldClass.Pistol });
        Assert.Equal(0.3f / (0.05f * Wu), pistol.Scale, 3);
        var (min, max) = Bounds(pistol);
        Assert.Equal(0.3f, max.Z - min.Z, 3);

        var (p2, n2, uv2, idx2) = SceneFactory.Box(0, 0.6f, 0.03f, 0.05f); // 1.83 m sniper-ish
        var rifle = MeshBuilder.Build(SceneFactory.Wrap(SceneFactory.Model("r", SceneFactory.MakeMesh(p2, n2, uv2, idx2, null))),
            new MeshBuildOptions { MaterialPrefix = "halo/x" });
        (min, max) = Bounds(rifle);
        Assert.Equal(1.2f, max.Z - min.Z, 3);
        // uniform scale: proportions kept
        Assert.Equal(0.06f / 0.6f, (max.X - min.X) / (max.Z - min.Z), 3);

        var (p3, n3, uv3, idx3) = SceneFactory.Box(-0.02f, 0.02f, 0.02f, 0.02f); // 0.12 m grenade: stays
        var gren = MeshBuilder.Build(SceneFactory.Wrap(SceneFactory.Model("g", SceneFactory.MakeMesh(p3, n3, uv3, idx3, null))),
            new MeshBuildOptions { MaterialPrefix = "halo/x", Held = HeldClass.Grenade });
        Assert.Equal(1f, gren.Scale, 3);
        Assert.Equal("bounding-box centre", gren.OriginSource);
    }

    [Fact]
    public void PositionAndTextureBoundsAreExpanded()
    {
        // compressed positions/uvs in 0..1 and the model's bounds, as Halo stores them
        var (p, n, uv, idx) = SceneFactory.Box(0, 1, 0.5f, 0.5f);           // x 0..1, y -0.5..0.5, z -0.5..0.5
        var unit = p.Select(v => new Vector3(v.X, v.Y + 0.5f, v.Z + 0.5f)).ToList(); // 0..1 cube
        var posBounds = new RealBounds3D(new RealBounds(-0.05f, 0.25f), new RealBounds(-0.02f, 0.02f), new RealBounds(-0.04f, 0.04f));
        var texBounds = new RealBounds2D(new RealBounds(0.25f, 0.75f), new RealBounds(0f, 0.5f));
        var mesh = SceneFactory.MakeMesh(unit, n, uv, idx, null, posBounds: posBounds, texBounds: texBounds);
        var b = MeshBuilder.Build(SceneFactory.Wrap(SceneFactory.Model("c", mesh)), new MeshBuildOptions { MaterialPrefix = "halo/x" });
        var (min, max) = Bounds(b);
        Assert.Equal(0.3f * Wu, max.Z - min.Z, 3);
        Assert.Equal(0.04f * Wu, max.X - min.X, 3);
        Assert.Equal(0.08f * Wu, max.Y - min.Y, 3);
        var s = b.Mesh.Submeshes[0];
        var us = Enumerable.Range(0, s.VertexCount).Select(i => s.Vertices[i * 8 + 6]).ToList();
        var vs = Enumerable.Range(0, s.VertexCount).Select(i => s.Vertices[i * 8 + 7]).ToList();
        Assert.Equal(0.25f, us.Min(), 4); Assert.Equal(0.75f, us.Max(), 4);
        Assert.Equal(0f, vs.Min(), 4); Assert.Equal(0.5f, vs.Max(), 4); // no V flip (top-left origin kept)
    }

    [Fact]
    public void PermutationAndPlacementTransformsAreApplied()
    {
        // permutation: scale 2 then +1 wu up (Halo z); placement: +0.5 wu left (Halo y) for mesh and markers alike
        var (p, n, uv, idx) = SceneFactory.Box(0, 0.1f, 0.01f, 0.01f);
        var model = SceneFactory.Model("t", SceneFactory.MakeMesh(p, n, uv, idx, null));
        model.Regions[0].Permutations[0].Transform = Matrix4x4.CreateTranslation(0, 0, 1);
        model.Regions[0].Permutations[0].UniformScale = 2;
        SceneFactory.AddMarker(model, "right_hand", Vector3.Zero);
        var scene = new Scene { CoordinateSystem = CoordinateSystem.Default.WithScale(3048) };
        scene.ChildObjects.Add(new ObjectPlacement(model) { Transform = Matrix4x4.CreateTranslation(0, 0.5f, 0) });
        var b = MeshBuilder.Build(scene, new MeshBuildOptions { MaterialPrefix = "halo/x" });
        Assert.Equal(1f, b.Scale, 4); // 0.2 wu = 0.61 m: inside the rifle range
        var (min, max) = Bounds(b);
        Assert.Equal(0f, min.Z, 3);                       // x 0..0.2 wu (scaled by 2) -> z
        Assert.Equal(0.2f * Wu, max.Z, 3);
        Assert.Equal(1f * Wu, (min.Y + max.Y) / 2, 3);     // +1 wu up from the permutation translation
        Assert.Equal(0.04f * Wu, max.Y - min.Y, 3);        // height 0.02 wu x 2
        Assert.Equal(0f, (min.X + max.X) / 2, 3);          // placement moved mesh and marker together
        Assert.Equal(0.04f * Wu, max.X - min.X, 3);
    }

    [Fact]
    public void PlacementOffsetMovesGeometryRelativeToMarkers()
    {
        // the marker is in model space; a placement translation moves both mesh and marker -> grip offset unchanged
        var (p, n, uv, idx) = SceneFactory.Box(-0.05f, 0.25f, 0.02f, 0.04f);
        var model = SceneFactory.Model("ar", SceneFactory.MakeMesh(p, n, uv, idx, null));
        SceneFactory.AddMarker(model, "right_hand", new Vector3(0, 0, -0.04f));
        var scene = new Scene { CoordinateSystem = CoordinateSystem.Default.WithScale(3048) };
        scene.ChildObjects.Add(new ObjectPlacement(model) { Transform = Matrix4x4.CreateTranslation(5, 5, 5) });
        var b = MeshBuilder.Build(scene, new MeshBuildOptions { MaterialPrefix = "halo/x" });
        var (min, max) = Bounds(b);
        Assert.Equal(-0.05f * Wu, min.Z, 3);
        Assert.Equal(0f, min.Y, 3);
    }

    [Fact]
    public void ClockwiseWindingIsFlippedToMatchNormals()
    {
        var (p, n, uv, idx) = SceneFactory.Box(-0.05f, 0.25f, 0.02f, 0.04f);
        var cw = new List<int>();
        for (int i = 0; i < idx.Count; i += 3) { cw.Add(idx[i]); cw.Add(idx[i + 2]); cw.Add(idx[i + 1]); }
        var b = MeshBuilder.Build(SceneFactory.Wrap(SceneFactory.Model("cw", SceneFactory.MakeMesh(p, n, uv, cw, null))), new MeshBuildOptions { MaterialPrefix = "halo/x" });
        Assert.True(b.WindingFlipped);
        AssertWindingMatchesNormals(b);

        var ok = MeshBuilder.Build(SceneFactory.Wrap(SceneFactory.Model("ccw", SceneFactory.MakeMesh(p, n, uv, idx, null))), new MeshBuildOptions { MaterialPrefix = "halo/x" });
        Assert.False(ok.WindingFlipped);
        AssertWindingMatchesNormals(ok);
    }

    [Fact]
    public void MissingNormalsAreRebuiltFromFaces()
    {
        var (p, _, uv, idx) = SceneFactory.Box(-0.05f, 0.25f, 0.02f, 0.04f);
        var b = MeshBuilder.Build(SceneFactory.Wrap(SceneFactory.Model("nn", SceneFactory.MakeMesh(p, null, uv, idx, null))), new MeshBuildOptions { MaterialPrefix = "halo/x" });
        var s = b.Mesh.Submeshes[0];
        for (int i = 0; i < s.VertexCount; i++)
            Assert.Equal(1f, new Vector3(s.Vertices[i * 8 + 3], s.Vertices[i * 8 + 4], s.Vertices[i * 8 + 5]).Length(), 3);
        AssertWindingMatchesNormals(b);
    }

    [Fact]
    public void TriangleStripsAreUnstripped()
    {
        // a quad as a 4-index strip: 2 triangles
        var p = new List<Vector3> { new(0, 0, 0), new(0.1f, 0, 0), new(0, 0, 0.1f), new(0.1f, 0, 0.1f) };
        var nrm = Enumerable.Repeat(-Vector3.UnitY, 4).ToList();
        var mesh = SceneFactory.MakeMesh(p, nrm, null, new List<int> { 0, 1, 2, 3 }, null, IndexFormat.TriangleStrip);
        var b = MeshBuilder.Build(SceneFactory.Wrap(SceneFactory.Model("s", mesh)), new MeshBuildOptions { MaterialPrefix = "halo/x" });
        Assert.Equal(2, b.Mesh.TriangleCount);
    }

    [Fact]
    public void EffectSegmentsAreLeftOutAndMaterialsNumbered()
    {
        var (p, n, uv, idx) = SceneFactory.Box(-0.05f, 0.25f, 0.02f, 0.04f);
        var body = SceneFactory.MakeMaterial(1, "body", AlphaMode.Opaque, null);
        var glass = SceneFactory.MakeMaterial(2, "glass", AlphaMode.Blend, null);
        var glow = SceneFactory.MakeMaterial(3, "glow", AlphaMode.Add, null);
        var mesh = SceneFactory.MakeMesh(p, n, uv, idx, body);
        mesh.Segments.Clear();
        mesh.Segments.Add(new MeshSegment { IndexStart = 0, IndexLength = 12, Material = glow });
        mesh.Segments.Add(new MeshSegment { IndexStart = 12, IndexLength = 12, Material = body });
        mesh.Segments.Add(new MeshSegment { IndexStart = 24, IndexLength = 12, Material = glass });
        var b = MeshBuilder.Build(SceneFactory.Wrap(SceneFactory.Model("m", mesh)), new MeshBuildOptions { MaterialPrefix = "halo/ar" });
        Assert.Equal(2, b.Mesh.Submeshes.Count);
        Assert.Equal(new[] { "halo/ar/0", "halo/ar/1" }, b.Mesh.Submeshes.Select(s => s.Material));
        Assert.Equal(new[] { "body", "glass" }, b.Materials.Select(m => m!.Name));
        Assert.Contains(b.Notes, s => s.Contains("additive"));
        Assert.Equal(8, b.Mesh.TriangleCount);
    }

    [Fact]
    public void OnePermutationPerRegionVariantFirstThenBase()
    {
        var (p, n, uv, idx) = SceneFactory.Box(0, 0.1f, 0.01f, 0.01f);
        var model = new Model { Name = "v" };
        model.Meshes.Add(SceneFactory.MakeMesh(p, n, uv, idx, null));
        model.Meshes.Add(SceneFactory.MakeMesh(p.Select(v => v + new Vector3(1, 0, 0)).ToList(), n, uv, idx, null));
        model.Meshes.Add(SceneFactory.MakeMesh(p.Select(v => v + new Vector3(2, 0, 0)).ToList(), n, uv, idx, null));
        var region = new ModelRegion { Name = "body" };
        region.Permutations.Add(new ModelPermutation { Name = "damaged", MeshRange = (0, 1) });
        region.Permutations.Add(new ModelPermutation { Name = "base", MeshRange = (1, 1) });
        region.Permutations.Add(new ModelPermutation { Name = "gold", MeshRange = (2, 1) });
        model.Regions.Add(region);
        var notes = new List<string>();
        Assert.Equal("base", MeshBuilder.ChoosePermutations(model, null, notes).Single().P.Name);
        Assert.Equal("gold", MeshBuilder.ChoosePermutations(model, new Dictionary<string, string> { ["body"] = "gold" }, notes).Single().P.Name);
        Assert.Equal("base", MeshBuilder.ChoosePermutations(model, new Dictionary<string, string> { ["body"] = "missing" }, notes).Single().P.Name);

        // instanced geometry follows its owner permutation
        var inst = new ModelRegion { Name = MeshBuilder.InstancesRegion };
        var i1 = new ModelPermutation { Name = "scope", MeshRange = (0, 1), IsInstanced = true };
        i1.CustomProperties.Add(MeshBuilder.InstanceOwnersProperty, new[] { @"body\base" });
        var i2 = new ModelPermutation { Name = "gold_trim", MeshRange = (2, 1), IsInstanced = true };
        i2.CustomProperties.Add(MeshBuilder.InstanceOwnersProperty, new[] { @"body\gold" });
        inst.Permutations.Add(i1); inst.Permutations.Add(i2);
        model.Regions.Add(inst);
        var chosen = MeshBuilder.ChoosePermutations(model, null, notes).Select(c => c.P.Name).ToList();
        Assert.Equal(new[] { "base", "scope" }, chosen);
    }

    [Fact]
    public void EmptySceneGivesNoTriangles()
    {
        var b = MeshBuilder.Build(new Scene(), new MeshBuildOptions { MaterialPrefix = "halo/x" });
        Assert.Equal(0, b.Mesh.TriangleCount);
    }

    [Fact]
    public void BadIndicesAreDroppedNotThrown()
    {
        var (p, n, uv, idx) = SceneFactory.Box(0, 0.3f, 0.02f, 0.04f);
        var bad = idx.ToList(); bad[0] = 999;
        var b = MeshBuilder.Build(SceneFactory.Wrap(SceneFactory.Model("b", SceneFactory.MakeMesh(p, n, uv, bad, null))), new MeshBuildOptions { MaterialPrefix = "halo/x" });
        Assert.Equal(11, b.Mesh.TriangleCount);
        Assert.Contains(b.Notes, s => s.Contains("bad indices"));
        CthExtract.Core.Validate.Mesh(b.Mesh);
    }

    private static void AssertWindingMatchesNormals(BuiltMesh b)
    {
        foreach (var s in b.Mesh.Submeshes)
            for (int t = 0; t < s.Indices.Count; t += 3)
            {
                Vector3 P(uint i) => new(s.Vertices[(int)i * 8], s.Vertices[(int)i * 8 + 1], s.Vertices[(int)i * 8 + 2]);
                Vector3 N(uint i) => new(s.Vertices[(int)i * 8 + 3], s.Vertices[(int)i * 8 + 4], s.Vertices[(int)i * 8 + 5]);
                var g = Vector3.Cross(P(s.Indices[t + 1]) - P(s.Indices[t]), P(s.Indices[t + 2]) - P(s.Indices[t]));
                Assert.True(Vector3.Dot(g, N(s.Indices[t])) > 0, "triangle winding disagrees with its normal");
            }
    }

    private sealed class Vec3Comparer : IEqualityComparer<Vector3>
    {
        private readonly float eps;
        public Vec3Comparer(float eps) => this.eps = eps;
        public bool Equals(Vector3 a, Vector3 b) => (a - b).Length() <= eps;
        public int GetHashCode(Vector3 v) => 0;
    }
}
