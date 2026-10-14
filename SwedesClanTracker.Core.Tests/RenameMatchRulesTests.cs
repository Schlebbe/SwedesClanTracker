namespace SwedesClanTracker.Core.Tests;

public class RenameMatchRulesTests
{
    [Fact]
    public void Evaluate_matches_strict_snapshot_similarity()
    {
        var previous = Snapshot(totalLevel: 2185, ehb: 664.262, ehp: 1060.4995, collections: 490, pets: 6);
        var next = Snapshot(totalLevel: 2185, ehb: 663.9, ehp: 1060.6, collections: 491, pets: 6);

        var result = RenameMatchRules.Evaluate(previous, next);

        Assert.True(result.IsMatch);
        Assert.Null(result.EhbIgnoredReason);
    }

    [Fact]
    public void Evaluate_matches_Iron_A3x_and_Gynofobi_snapshots_with_small_Ehb_variance()
    {
        var previous = Snapshot(totalLevel: 2088, ehb: 299.0733, ehp: 627.7743, collections: 325, pets: 4);
        var next = Snapshot(totalLevel: 2088, ehb: 287.8573, ehp: 627.7743, collections: 325, pets: 4);

        var result = RenameMatchRules.Evaluate(previous, next);

        Assert.True(result.IsMatch);
        Assert.Equal("strong-identity-match-with-tolerated-ehb-variance", result.EhbIgnoredReason);
        Assert.Equal(11.216, result.EhbDelta, 6);
        Assert.Equal(0, result.TotalLevelDelta);
        Assert.Equal(0, result.EhpDelta, 6);
        Assert.Equal(0, result.CollectionsDelta);
        Assert.Equal(0, result.PetCountDelta);
    }

    [Fact]
    public void Evaluate_rejects_broader_Ehb_match_when_collections_differ_materially()
    {
        var previous = Snapshot(totalLevel: 2088, ehb: 299.0733, ehp: 627.7743, collections: 325, pets: 4);
        var next = Snapshot(totalLevel: 2088, ehb: 287.8573, ehp: 627.7743, collections: 345, pets: 4);

        var result = RenameMatchRules.Evaluate(previous, next);

        Assert.False(result.IsMatch);
        Assert.Equal(20, result.CollectionsDelta);
    }

    [Fact]
    public void Evaluate_matches_when_one_ehb_is_zero_but_other_stats_are_strong()
    {
        var previous = Snapshot(totalLevel: 2185, ehb: 664.262, ehp: 1060.4995, collections: 490, pets: 6);
        var next = Snapshot(totalLevel: 2185, ehb: 0, ehp: 1060.4995, collections: 490, pets: 6);

        var result = RenameMatchRules.Evaluate(previous, next);

        Assert.True(result.IsMatch);
        Assert.Equal("zero-ehb-with-strong-total-ehp-collections-pet-match", result.EhbIgnoredReason);
        Assert.Equal(664.262, result.EhbDelta, 6);
    }

    [Fact]
    public void Evaluate_rejects_zero_ehb_match_when_collections_differ()
    {
        var previous = Snapshot(totalLevel: 2185, ehb: 664.262, ehp: 1060.4995, collections: 490, pets: 6);
        var next = Snapshot(totalLevel: 2185, ehb: 0, ehp: 1060.4995, collections: 505, pets: 6);

        var result = RenameMatchRules.Evaluate(previous, next);

        Assert.False(result.IsMatch);
    }

    [Fact]
    public void Evaluate_rejects_zero_ehb_match_when_ehp_differs()
    {
        var previous = Snapshot(totalLevel: 2185, ehb: 664.262, ehp: 1060.4995, collections: 490, pets: 6);
        var next = Snapshot(totalLevel: 2185, ehb: 0, ehp: 1080.6, collections: 490, pets: 6);

        var result = RenameMatchRules.Evaluate(previous, next);

        Assert.False(result.IsMatch);
    }

    private static PlayerSnapshot Snapshot(int totalLevel, double ehb, double ehp, int collections, int pets)
    {
        return new PlayerSnapshot
        {
            TotalLevel = totalLevel,
            Ehb = ehb,
            Ehp = ehp,
            Collections = collections,
            PetCount = pets
        };
    }
}
