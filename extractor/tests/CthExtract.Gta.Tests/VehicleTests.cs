using System.Numerics;
using CthExtract.Core;
using CthExtract.Gta.Resources;
using CthExtract.Gta.Tests.Synth;
using CthExtract.Gta.Vehicles;
using Xunit;

namespace CthExtract.Gta.Tests;

public class FragmentReaderTests
{
    private static FragmentData ReadSynth(string name = "infernus") => FragmentReader.Read(Resource.FromRaw(SynthCar.Yft(name), name + ".yft"));

    [Fact]
    public void Reads_drawable_shaders_skeleton_lods_and_children()
    {
        var f = ReadSynth();
        Assert.Equal("infernus", f.Name);
        Assert.Empty(f.Drawable.Problems);
        Assert.Equal(8, f.Drawable.Shaders.Count);
        Assert.Equal("vehicle_paint1", f.Drawable.Shaders[0].KnownName);
        Assert.Equal("infernus_paint_d", f.Drawable.Shaders[0].Textures[ShaderParams.DiffuseSampler]);
        Assert.Equal("synth_badge_n", f.Drawable.Shaders[1].Textures[ShaderParams.BumpSampler]);
        Assert.Equal(new[] { 0.2f, 0.4f, 0.6f, 1f }, f.Drawable.Shaders[6].Vectors[ShaderParams.DiffuseColor]);
        Assert.Equal(6, f.Drawable.Bones.Count);
        Assert.Equal("wheel_lf", f.Drawable.Bones[2].Name);
        Assert.Equal(27922, f.Drawable.Bones[2].Tag);
        Assert.Equal(0, f.Drawable.Bones[2].Parent);
        Assert.Single(f.Drawable.EmbeddedTextures);
        Assert.Equal(2, f.Drawable.Lods[0].Count);
        Assert.Equal(2, f.Drawable.Lods[1].Count);
        Assert.Empty(f.Drawable.Lods[2]);
        int high = f.Drawable.TriangleCount(0), med = f.Drawable.TriangleCount(1);
        Assert.Equal(6 * SynthCar.HighBodySubdiv * SynthCar.HighBodySubdiv * 2 + 8 * 12, high);
        Assert.Equal(9 * 12, med);
        Assert.Equal(5, f.Children.Count);
        Assert.Equal(SynthCar.PositionOffset, f.PositionOffset);
        var lf = f.Children.Single(c => c.BoneTag == 27922);
        Assert.NotNull(lf.Drawable);
        Assert.True(lf.Drawable!.HasGeometry);
        Assert.Equal(SynthCar.WheelLf, lf.Transform!.Value.Translation); // offset added back
        Assert.Null(f.Children.Single(c => c.BoneTag == 26418).Drawable);
    }

    [Fact]
    public void Bone_bound_model_is_posed_and_vertex_formats_decode()
    {
        var f = ReadSynth();
        var door = f.Drawable.Lods[1].Single(m => m.BoneIndex == 1);
        var centre = door.Geometries[0].Positions.Aggregate(Vector3.Zero, (a, p) => a + p) / door.Geometries[0].Positions.Length;
        Assert.True(Vector3.Distance(centre, SynthCar.DoorPose) < 1e-4f, $"door centre {centre}");
        var body = f.Drawable.Lods[1].Single(m => m.BoneIndex == 0);
        // geometry 1 was written with half-float UVs, geometry 2 with an extra colour channel
        Assert.All(body.Geometries[1].Uvs, uv => Assert.InRange(uv.X, 0f, 1f));
        Assert.Contains(body.Geometries[1].Uvs, uv => uv.X > 0.99f);
        Assert.Contains(body.Geometries[2].Uvs, uv => uv.Y > 0.99f);
        Assert.All(body.Geometries[2].Normals, n => Assert.InRange(n.Length(), 0.99f, 1.01f));
    }

    [Fact]
    public void Without_a_pose_block_models_use_the_skeleton_default_pose()
    {
        var f = SynthCar.Fragment("noposecar");
        f.Pose = Array.Empty<Matrix4x4>();
        var frag = FragmentReader.Read(Resource.FromRaw(FragWriter.Yft(f), "nopose.yft"));
        Assert.Empty(frag.BonePose);
        var door = frag.Drawable.Lods[1].Single(m => m.BoneIndex == 1);
        var centre = door.Geometries[0].Positions.Aggregate(Vector3.Zero, (a, p) => a + p) / door.Geometries[0].Positions.Length;
        Assert.True(Vector3.Distance(centre, SynthCar.DoorPose) < 1e-4f, $"door centre {centre}");
    }

    [Fact]
    public void Skeleton_pose_composes_rotation_and_translation_up_the_parent_chain()
    {
        var bones = new List<BoneInfo>
        {
            new() { Name = "root", Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), Translation = new Vector3(1, 0, 0) },
            new() { Name = "child", Parent = 0, Translation = new Vector3(0, 1, 0) },
        };
        var m = DrawableReader.BonePose(bones, 1)!.Value;
        var origin = Vector3.Transform(Vector3.Zero, m);
        Assert.True(Vector3.Distance(origin, Vector3.Zero) < 1e-5f, $"child origin {origin}");
        var x = Vector3.Transform(Vector3.UnitX, m); // child's +x is the parent's rotated +x: (0,1,0) + origin
        Assert.True(Vector3.Distance(x, new Vector3(0, 1, 0)) < 1e-5f, $"child +x {x}");
        Assert.Null(DrawableReader.BonePose(bones, 5));
    }

    [Fact]
    public void Vertex_layout_resolves_ambiguous_types_by_stride()
    {
        // type 2 is 6 bytes (half3) in one reading and 4 (float) in the other: the stride decides
        uint flags = 0b1001; // position + normal
        ulong types = 6UL | (2UL << 12);
        Assert.NotNull(DrawableReader.Layout(flags, types, 12 + 6, out var s1));
        Assert.Equal(6, s1[3]);
        Assert.NotNull(DrawableReader.Layout(flags, types, 12 + 4, out var s2));
        Assert.Equal(4, s2[3]);
        Assert.Null(DrawableReader.Layout(flags, types, 40, out _));
    }

    [Fact]
    public void Damaged_resource_reports_instead_of_crashing()
    {
        var bytes = SynthCar.Yft("blista");
        var r = Resource.FromRaw(bytes, "blista.yft");
        // break the drawable pointer
        BitConverter.GetBytes(0x5FFFFFF0UL).CopyTo(r.Data, 0x30);
        Assert.Throws<InvalidDataException>(() => FragmentReader.Read(r));
    }
}

public class VehicleBuilderTests
{
    private static readonly Dictionary<string, TextureEntry> Library = BuildLibrary();

    private static Dictionary<string, TextureEntry> BuildLibrary()
    {
        var d = new Dictionary<string, TextureEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var ytd in new[] { SynthCar.ModelYtd("infernus"), SynthCar.InteriorYtd(), SynthCar.VehshareYtd() })
            foreach (var t in TextureDictionaryReader.Read(Resource.FromRaw(ytd, "lib"), 0)) d[t.Name] = t;
        return d;
    }

    private static VehicleBuilder.Result Build(string model = "infernus", int budget = 8000)
    {
        var frag = FragmentReader.Read(Resource.FromRaw(SynthCar.Yft(model), model + ".yft"));
        using var log = new Log(null);
        return new VehicleBuilder(model, frag, n => Library.GetValueOrDefault(n), budget, log).Build();
    }

    private static (Vector3 min, Vector3 max) Bounds(IEnumerable<MeshData.Submesh> subs)
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (var s in subs)
            for (int i = 0; i < s.Vertices.Count; i += 8)
            {
                var p = new Vector3(s.Vertices[i], s.Vertices[i + 1], s.Vertices[i + 2]);
                min = Vector3.Min(min, p); max = Vector3.Max(max, p);
            }
        return (min, max);
    }

    [Fact]
    public void Car_is_centred_grounded_and_faces_plus_z()
    {
        var r = Build();
        var (min, max) = Bounds(r.Mesh.Submeshes);
        Assert.Equal(0f, min.Y, 4);
        Assert.Equal(0f, (min.X + max.X) / 2, 4);
        Assert.Equal(0f, (min.Z + max.Z) / 2, 4);
        Assert.True(max.Z - min.Z > max.X - min.X, "length runs along Z");
        // the badge marker sits on the GTA front (+y): it must end up at +Z
        var badge = r.Mesh.Submeshes.Where(s => s.Material == "gta/infernus/synth_badge" && s.Node == -1).ToList();
        Assert.NotEmpty(badge);
        Assert.True(Bounds(badge).min.Z > 1.5f, $"front marker at z {Bounds(badge).min.Z}");
        // the GTA up axis is +Y now: the glass is the highest body part
        var glass = r.Mesh.Submeshes.Single(s => s.Material.EndsWith("_glass"));
        Assert.True(Math.Abs(Bounds(new[] { glass }).max.Y - max.Y) < 0.11f);
    }

    [Fact]
    public void Wheel_nodes_follow_the_contract_and_sit_at_the_wheel_centres()
    {
        var r = Build();
        Assert.Equal(new[] { "wheel_lf", "wheel_rf", "wheel_lb", "wheel_rb" }, r.Mesh.Nodes.Select(n => n.Name));
        // GTA (x, y, z) -> MC (-x, z, y): +x (right) -> -X, +y (front) -> +Z, +z (up) -> +Y
        Assert.Equal(new Vector3(-1, 3, 2), VehicleBuilder.ToMc(new Vector3(1, 2, 3)));
        var lf = r.Mesh.Nodes[0];
        // left wheels are on +X in Minecraft (facing +Z, left is +X), front wheels at +Z
        Assert.True(lf.X > 0.5f && lf.Z > 1f, $"wheel_lf at {lf.X},{lf.Y},{lf.Z}");
        var rb = r.Mesh.Nodes[3];
        Assert.True(rb.X < -0.5f && rb.Z < -1f, $"wheel_rb at {rb.X},{rb.Y},{rb.Z}");
        // wheels touch the ground: the pivot height equals the radius
        Assert.All(r.Mesh.Nodes, n => Assert.Equal(SynthCar.WheelRadius, n.Y, 3));
        // left/right symmetric
        Assert.Equal(r.Mesh.Nodes[0].X, -r.Mesh.Nodes[1].X, 3);
        Assert.Equal(r.Mesh.Nodes[0].Z, r.Mesh.Nodes[1].Z, 3);
        Assert.Equal(SynthCar.WheelLf.Y - SynthCar.WheelLr.Y, r.Mesh.Nodes[0].Z - r.Mesh.Nodes[2].Z, 3);
        Assert.Equal(-2 * SynthCar.WheelLf.X, r.Mesh.Nodes[0].X - r.Mesh.Nodes[1].X, 3);
    }

    [Fact]
    public void Every_wheel_gets_geometry_and_right_hubs_face_outwards()
    {
        var r = Build();
        for (int node = 0; node < 4; node++)
        {
            var subs = r.Mesh.Submeshes.Where(s => s.Node == node).ToList();
            Assert.NotEmpty(subs);
            var n = r.Mesh.Nodes[node];
            var hub = subs.Single(s => s.Material == "gta/infernus/synth_badge");
            var hb = Bounds(new[] { hub });
            float hubX = (hb.min.X + hb.max.X) / 2;
            bool left = node is 0 or 2;
            // hub marker is on the outer side: left wheels -> further +X than the pivot, right wheels -> further -X
            if (left) Assert.True(hubX > n.X, $"node {node}: hub {hubX} pivot {n.X}");
            else Assert.True(hubX < n.X, $"node {node}: hub {hubX} pivot {n.X}");
        }
    }

    [Fact]
    public void Triangle_winding_agrees_with_normals_everywhere_including_rotated_wheels()
    {
        var r = Build();
        foreach (var s in r.Mesh.Submeshes)
            for (int t = 0; t < s.Indices.Count; t += 3)
            {
                Vector3 P(uint i) => new(s.Vertices[(int)i * 8], s.Vertices[(int)i * 8 + 1], s.Vertices[(int)i * 8 + 2]);
                Vector3 N(uint i) => new(s.Vertices[(int)i * 8 + 3], s.Vertices[(int)i * 8 + 4], s.Vertices[(int)i * 8 + 5]);
                var a = P(s.Indices[t]); var b = P(s.Indices[t + 1]); var c = P(s.Indices[t + 2]);
                var geo = Vector3.Cross(b - a, c - a);
                if (geo.LengthSquared() < 1e-12f) continue;
                Assert.True(Vector3.Dot(geo, N(s.Indices[t])) > 0, $"{s.Material} node {s.Node} triangle {t / 3} is wound against its normal");
            }
    }

    [Fact]
    public void Materials_use_paint_textures_chain_and_fallbacks()
    {
        var r = Build("police");
        var mats = r.Mesh.Submeshes.Select(s => s.Material).Distinct().ToList();
        Assert.Contains("gta/police/synth_badge", mats);             // model ytd
        Assert.Contains("gta/police/synth_interior", mats);          // via txdRelationships parent (library here)
        Assert.Contains("gta/police/vehicle_genericglass_glass", mats); // vehshare
        Assert.Contains("gta/police/synth_embedded", mats);          // embedded in the yft
        Assert.Contains("gta/police/synth_tyre", mats);
        Assert.Contains(mats, m => m.StartsWith("gta/police/paint_primary"));
        Assert.Contains("gta/police/paint_secondary", mats);
        Assert.Contains("gta/police/flat_vehicle_track", mats);
        Assert.All(mats, m => Assert.True(r.Textures.ContainsKey(m), $"texture for {m}"));
        // police: white primary, black secondary
        var sec = r.Textures["gta/police/paint_secondary"];
        Assert.True(sec.Pixels[0] < 40 && sec.Pixels[1] < 40 && sec.Pixels[2] < 40);
        var pri = r.Textures[mats.First(m => m.StartsWith("gta/police/paint_primary"))];
        Assert.Equal(255, pri.Pixels[3]);
        Assert.Contains(Enumerable.Range(0, pri.Pixels.Length / 4), i => pri.Pixels[i * 4] > 200 && pri.Pixels[i * 4 + 1] > 200 && pri.Pixels[i * 4 + 2] > 200);
        var flat = r.Textures["gta/police/flat_vehicle_track"];
        Assert.Equal(new byte[] { 51, 102, 153, 255 }, flat.Pixels.Take(4).ToArray());
        // decals keep their alpha (cut-out), under their own material name
        var decal = r.Textures["gta/police/synth_decal_alpha"];
        Assert.Contains(Enumerable.Range(0, decal.Pixels.Length / 4), i => decal.Pixels[i * 4 + 3] == 0);
        Assert.Contains(Enumerable.Range(0, decal.Pixels.Length / 4), i => decal.Pixels[i * 4 + 3] == 255);
        // diffuse alpha (a specular mask in GTA) is made opaque except for decals
        Assert.All(Enumerable.Range(0, r.Textures["gta/police/synth_badge"].Pixels.Length / 4), i => Assert.Equal(255, r.Textures["gta/police/synth_badge"].Pixels[i * 4 + 3]));
    }

    [Fact]
    public void Missing_textures_fall_back_to_grey_without_failing()
    {
        var frag = FragmentReader.Read(Resource.FromRaw(SynthCar.Yft("blista"), "blista.yft"));
        using var log = new Log(null);
        var r = new VehicleBuilder("blista", frag, _ => null, 8000, log).Build();
        Assert.True(r.Mesh.TriangleCount > 0);
        Assert.True(r.Textures.ContainsKey("gta/blista/synth_badge"));
        Assert.Equal(128, r.Textures["gta/blista/synth_badge"].Pixels[0]);
        Assert.True(r.Textures.ContainsKey("gta/blista/synth_embedded")); // still found: embedded
    }

    [Fact]
    public void Lod_choice_respects_the_triangle_budget()
    {
        var high = Build(budget: 8000);
        Assert.Equal(0, high.Lod);
        var med = Build(budget: 500);
        Assert.Equal(1, med.Lod);
        Assert.True(med.Mesh.TriangleCount <= 625);
        Assert.Contains(med.Notes, n => n.Contains("LOD Medium"));
        var tiny = Build(budget: 10); // nothing fits: the smallest LOD is taken, decimation does the rest
        Assert.Equal(1, tiny.Lod);
    }

    [Fact]
    public void Cthm_written_through_the_context_roundtrips()
    {
        var r = Build();
        using var cache = new TempCache();
        foreach (var (m, img) in r.Textures) cache.Ctx.MaterialTexture(m, img);
        cache.Ctx.Mesh("gta_mesh_infernus", r.Mesh, "synthetic");
        var back = Cthm.Read(cache.Asset("meshes/gta/infernus.cthm"));
        Assert.Equal(r.Mesh.TriangleCount, back.TriangleCount);
        Assert.Equal(4, back.Nodes.Count);
        foreach (var s in back.Submeshes) Assert.True(File.Exists(cache.Asset("textures/" + s.Material + ".png")), s.Material);
    }
}
