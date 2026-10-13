using System.Numerics;
using CthExtract.Gta.Resources;

namespace CthExtract.Gta.Tests.Synth;

/// <summary>
/// A synthetic box car in GTA model space (metres, x right, y forward, z up): a body box, a marker on the front
/// (+y), glass, interior, a door bound to a posed bone, four wheels from physics children (only wheel_lf has a
/// drawable; its hub marker sits on the outer, -x side). High LOD is subdivided, Medium is coarse.
/// Textures: the model ytd, vehicles_synth_interior.ytd (reached through txdRelationships), vehshare.ytd and one
/// texture embedded in the yft.
/// </summary>
public static class SynthCar
{
    public static readonly Vector3 WheelLf = new(-0.8f, 1.3f, 0.4f), WheelRf = new(0.8f, 1.3f, 0.4f),
                                   WheelLr = new(-0.8f, -1.3f, 0.4f), WheelRr = new(0.8f, -1.3f, 0.4f);
    public static readonly Vector3 DoorPose = new(-0.9f, 0.5f, 0.8f);
    public static readonly Vector3 PositionOffset = new(0, 0, 0.05f);
    public const float WheelRadius = 0.4f;
    public const int HighBodySubdiv = 12;

    public static SynthFrag Fragment(string name)
    {
        var main = new SynthDrawable();
        main.Shaders.Add(new SynthShader { Name = "vehicle_paint1", Textures = { ["DiffuseSampler"] = $"{name}_paint_d" } });     // 0
        main.Shaders.Add(new SynthShader { Name = "vehicle_mesh", Textures = { ["DiffuseSampler"] = "synth_badge", ["BumpSampler"] = "synth_badge_n" } }); // 1
        main.Shaders.Add(new SynthShader { Name = "vehicle_vehglass", Textures = { ["DiffuseSampler"] = "vehicle_genericglass" } }); // 2
        main.Shaders.Add(new SynthShader { Name = "vehicle_interior", Textures = { ["DiffuseSampler"] = "synth_interior" } }); // 3
        main.Shaders.Add(new SynthShader { Name = "vehicle_mesh", Textures = { ["DiffuseSampler"] = "synth_embedded" } }); // 4
        main.Shaders.Add(new SynthShader { Name = "vehicle_paint2" }); // 5: secondary paint, no texture
        main.Shaders.Add(new SynthShader { Name = "vehicle_track", Vectors = { ["matDiffuseColor"] = new[] { 0.2f, 0.4f, 0.6f, 1f } } }); // 6
        main.Shaders.Add(new SynthShader { Name = "vehicle_decal", Textures = { ["DiffuseSampler"] = "synth_decal" } }); // 7
        main.Embedded.Add(new SynthTexture("synth_embedded", TextureWriter.Pattern(16, 16, 9), TextureFormats.A8R8G8B8));
        main.Bones.AddRange(new[]
        {
            new SynthBone { Name = "chassis" },
            new SynthBone { Name = "door_dside_f", Parent = 0, T = DoorPose },
            new SynthBone { Name = "wheel_lf", Parent = 0, T = WheelLf },
            new SynthBone { Name = "wheel_rf", Parent = 0, T = WheelRf },
            new SynthBone { Name = "wheel_lr", Parent = 0, T = WheelLr },
            new SynthBone { Name = "wheel_rr", Parent = 0, T = WheelRr },
        });
        for (int lod = 0; lod < 2; lod++)
        {
            int n = lod == 0 ? HighBodySubdiv : 1;
            var body = new SynthModel { Bone = 0 };
            body.Geoms.Add(new SynthGeometry { Mesh = SynthMesh.Box(new(-0.9f, -2.2f, 0.3f), new(0.9f, 2.2f, 1.3f), n), Shader = 0 });
            body.Geoms.Add(new SynthGeometry { Mesh = SynthMesh.Box(new(-0.3f, 2.0f, 0.9f), new(0.3f, 2.3f, 1.0f)), Shader = 1, VertexFormat = 1 });
            body.Geoms.Add(new SynthGeometry { Mesh = SynthMesh.Box(new(-0.8f, 0.2f, 1.3f), new(0.8f, 1.0f, 1.7f)), Shader = 2, VertexFormat = 2 });
            body.Geoms.Add(new SynthGeometry { Mesh = SynthMesh.Box(new(-0.5f, -0.5f, 0.5f), new(0.5f, 0.5f, 0.9f)), Shader = 3 });
            body.Geoms.Add(new SynthGeometry { Mesh = SynthMesh.Box(new(-0.2f, -2.3f, 0.6f), new(0.2f, -2.2f, 0.8f)), Shader = 4 });
            body.Geoms.Add(new SynthGeometry { Mesh = SynthMesh.Box(new(-0.9f, -2.2f, 0.3f), new(0.9f, -1.8f, 0.4f)), Shader = 5 });
            body.Geoms.Add(new SynthGeometry { Mesh = SynthMesh.Box(new(-0.1f, -0.1f, 1.7f), new(0.1f, 0.1f, 1.8f)), Shader = 6 });
            body.Geoms.Add(new SynthGeometry { Mesh = SynthMesh.Box(new(-0.91f, -1.0f, 0.6f), new(-0.9f, 1.0f, 1.0f)), Shader = 7 });
            main.Lods[lod].Add(body);
            var door = new SynthModel { Bone = 1 };
            door.Geoms.Add(new SynthGeometry { Mesh = SynthMesh.Box(new(-0.05f, -0.4f, -0.3f), new(0.05f, 0.4f, 0.3f)), Shader = 0 });
            main.Lods[lod].Add(door);
        }

        var wheel = new SynthDrawable();
        wheel.Shaders.Add(new SynthShader { Name = "vehicle_tire", Textures = { ["DiffuseSampler"] = "synth_tyre" } });
        wheel.Shaders.Add(new SynthShader { Name = "vehicle_mesh", Textures = { ["DiffuseSampler"] = "synth_badge" } });
        for (int lod = 0; lod < 2; lod++)
        {
            var m = new SynthModel { Bone = 0 };
            m.Geoms.Add(new SynthGeometry { Mesh = SynthMesh.Box(new(-0.15f, -WheelRadius, -WheelRadius), new(0.15f, WheelRadius, WheelRadius), lod == 0 ? 3 : 1), Shader = 0 });
            m.Geoms.Add(new SynthGeometry { Mesh = SynthMesh.Box(new(-0.2f, -0.1f, -0.1f), new(-0.15f, 0.1f, 0.1f)), Shader = 1 }); // hub on the outer (-x) side
            wheel.Lods[lod].Add(m);
        }

        Matrix4x4 T(Vector3 v) => Matrix4x4.CreateTranslation(v);
        return new SynthFrag
        {
            Name = name,
            Main = main,
            Pose = new[] { Matrix4x4.Identity, T(DoorPose), T(WheelLf), T(WheelRf), T(WheelLr), T(WheelRr) },
            PositionOffset = PositionOffset,
            Children =
            {
                new SynthChild { Bone = "chassis", Transform = Matrix4x4.Identity },
                new SynthChild { Bone = "wheel_lf", Transform = T(WheelLf - PositionOffset), Drawable = wheel },
                new SynthChild { Bone = "wheel_rf", Transform = T(WheelRf - PositionOffset) },
                new SynthChild { Bone = "wheel_lr", Transform = T(WheelLr - PositionOffset) },
                new SynthChild { Bone = "wheel_rr", Transform = T(WheelRr - PositionOffset) },
            },
        };
    }

    public static byte[] Yft(string name, int version = 162) => FragWriter.Yft(Fragment(name), version);

    public static byte[] ModelYtd(string name) => TextureWriter.Ytd(new[]
    {
        new SynthTexture($"{name}_paint_d", TextureWriter.Pattern(32, 32, 1, alpha: true), TextureFormats.DXT5),
        new SynthTexture("synth_badge", TextureWriter.Pattern(64, 32, 2), TextureFormats.DXT1),
        new SynthTexture("synth_tyre", TextureWriter.Solid(16, 16, 30, 30, 30), TextureFormats.BC7),
        new SynthTexture("synth_decal", TextureWriter.Pattern(16, 16, 7, alpha: true), TextureFormats.A8R8G8B8),
    });

    public static byte[] InteriorYtd() => TextureWriter.Ytd(new[]
    {
        new SynthTexture("synth_interior", TextureWriter.Solid(8, 8, 200, 100, 50), TextureFormats.A8B8G8R8),
    });

    public static byte[] VehshareYtd() => TextureWriter.Ytd(new[]
    {
        new SynthTexture("vehicle_genericglass", TextureWriter.Solid(8, 8, 120, 140, 160, 60), TextureFormats.A8R8G8B8),
        new SynthTexture("vehicle_genericmud_car", TextureWriter.Solid(8, 8, 90, 70, 40), TextureFormats.L8),
    });

    public static string VehiclesMeta(params string[] models)
    {
        var sb = new System.Text.StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<CVehicleModelInfo__InitDataList>\n  <InitDatas>\n");
        foreach (var m in models) sb.Append($"    <Item type=\"CVehicleModelInfo__InitData\">\n      <modelName>{m}</modelName>\n      <txdName>{m}</txdName>\n    </Item>\n");
        sb.Append("  </InitDatas>\n  <txdRelationships>\n");
        sb.Append("    <Item>\n      <parent>vehshare</parent>\n      <child>vehicles_synth_interior</child>\n    </Item>\n");
        foreach (var m in models) sb.Append($"    <Item>\n      <parent>vehicles_synth_interior</parent>\n      <child>{m}</child>\n    </Item>\n");
        sb.Append("  </txdRelationships>\n</CVehicleModelInfo__InitDataList>\n");
        return sb.ToString();
    }
}
