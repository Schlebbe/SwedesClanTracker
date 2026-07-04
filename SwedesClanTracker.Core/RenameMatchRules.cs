namespace SwedesClanTracker.Core;

public sealed record RenameMatchEvaluation(
    bool IsMatch,
    int TotalLevelDelta,
    double EhbDelta,
    double EhpDelta,
    int CollectionsDelta,
    int PetCountDelta,
    double Score,
    string? EhbIgnoredReason);

public static class RenameMatchRules
{
    public const int MaxTotalLevelDelta = 5;
    public const double MaxEhbDelta = 10;
    public const double MaxEhpDelta = 10;
    public const int MaxCollectionsDeltaWhenIgnoringZeroEhb = 2;
    public const int MaxPetCountDeltaWhenIgnoringZeroEhb = 1;

    public static RenameMatchEvaluation Evaluate(PlayerSnapshot previousSnapshot, PlayerSnapshot newSnapshot)
    {
        var totalLevelDelta = Math.Abs(previousSnapshot.TotalLevel - newSnapshot.TotalLevel);
        var ehbDelta = Math.Abs(previousSnapshot.Ehb - newSnapshot.Ehb);
        var ehpDelta = Math.Abs(previousSnapshot.Ehp - newSnapshot.Ehp);
        var collectionsDelta = Math.Abs(previousSnapshot.Collections - newSnapshot.Collections);
        var petCountDelta = Math.Abs(previousSnapshot.PetCount - newSnapshot.PetCount);

        var strictMatch =
            totalLevelDelta <= MaxTotalLevelDelta &&
            ehbDelta <= MaxEhbDelta &&
            ehpDelta <= MaxEhpDelta;
        if (strictMatch)
        {
            return new RenameMatchEvaluation(
                true,
                totalLevelDelta,
                ehbDelta,
                ehpDelta,
                collectionsDelta,
                petCountDelta,
                totalLevelDelta + ehbDelta + ehpDelta,
                null);
        }

        var zeroEhbLooksTransient =
            HasZeroAndNonZeroEhb(previousSnapshot.Ehb, newSnapshot.Ehb) &&
            totalLevelDelta <= MaxTotalLevelDelta &&
            ehpDelta <= MaxEhpDelta &&
            collectionsDelta <= MaxCollectionsDeltaWhenIgnoringZeroEhb &&
            petCountDelta <= MaxPetCountDeltaWhenIgnoringZeroEhb;
        if (zeroEhbLooksTransient)
        {
            return new RenameMatchEvaluation(
                true,
                totalLevelDelta,
                ehbDelta,
                ehpDelta,
                collectionsDelta,
                petCountDelta,
                totalLevelDelta + ehpDelta + collectionsDelta + petCountDelta + 1000,
                "zero-ehb-with-strong-total-ehp-collections-pet-match");
        }

        return new RenameMatchEvaluation(
            false,
            totalLevelDelta,
            ehbDelta,
            ehpDelta,
            collectionsDelta,
            petCountDelta,
            double.MaxValue,
            null);
    }

    private static bool HasZeroAndNonZeroEhb(double left, double right)
    {
        return (IsZero(left) && right > MaxEhbDelta) ||
            (IsZero(right) && left > MaxEhbDelta);
    }

    private static bool IsZero(double value) => Math.Abs(value) < 0.000001;
}
