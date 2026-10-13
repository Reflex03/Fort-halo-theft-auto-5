using Xunit;

namespace CthExtract.Cli.Tests;

/// <summary>Material names read back from a previous run's .cthm (keep policy) must stay inside the pack.</summary>
public class MaterialPathTests
{
    [Theory]
    [InlineData("halo/assault_rifle/0")]
    [InlineData("gta/infernus/vehicle_generic_tyrewallblack")]
    [InlineData("gta/infernus/..")] // a texture literally named ".." ends up as "...png" inside the folder
    [InlineData("fortnite/battle_bus/3")]
    [InlineData("placeholder/generic")]
    public void Normal_material_names_are_accepted(string m)
    {
        Assert.True(CthExtract.Finisher.SafeMaterial(m));
        var p = CthExtract.Finisher.MaterialPath("/cache/packs/x/assets/crafttheft", m);
        Assert.StartsWith(Path.Combine("/cache/packs/x/assets/crafttheft", "textures"), Path.GetFullPath(p));
    }

    [Theory]
    [InlineData("../../../../evil")]
    [InlineData("halo/../../evil")]
    [InlineData("/etc/evil")]
    [InlineData("halo\\..\\evil")]
    [InlineData("C:/evil")]
    [InlineData("halo//x")]
    [InlineData("./x")]
    [InlineData("")]
    [InlineData("halo/")]
    public void Traversal_and_rooted_names_are_refused(string m)
    {
        Assert.False(CthExtract.Finisher.SafeMaterial(m));
        Assert.Equal("", CthExtract.Finisher.MaterialPath("/cache/packs/x/assets/crafttheft", m));
    }
}
