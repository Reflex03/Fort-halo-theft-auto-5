using Xunit;

namespace CthExtract.Halo.Tests;

public class SoundIndexTests
{
    private static FsbInfo Info(params (uint, string)[] recs) =>
        new("test.fsb.info", FsbInfo.Write(recs.Select(r => (r.Item1, 1000u, 1, 0u, r.Item2))));

    [Fact]
    public void RecordsRoundTripAt0x118Bytes()
    {
        var bytes = FsbInfo.Write(new[] { (0xDEADBEEFu, 4410u, 2, 1234u, @"data\sound\weapons\assault_rifle\ar_fire\19103_b.aif") });
        Assert.Equal(0x118, bytes.Length);
        var info = new FsbInfo("x", bytes);
        var r = Assert.Single(info.Records);
        Assert.Equal(0xDEADBEEFu, r.Hash);
        Assert.Equal(4410u, r.SampleCount);
        Assert.Equal(2, r.ChannelCount);
        Assert.Equal(1234u, r.SampleSize);
        Assert.Equal(@"data\sound\weapons\assault_rifle\ar_fire\19103_b.aif", r.FileName);
        Assert.Equal(0, info.TrailingBytes);
    }

    [Fact]
    public void FirstRecordWinsForDuplicateHashesAndIndexIsSubsoundIndex()
    {
        var info = Info((1, "a"), (2, "b"), (1, "c"), (3, "d"));
        Assert.Equal(0, info.IndexOfHash(1));
        Assert.Equal(1, info.IndexOfHash(2));
        Assert.Equal(3, info.IndexOfHash(3));
        Assert.Equal(-1, info.IndexOfHash(4));
        Assert.Equal(1, info.DuplicateHashes);
    }

    [Fact]
    public void TruncatedTailIsReported()
    {
        var bytes = FsbInfo.Write(new[] { (1u, 1u, 1, 0u, "a") }).Concat(new byte[17]).ToArray();
        var info = new FsbInfo("x", bytes);
        Assert.Single(info.Records);
        Assert.Equal(17, info.TrailingBytes);
    }

    [Fact]
    public void SheetPatternsMatchLikeGlobsWithAlternation()
    {
        var info = Info((1, @"data\sound\weapons\assault_rifle\ar_fire\19103_b.aif"),
                        (2, @"data\sound\weapons\battle_rifle\br_fire\1.aif"),
                        (3, @"data\sound\weapons\frag_grenade\throw\1.aif"),
                        (4, @"data\sound\characters\spartan\shield_recharge\a.aif"),
                        (5, @"data\sound\characters\spartan\shield_depleted\a.aif"),
                        (6, @"data\sound\characters\spartan\shield_down\a.aif"),
                        (7, @"data\sound\weapons\ASSAULT_RIFLE\AR_FIRE_LOD\x.aif"));
        Assert.Equal(new uint[] { 1, 7 }, info.Match(@"data\sound\weapons\assault_rifle\*fire*").Select(r => r.Hash));
        Assert.Equal(new uint[] { 3 }, info.Match(@"data\sound\weapons\*grenade*\*throw*").Select(r => r.Hash));
        Assert.Equal(new uint[] { 4 }, info.Match("*shield*charg*").Select(r => r.Hash));
        Assert.Equal(new uint[] { 5, 6 }, info.Match("*shield*(deplet|down|empty)*").Select(r => r.Hash));
    }

    [Fact]
    public void LoosePatternFindsFilesWithoutTheDataPrefix()
    {
        var info = Info((1, @"sound\weapons\magnum\magnum_fire\1.aif"));
        Assert.Single(info.Match(@"data\sound\weapons\magnum\*fire*"));
    }

    [Fact]
    public void PickerPrefersPlainFireOverLodEmptyAndTails()
    {
        var recs = new[]
        {
            new FsbInfoRecord(0, 1, 100, 1, 0, 0, 0, @"data\sound\weapons\assault_rifle\ar_fire_lod\a.aif"),
            new FsbInfoRecord(1, 2, 100, 1, 0, 0, 0, @"data\sound\weapons\assault_rifle\dry_fire\a.aif"),
            new FsbInfoRecord(2, 3, 100, 2, 0, 0, 0, @"data\sound\weapons\assault_rifle\ar_fire\19103_b.aif"),
            new FsbInfoRecord(3, 4, 100, 1, 0, 0, 0, @"data\sound\weapons\assault_rifle\ar_fire_tail\a.aif"),
        };
        Assert.True(SoundPicker.Score(recs[2], SoundPurpose.Fire) > recs.Where(r => r != recs[2]).Max(r => SoundPicker.Score(r, SoundPurpose.Fire)));

        var shield = new[]
        {
            new FsbInfoRecord(0, 1, 100, 1, 0, 0, 0, @"data\sound\characters\spartan\shield_depleted\a.aif"),
            new FsbInfoRecord(1, 2, 100, 1, 0, 0, 0, @"data\sound\characters\spartan\shield_recharge\a.aif"),
        };
        Assert.True(SoundPicker.Score(shield[1], SoundPurpose.ShieldRecharge) > SoundPicker.Score(shield[0], SoundPurpose.ShieldRecharge));
        Assert.True(SoundPicker.Score(shield[0], SoundPurpose.ShieldDepleted) > SoundPicker.Score(shield[1], SoundPurpose.ShieldDepleted));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(16420, 3)]
    [InlineData(524287, 63)]
    [InlineData(-5, 2)]   // sign-extended 20-bit field
    public void PitchRangePackingRoundTrips(int first, int count)
    {
        var v = TagWalker.EncodePitchRange(first, count);
        Assert.Equal((first, count), TagWalker.DecodePitchRange(v));
    }

    [Fact]
    public void PitchRangeDecodeMatchesTagToolFormula()
    {
        // TagTool SoundCacheFileGestalt (MCC): first = (v << 12) >> 12, count = (v >> 20) & 63
        int v = unchecked((int)0x0230_1234);
        Assert.Equal((0x01234, 0x23), TagWalker.DecodePitchRange(v));
    }

    [Theory]
    [InlineData(@"sfx.fsb.info filename matching data\sound\weapons\*grenade*\*throw*", @"data\sound\weapons\*grenade*\*throw*")]
    [InlineData(@"sfx.fsb.info filename matching *shield*(deplet|down|empty)*", @"*shield*(deplet|down|empty)*")]
    [InlineData("matg Player Representation -> biped", null)]
    public void PatternIsParsedFromSheetText(string text, string? expected) => Assert.Equal(expected, HaloPlan.Pattern(text));
}
