using System.Numerics;
using CthExtract.Core;
using CthExtract.Fortnite.Conversion;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;
using Xunit;

namespace CthExtract.Fortnite.Tests;

/// <summary>The lookup hints are read from the sources sheet; these keep code and sheet in step.</summary>
public class RecipeBookTests
{
    [Fact]
    public void Item_names_come_from_the_sheet_locators()
    {
        Assert.Equal(["WID_Paprika_Bandage", "Athena_Bandage", "Figment_Athena_Bandage", "Clyde_Athena_Bandage"], RecipeBook.ItemNames(Rows.Get("fn_icon_bandages")));
        Assert.Equal("WID_Paprika_ShieldPot", RecipeBook.ItemNames(Rows.Get("fn_icon_shield_potion"))[0]);
        Assert.Contains("Figment_Athena_SuperMedkit", RecipeBook.ItemNames(Rows.Get("fn_icon_chug_jug")));
        Assert.Equal(["Glider_Default", "Umbrella_Default"], RecipeBook.ItemNames(Rows.Get("fn_mesh_glider")));
    }

    [Fact]
    public void Display_names_and_keywords_come_from_the_row()
    {
        Assert.Equal("Med Kit", RecipeBook.DisplayName(Rows.Get("fn_icon_med_kit")));
        Assert.Equal("Small Shield Potion", RecipeBook.DisplayName(Rows.Get("fn_icon_small_shield_potion")));
        Assert.Equal("chugjug", RecipeBook.Squash("Chug-Jug"));
        Assert.Contains("supermedkit", RecipeBook.Keywords(Rows.Get("fn_icon_chug_jug")));
    }

    [Fact]
    public void Mesh_and_sound_patterns_come_from_the_sheet()
    {
        Assert.Equal("BBID_DefaultBus", RecipeBook.PreferredName(Rows.Get("fn_mesh_battle_bus"), "BBID_"));
        var ex = RecipeBook.Exclusion(Rows.Get("fn_mesh_battle_bus"), "x")!;
        Assert.Matches(ex, "BBID_ArmoredBus");
        Assert.DoesNotMatch(ex, "BBID_DefaultBus");
        Assert.Equal("(?i)tiered_chest", RecipeBook.InlineRegex(Rows.Get("fn_mesh_chest").Locator)!.ToString());
        Assert.Equal("(?i)chest", RecipeBook.InlineRegex(Rows.Get("fn_mesh_chest").Fallback)!.ToString());
        Assert.Matches(RecipeBook.Exclusion(Rows.Get("fn_mesh_chest"), "x")!, "Tiered_Ammo_Box");
        Assert.Matches(RecipeBook.InlineRegex(Rows.Get("fn_snd_chest_open").Fallback)!, "Chest_Open_01");
        Assert.Matches(RecipeBook.InlineRegex(Rows.Get("fn_snd_drink").Fallback)!, "ShieldPotion_Drink");
        Assert.Matches(RecipeBook.InlineRegex(Rows.Get("fn_snd_heal_use").Fallback)!, "Bandage_Use_01");
        Assert.DoesNotMatch(RecipeBook.InlineRegex(Rows.Get("fn_snd_heal_use").Fallback)!, "Shield_Drink");
    }

    [Fact]
    public void Every_fortnite_row_has_a_recipe()
    {
        var known = new HashSet<string> { "fn_mesh_battle_bus", "fn_mesh_glider", "fn_mesh_chest", "fn_snd_chest_open", "fn_snd_drink", "fn_snd_heal_use" };
        foreach (var r in CthExtract.Core.Generated.Sources.ForGame("fortnite"))
            Assert.True(r.Kind == "icon" || known.Contains(r.Id), $"{r.Id} has no recipe");
    }
}

public class PlacementTests
{
    private static MeshData Box(Vector3 min, Vector3 max)
    {
        var s = new MeshData.Submesh { Material = "m" };
        foreach (var p in new[] { min, max, new Vector3(min.X, max.Y, min.Z) })
            s.Vertices.AddRange([p.X, p.Y, p.Z, 0, 1, 0, 0, 0]);
        s.Indices.AddRange([0, 1, 2]);
        var m = new MeshData();
        m.Submeshes.Add(s);
        return m;
    }

    [Fact]
    public void Vehicles_are_centred_and_floored()
    {
        var m = Box(new Vector3(1, -2, 3), new Vector3(3, 1, 13));
        Placement.PlaceVehicle(m, longAxisAlongZ: true);
        var b = Placement.Measure(m);
        Assert.Equal(0, b.Min.Y, 4);
        Assert.Equal(0, b.Centre.X, 4);
        Assert.Equal(0, b.Centre.Z, 4);
        Assert.Equal(10, b.Size.Z, 4);
    }

    [Fact]
    public void A_bus_lying_along_x_is_turned_so_its_length_runs_along_z()
    {
        var m = Box(new Vector3(-6, 0, -1), new Vector3(6, 3, 1));
        var note = Placement.PlaceVehicle(m, longAxisAlongZ: true);
        var b = Placement.Measure(m);
        Assert.Equal(12, b.Size.Z, 4);
        Assert.Equal(2, b.Size.X, 4);
        Assert.Contains("turned", note);
    }

    [Fact]
    public void Glider_keeps_its_origin_and_gets_its_span_on_x()
    {
        var m = Box(new Vector3(-0.5f, 1, -2), new Vector3(0.5f, 2, 2));
        Placement.PlaceGlider(m);
        var b = Placement.Measure(m);
        Assert.Equal(4, b.Size.X, 4);
        Assert.Equal(1, b.Min.Y, 4); // not moved
    }

    [Fact]
    public void Quarter_turns_are_proper_rotations()
    {
        var m = Box(new Vector3(0, 0, 0), new Vector3(1, 1, 2));
        var before = Placement.Measure(m);
        for (int q = 1; q <= 4; q++) Placement.RotateY90(m, 1);
        var after = Placement.Measure(m);
        Assert.Equal(before.Min, after.Min);
        Assert.Equal(before.Max, after.Max);
    }
}

public class TextureConvertTests
{
    [Fact]
    public void Converts_each_decoder_output_format_to_rgba()
    {
        var bgra = new CTexture(1, 1, EPixelFormat.PF_B8G8R8A8, [10, 20, 30, 40]);
        Assert.Equal([30, 20, 10, 40], TextureConvert.ToRgba(bgra).Pixels);
        var rgba = new CTexture(1, 1, EPixelFormat.PF_R8G8B8A8, [1, 2, 3, 4]);
        Assert.Equal([1, 2, 3, 4], TextureConvert.ToRgba(rgba).Pixels);
        var g8 = new CTexture(1, 1, EPixelFormat.PF_G8, [77]);
        Assert.Equal([77, 77, 77, 255], TextureConvert.ToRgba(g8).Pixels);
        var g16 = new CTexture(1, 1, EPixelFormat.PF_G16, [0x34, 0x12]);
        Assert.Equal([0x12, 0x12, 0x12, 255], TextureConvert.ToRgba(g16).Pixels);
        var half = new byte[8];
        BitConverter.TryWriteBytes(half.AsSpan(0), (Half)1.0f);
        BitConverter.TryWriteBytes(half.AsSpan(2), (Half)0.0f);
        BitConverter.TryWriteBytes(half.AsSpan(4), (Half)0.5f);
        BitConverter.TryWriteBytes(half.AsSpan(6), (Half)1.0f);
        Assert.Equal([255, 0, 188, 255], TextureConvert.ToRgba(new CTexture(1, 1, EPixelFormat.PF_FloatRGBA, half)).Pixels);
        Assert.Throws<NotSupportedException>(() => TextureConvert.ToRgba(new CTexture(1, 1, EPixelFormat.PF_BC5, new byte[16])));
        Assert.Throws<InvalidDataException>(() => TextureConvert.ToRgba(new CTexture(2, 2, EPixelFormat.PF_R8G8B8A8, new byte[4])));
    }

    [Fact]
    public void Flat_or_empty_images_are_not_icons()
    {
        Assert.False(TextureConvert.LooksLikeImage(TextureConvert.Flat(1, 2, 3), out _));
        var px = new byte[16 * 16 * 4];
        Assert.False(TextureConvert.LooksLikeImage(new Rgba(16, 16, px), out var why));
        Assert.Contains("transparent", why);
        for (int i = 0; i < px.Length; i += 4) { px[i] = (byte)i; px[i + 3] = 255; }
        Assert.True(TextureConvert.LooksLikeImage(new Rgba(16, 16, px), out _));
    }
}
