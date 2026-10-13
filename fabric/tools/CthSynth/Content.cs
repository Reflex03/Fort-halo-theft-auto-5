using System.Diagnostics;
using System.Text.Json.Nodes;
using CthExtract.Core;

namespace CthSynth;

/// <summary>Writes one synthetic asset per sources row through ExtractContext, exactly where the real modules would.</summary>
public static class Content
{
    const uint Black = 0xFF101010, White = 0xFFF0F0F0;

    public static void Write(ExtractContext ctx, string id)
    {
        var row = ctx.Row(id);
        switch (row.Kind)
        {
            case "mesh": Mesh(ctx, id); break;
            case "sound": ctx.Sound(id, Tone(id), "synthetic tone (ffmpeg libvorbis)"); break;
            case "icon": Icon(ctx, id); break;
            case "stats": Stats(ctx, id); break;
            default: ctx.Failed(id, "synthetic: unknown kind " + row.Kind); break;
        }
    }

    // ------------------------------------------------------------------ meshes
    static void Mesh(ExtractContext ctx, string id)
    {
        string name = Path.GetFileNameWithoutExtension(ctx.Row(id).Out);
        string game = ctx.Short;
        Shapes s;
        Dictionary<string, Rgba> tex;
        if (game == "halo") (s, tex) = Gun(name);
        else if (game == "gta") (s, tex) = Car(name);
        else if (name == "battle_bus") (s, tex) = Bus();
        else if (name == "glider") (s, tex) = Glider();
        else (s, tex) = Chest();
        foreach (var (mat, img) in tex) ctx.MaterialTexture(mat, img);
        ctx.Mesh(id, s.Mesh, "synthetic shape");
    }

    static Rgba Tex(uint a, uint b, int cell = 8, int size = 64) => new Pixels(size, size).Checker(cell, a, b).Mark(White).ToRgba();

    static (Shapes, Dictionary<string, Rgba>) Gun(string w)
    {
        string m0 = $"halo/{w}/0", m1 = $"halo/{w}/1";
        var tex = new Dictionary<string, Rgba>
        {
            [m0] = w.Contains("plasma") || w == "needler" ? Tex(0xFF7A2BD0, 0xFF4E1A90) : Tex(0xFF3C6E3C, 0xFF2A4E2A),
            [m1] = Tex(0xFF30D0D0, 0xFF1A8080, 4),
        };
        var s = new Shapes();
        if (w.Contains("grenade"))
        {
            s.Sphere(m0, 0, 0, 0, 0.07f, w.Contains("plasma") ? 0.07f : 0.09f, 0.07f);
            s.Box(m1, -0.015f, 0.07f, -0.015f, 0.015f, 0.11f, 0.015f);
            return (s, tex);
        }
        float len = w switch { "magnum" => 0.32f, "needler" => 0.45f, "plasma_rifle" => 0.6f, "assault_rifle" => 0.8f, "battle_rifle" => 0.9f, "shotgun" => 1.0f, "sniper_rifle" => 1.35f, "rocket_launcher" => 1.3f, _ => 0.7f };
        bool pistol = len < 0.5f;
        float back = pistol ? -0.08f : -len * 0.32f;
        if (w == "rocket_launcher")
        {
            s.Box(m0, -0.12f, 0.02f, -0.6f, 0.12f, 0.24f, 0.7f);
            s.Box(m1, -0.13f, 0.06f, 0.7f, 0.13f, 0.2f, 0.72f);
            s.Box(m1, -0.03f, -0.15f, -0.04f, 0.03f, 0.02f, 0.04f);
            return (s, tex);
        }
        s.Wedge(m0, -0.04f, 0.0f, back, 0.04f, 0.12f, 0.15f, back + len * 0.7f);   // receiver
        s.Box(m1, -0.018f, 0.07f, back + len * 0.7f, 0.018f, 0.1f, back + len);    // barrel
        s.Box(m0, -0.028f, -0.13f, -0.04f, 0.028f, 0.0f, 0.03f);                    // grip at the origin
        if (!pistol)
        {
            s.Box(m1, -0.025f, -0.12f, 0.08f, 0.025f, 0.0f, 0.16f);                 // magazine
            s.Box(m0, -0.035f, -0.03f, back - len * 0.2f, 0.035f, 0.1f, back);      // stock
        }
        if (w is "battle_rifle" or "sniper_rifle") s.Box(m1, -0.025f, 0.15f, -0.05f, 0.025f, 0.2f, 0.2f);
        if (w == "needler") for (int i = 0; i < 5; i++) s.Box(m1, -0.01f, 0.12f, 0.0f + i * 0.06f, 0.01f, 0.22f, 0.02f + i * 0.06f);
        return (s, tex);
    }

    static (Shapes, Dictionary<string, Rgba>) Car(string car)
    {
        string body = $"gta/{car}/body", glass = $"gta/{car}/glass", wheel = $"gta/{car}/wheel", light = $"gta/{car}/lights";
        uint paint = car switch { "infernus" => 0xFFE8C020, "sultan" => 0xFF2050C0, "blista" => 0xFFC02828, _ => 0xFFF2F2F2 };
        var tex = new Dictionary<string, Rgba>
        {
            [body] = car == "police" ? new Pixels(64, 64).All(paint).Fill(0, 40, 63, 63, 0xFF101828).Mark(Black).ToRgba() : new Pixels(64, 64).All(paint).Fill(0, 0, 63, 3, 0xFFFFFFFF).Mark(Black).ToRgba(),
            [glass] = new Pixels(16, 16).All(0xFF1E2A38).ToRgba(),
            [wheel] = new Pixels(16, 16).Checker(4, 0xFF181818, 0xFF505050).ToRgba(),
            [light] = new Pixels(16, 16).All(0xFFFFF8D0).ToRgba(),
        };
        float w = car == "blista" ? 1.75f : 1.95f, len = car == "infernus" ? 4.6f : car == "blista" ? 3.8f : 4.4f, h = car == "infernus" ? 1.15f : 1.45f, r = 0.34f;
        var s = new Shapes();
        float wx = w / 2 - 0.15f, wz = len / 2 - 0.75f;
        int lf = s.Node("wheel_lf", wx, r, wz), rf = s.Node("wheel_rf", -wx, r, wz), lb = s.Node("wheel_lb", wx, r, -wz), rb = s.Node("wheel_rb", -wx, r, -wz);
        s.Box(body, -w / 2, 0.25f, -len / 2, w / 2, h * 0.55f, len / 2);
        s.Wedge(body, -w / 2 + 0.1f, h * 0.55f, -len / 2 + 0.6f, w / 2 - 0.1f, h, h * 0.75f, len / 2 - 1.2f);
        s.Wedge(glass, -w / 2 + 0.14f, h * 0.6f, len / 2 - 1.22f, w / 2 - 0.14f, h * 0.75f, h * 0.56f, len / 2 - 0.7f);
        s.Box(light, -w / 2 + 0.1f, h * 0.35f, len / 2, -w / 2 + 0.45f, h * 0.48f, len / 2 + 0.02f);
        s.Box(light, w / 2 - 0.45f, h * 0.35f, len / 2, w / 2 - 0.1f, h * 0.48f, len / 2 + 0.02f);
        if (car == "infernus") s.Box(body, -w / 2 + 0.1f, h * 0.7f, -len / 2, w / 2 - 0.1f, h * 0.75f, -len / 2 + 0.35f);
        if (car == "police")
        {
            tex[$"gta/{car}/siren"] = new Pixels(16, 16).Fill(0, 0, 7, 15, 0xFFFF2020).Fill(8, 0, 15, 15, 0xFF2040FF).ToRgba();
            s.Box($"gta/{car}/siren", -0.6f, h, -0.15f, 0.6f, h + 0.13f, 0.15f);
        }
        foreach (var (n, x, z) in new[] { (lf, wx, wz), (rf, -wx, wz), (lb, wx, -wz), (rb, -wx, -wz) }) s.Wheel(wheel, n, x, r, z, r, 0.13f);
        return (s, tex);
    }

    static (Shapes, Dictionary<string, Rgba>) Bus()
    {
        var tex = new Dictionary<string, Rgba>
        {
            ["fortnite/battle_bus/0"] = new Pixels(64, 64).All(0xFF2E8BFF).Fill(0, 24, 63, 39, 0xFFFFFFFF).Mark(Black).ToRgba(),
            ["fortnite/battle_bus/1"] = new Pixels(32, 32).Checker(8, 0xFFFF4FB0, 0xFFFFE04F).ToRgba(),
            ["fortnite/battle_bus/2"] = new Pixels(16, 16).All(0xFF15202C).ToRgba(),
        };
        var s = new Shapes();
        s.Box("fortnite/battle_bus/0", -1.3f, 0.5f, -4.6f, 1.3f, 3.1f, 4.6f);
        s.Box("fortnite/battle_bus/2", -1.31f, 1.8f, -4.0f, 1.31f, 2.6f, 4.0f);
        s.Box("fortnite/battle_bus/2", -1.1f, 1.8f, 4.6f, 1.1f, 2.7f, 4.61f);
        foreach (float z in new[] { 3.2f, -3.2f }) foreach (float x in new[] { 1.15f, -1.15f }) s.Wheel("fortnite/battle_bus/2", -1, x, 0.5f, z, 0.5f, 0.16f);
        s.Sphere("fortnite/battle_bus/1", 0, 8.4f, 0, 3.6f, 3.4f, 3.6f, 14, 24);
        foreach (float x in new[] { -1.0f, 1.0f }) foreach (float z in new[] { -3f, 3f }) s.Box("fortnite/battle_bus/2", x - 0.04f, 3.1f, z - 0.04f, x + 0.04f, 5.2f, z + 0.04f);
        return (s, tex);
    }

    static (Shapes, Dictionary<string, Rgba>) Glider()
    {
        var tex = new Dictionary<string, Rgba> { ["fortnite/glider/0"] = new Pixels(64, 32).Checker(8, 0xFFFF7A00, 0xFF2E8BFF).Mark(White).ToRgba(), ["fortnite/glider/1"] = new Pixels(8, 8).All(0xFF303030).ToRgba() };
        var s = new Shapes();
        s.Wedge("fortnite/glider/0", -2.0f, 1.25f, -0.7f, 2.0f, 1.35f, 1.5f, 0.7f);
        s.Box("fortnite/glider/1", -0.6f, 0.0f, -0.03f, -0.54f, 1.3f, 0.03f);
        s.Box("fortnite/glider/1", 0.54f, 0.0f, -0.03f, 0.6f, 1.3f, 0.03f);
        return (s, tex);
    }

    static (Shapes, Dictionary<string, Rgba>) Chest()
    {
        var tex = new Dictionary<string, Rgba> { ["fortnite/chest/0"] = new Pixels(32, 32).All(0xFFB07A2A).Fill(0, 14, 31, 17, 0xFFFFD040).Mark(Black).ToRgba(), ["fortnite/chest/1"] = new Pixels(8, 8).All(0xFFFFD040).ToRgba() };
        var s = new Shapes();
        s.Box("fortnite/chest/0", -0.45f, 0f, -0.3f, 0.45f, 0.45f, 0.3f);
        s.Wedge("fortnite/chest/0", -0.45f, 0.45f, -0.3f, 0.45f, 0.62f, 0.62f, 0.3f);
        s.Box("fortnite/chest/1", -0.06f, 0.3f, 0.3f, 0.06f, 0.5f, 0.33f);
        return (s, tex);
    }

    // ------------------------------------------------------------------ icons
    static void Icon(ExtractContext ctx, string id)
    {
        string name = Path.GetFileNameWithoutExtension(ctx.Row(id).Out);
        Pixels p;
        if (name == "wanted_star") p = new Pixels(32, 32).Star(16, 17, 15, 6.5f, 0xFFFFD030).Mark(Black);
        else
        {
            uint main = name switch { "bandages" => 0xFFF0F0F0, "med_kit" => 0xFFE04040, "chug_jug" => 0xFF2060E0, _ => 0xFF40A8FF };
            p = new Pixels(64, 64).Disc(32, 32, 30, 0xFF202838).Disc(32, 34, name.Contains("small") ? 16 : 24, main).Fill(28, 4, 36, 14, 0xFFC0C0C0).Mark(White);
        }
        ctx.Texture(id, p.ToRgba(), detail: "synthetic icon");
    }

    // ------------------------------------------------------------------ stats
    static void Stats(ExtractContext ctx, string id)
    {
        if (id == "halo_stats_shield")
        {
            ctx.Stats(id, "shield", new JsonObject { ["max"] = 100, ["rechargeDelayTicks"] = 90, ["rechargePerTick"] = 1.25 });
            ctx.StatsDone(id, "synthetic shield timing");
            return;
        }
        ctx.Stats(id, "assault_rifle", new JsonObject { ["damage"] = 8.0, ["fireIntervalTicks"] = 2, ["clip"] = 32, ["auto"] = true, ["spreadDeg"] = 2.0 });
        ctx.Stats(id, "battle_rifle", new JsonObject { ["damage"] = 6.5, ["burst"] = 3, ["zoom"] = 0.5 });
        ctx.Stats(id, "needler", new JsonObject { ["homing"] = true, ["projectileSpeed"] = 1.5 });
        ctx.Stats(id, "sniper_rifle", new JsonObject { ["damage"] = 80.0, ["zoom"] = 5.0 }); // zoom out of range: the mod must keep the sheet's 0.2
        ctx.StatsDone(id, "synthetic weapon stats (4 weapons)");
    }

    // ------------------------------------------------------------------ sounds (ffmpeg + libvorbis)
    static byte[] Tone(string id)
    {
        string expr = id switch
        {
            var x when x.EndsWith("_fire") => $"0.7*sin(2*PI*{900 + id.Sum(c => (int)c) * 7 % 900}*t)*exp(-25*t)",
            "halo_snd_grenade_throw" => "0.5*sin(2*PI*300*t)*exp(-8*t)",
            "halo_snd_shield_recharge" => "0.5*sin(2*PI*(400+700*t)*t)",
            "halo_snd_shield_depleted" => "0.6*sin(2*PI*(900-600*t)*t)",
            "gta_snd_siren" => "0.5*sin(2*PI*(750+250*sin(2*PI*1.5*t))*t)",
            "gta_snd_horn" => "0.6*sin(2*PI*420*t)+0.3*sin(2*PI*525*t)",
            "fn_snd_chest_open" => "0.6*sin(2*PI*1200*t)*exp(-4*t)",
            _ => "0.5*sin(2*PI*600*t)*exp(-3*t)",
        };
        double dur = id switch { "gta_snd_siren" => 2.0, "halo_snd_shield_recharge" => 1.0, var x when x.EndsWith("_fire") => 0.15, _ => 0.6 };
        var tmp = Path.Combine(Path.GetTempPath(), $"cthsynth-{Environment.ProcessId}-{id}.ogg");
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "-v", "error", "-y", "-f", "lavfi", "-i", $"aevalsrc={expr}:s=44100:d={dur.ToString(System.Globalization.CultureInfo.InvariantCulture)}", "-ac", "1", "-c:a", "libvorbis", "-q:a", "3", tmp }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg failed for {id}: {err}");
        var bytes = File.ReadAllBytes(tmp);
        File.Delete(tmp);
        return bytes;
    }
}
