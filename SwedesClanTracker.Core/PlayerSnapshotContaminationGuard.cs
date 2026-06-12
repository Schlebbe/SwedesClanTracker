namespace SwedesClanTracker.Core;

public sealed record PlayerSnapshotContaminationResult(
    bool IsContaminated,
    IReadOnlyList<string> Reasons,
    int TotalLevelDrop,
    double EhbDrop,
    double EhpDrop)
{
    public static readonly PlayerSnapshotContaminationResult Clean = new(false, [], 0, 0, 0);
}

public static class PlayerSnapshotContaminationGuard
{
    public const string EventType = "PLAYER_SNAPSHOT_CONTAMINATION_REQUIRED";
    public const double MaxAllowedEhbDrop = 200;
    public const double MaxAllowedEhpDrop = 200;

    public static PlayerSnapshotContaminationResult Evaluate(PlayerSnapshot? latestSnapshot, PlayerSnapshot candidateSnapshot)
    {
        if (latestSnapshot is null)
        {
            return PlayerSnapshotContaminationResult.Clean;
        }

        var reasons = new List<string>();
        var totalLevelDrop = latestSnapshot.TotalLevel - candidateSnapshot.TotalLevel;
        var ehbDrop = latestSnapshot.Ehb - candidateSnapshot.Ehb;
        var ehpDrop = latestSnapshot.Ehp - candidateSnapshot.Ehp;

        if (totalLevelDrop > 0)
        {
            reasons.Add("total-level-decreased");
        }

        if (ehbDrop > MaxAllowedEhbDrop)
        {
            reasons.Add("ehb-drop-too-large");
        }

        if (ehpDrop > MaxAllowedEhpDrop)
        {
            reasons.Add("ehp-drop-too-large");
        }

        return reasons.Count == 0
            ? PlayerSnapshotContaminationResult.Clean
            : new PlayerSnapshotContaminationResult(true, reasons, totalLevelDrop, ehbDrop, ehpDrop);
    }
}
