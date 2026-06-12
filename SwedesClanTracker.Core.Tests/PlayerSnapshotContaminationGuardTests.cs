namespace SwedesClanTracker.Core.Tests;

public class PlayerSnapshotContaminationGuardTests
{
    [Fact]
    public void Evaluate_allows_first_snapshot()
    {
        var candidate = Snapshot(totalLevel: 1500, ehb: 10, ehp: 20, petCount: 0);

        var result = PlayerSnapshotContaminationGuard.Evaluate(null, candidate);

        Assert.False(result.IsContaminated);
    }

    [Fact]
    public void Evaluate_blocks_total_level_decrease()
    {
        var latest = Snapshot(totalLevel: 2376, ehb: 629, ehp: 1216, petCount: 16);
        var candidate = Snapshot(totalLevel: 2375, ehb: 630, ehp: 1217, petCount: 16);

        var result = PlayerSnapshotContaminationGuard.Evaluate(latest, candidate);

        Assert.True(result.IsContaminated);
        Assert.Contains("total-level-decreased", result.Reasons);
        Assert.Equal(1, result.TotalLevelDrop);
    }

    [Fact]
    public void Evaluate_blocks_large_ehb_drop()
    {
        var latest = Snapshot(totalLevel: 2376, ehb: 629, ehp: 1216, petCount: 16);
        var candidate = Snapshot(totalLevel: 2376, ehb: 428.99, ehp: 1216, petCount: 16);

        var result = PlayerSnapshotContaminationGuard.Evaluate(latest, candidate);

        Assert.True(result.IsContaminated);
        Assert.Contains("ehb-drop-too-large", result.Reasons);
    }

    [Fact]
    public void Evaluate_allows_ehb_and_ehp_drop_at_threshold()
    {
        var latest = Snapshot(totalLevel: 2376, ehb: 629, ehp: 1216, petCount: 16);
        var candidate = Snapshot(totalLevel: 2376, ehb: 429, ehp: 1016, petCount: 16);

        var result = PlayerSnapshotContaminationGuard.Evaluate(latest, candidate);

        Assert.False(result.IsContaminated);
    }

    [Fact]
    public void Evaluate_ignores_pet_count_decrease()
    {
        var latest = Snapshot(totalLevel: 2376, ehb: 629, ehp: 1216, petCount: 16);
        var candidate = Snapshot(totalLevel: 2376, ehb: 629, ehp: 1216, petCount: 0);

        var result = PlayerSnapshotContaminationGuard.Evaluate(latest, candidate);

        Assert.False(result.IsContaminated);
    }

    private static PlayerSnapshot Snapshot(int totalLevel, double ehb, double ehp, int petCount)
    {
        return new PlayerSnapshot
        {
            TotalLevel = totalLevel,
            Ehb = ehb,
            Ehp = ehp,
            PetCount = petCount
        };
    }
}
