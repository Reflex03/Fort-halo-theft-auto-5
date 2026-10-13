using System.Reflection;
using System.Runtime.CompilerServices;
using CthExtract.Fortnite.Unreal;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.UObject;
using Xunit;

namespace CthExtract.Fortnite.Tests;

/// <summary>The generic walker on hand-built property trees shaped like Fortnite item definitions.</summary>
public class PropertyWalkerTests
{
    private static FPropertyTag Tag(string name, FPropertyTagType value) =>
        new(new FName(name), new FName(value.GetType().Name), 0, 0, null, false, null, value);

    private static SoftObjectProperty Soft(string path) => new(new FSoftObjectPath(new FName(path), "", null));

    private static StructProperty Struct(params FPropertyTag[] props) => new(new FScriptStruct(new FStructFallback(props.ToList())));

    /// <summary>An FInstancedStruct can only be read from an archive; build one around a struct for the test.</summary>
    private static StructProperty Instanced(params FPropertyTag[] props)
    {
        var inst = (FInstancedStruct)RuntimeHelpers.GetUninitializedObject(typeof(FInstancedStruct));
        typeof(FInstancedStruct).GetField("ScriptStruct", BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(inst, new FScriptStruct(new FStructFallback(props.ToList())));
        return new StructProperty(new FScriptStruct(inst));
    }

    [Fact]
    public void Finds_icons_and_sounds_inside_datalist_structs_arrays_and_maps()
    {
        var dataList = new ArrayProperty(new UScriptArray(
        [
            Instanced(Tag("LargeIcon", Soft("/Game/UI/T_Icon_L.T_Icon_L"))),
            Instanced(Tag("Icon", Soft("/Game/UI/T_Icon_S.T_Icon_S"))),
            Instanced(Tag("PrimaryFireSoundMap", new MapProperty(new UScriptMap(new Dictionary<FPropertyTagType, FPropertyTagType?>
            {
                [new NameProperty(new FName("EFortWeaponSoundState::Default"))] = Soft("/Game/Sounds/Drink.Drink"),
            })))),
            Instanced(Tag("Pickup", Struct(Tag("PickupSound", Soft("/Game/Sounds/Pickup.Pickup")), Tag("Nothing", Soft("None"))))),
        ], "StructProperty"));
        var props = new List<FPropertyTag>
        {
            Tag("ItemName", new StrProperty("Shield Potion")),
            Tag("DataList", dataList),
            Tag("Maybe", new OptionalProperty(Soft("/Game/X/Opt.Opt"))),
        };

        var refs = PropertyWalker.Refs(props, "WID_Test").ToList();
        Assert.Contains(refs, r => r.PropertyName == "LargeIcon" && r.TargetPath.Contains("T_Icon_L") && r.Path == "WID_Test.DataList[0].LargeIcon");
        Assert.Contains(refs, r => r.PropertyName == "Icon" && r.TargetPath.Contains("T_Icon_S"));
        var drink = Assert.Single(refs, r => r.TargetPath.Contains("Drink"));
        Assert.Equal("WID_Test.DataList[2].PrimaryFireSoundMap[EFortWeaponSoundState::Default]", drink.Path);
        Assert.True(SoundGraph.LooksLikeSound(drink));
        Assert.Contains(refs, r => r.Path == "WID_Test.DataList[3].Pickup.PickupSound");
        Assert.Contains(refs, r => r.TargetPath.Contains("Opt"));
        Assert.DoesNotContain(refs, r => r.TargetPath == "None");
        Assert.False(SoundGraph.LooksLikeSound(refs.First(r => r.PropertyName == "LargeIcon")));
    }

    [Fact]
    public void Depth_is_bounded()
    {
        FPropertyTagType nested = Soft("/Game/Deep.Deep");
        for (int i = 0; i < 30; i++) nested = Struct(Tag("Inner", nested));
        Assert.Empty(PropertyWalker.Refs([Tag("Root", nested)], "o", maxDepth: 8));
        Assert.Single(PropertyWalker.Refs([Tag("Root", nested)], "o", maxDepth: 40));
    }
}
