using System.Text.Json;

namespace SwedesClanTracker.Core;

public enum PlayerStatus { ACTIVE, NEW_PENDING_REVIEW, MISSING_PENDING_REVIEW, MERGE_SUGGESTED, REMOVED_CONFIRMED }
public enum PromotionStatus { PENDING, APPROVED, DISMISSED }
public enum PromotionCandidateType { wom_already_at_new_rank, needs_wom_rank_update, unknown_wom_role }

public class Player
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string CurrentRank { get; set; } = "Recruit";
    public string EligibleRank { get; set; } = "Recruit";
    public PlayerStatus Status { get; set; } = PlayerStatus.NEW_PENDING_REVIEW;
    public DateTimeOffset LastSeen { get; set; }
    public DateTimeOffset? LastSynced { get; set; }
    public int StoredPetCount { get; set; }
    public int? ManualPetOverride { get; set; }
    public ICollection<PlayerSnapshot> Snapshots { get; set; } = new List<PlayerSnapshot>();
}

public class PlayerSnapshot
{
    public int Id { get; set; }
    public int PlayerId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public int TotalLevel { get; set; }
    public double Ehb { get; set; }
    public double Ehp { get; set; }
    public int Collections { get; set; }
    public int PetCount { get; set; }
    public Player Player { get; set; } = null!;

    public bool HasSameStats(PlayerSnapshot other)
    {
        return TotalLevel == other.TotalLevel &&
            Ehb == other.Ehb &&
            Ehp == other.Ehp &&
            Collections == other.Collections &&
            PetCount == other.PetCount;
    }
}

public class PromotionCandidate
{
    public int Id { get; set; }
    public int PlayerId { get; set; }
    public string OldRank { get; set; } = "";
    public string NewRank { get; set; } = "";
    public string Reason { get; set; } = "";
    public PromotionStatus Status { get; set; } = PromotionStatus.PENDING;
    public DateTimeOffset CreatedAt { get; set; }
    public Player Player { get; set; } = null!;
}

public class LifecycleEvent
{
    public int Id { get; set; }
    public int PlayerId { get; set; }
    public string EventType { get; set; } = "";
    public string MetadataJson { get; set; } = "{}";
    public string Status { get; set; } = "OPEN";
    public DateTimeOffset CreatedAt { get; set; }
}

public class RankResult
{
    public string Rank { get; init; } = "Recruit";
    public string Explanation { get; init; } = "No rank requirement met.";
}

public static class RankEvaluator
{
    private static readonly List<(string Rank, Func<PlayerSnapshot, bool> Rule, string Why)> Rules =
    [
        ("Officer", s => s.TotalLevel >= 2100, "Total level >= 2100"),
        ("Commander", s => s.TotalLevel >= 2300, "Total level >= 2300"),
        ("Lieutenant", s => s.Ehb >= 750 || s.Ehp >= 1000, "EHB >= 750 OR EHP >= 1000"),
        ("Captain", s => s.Ehb >= 1000 || s.PetCount >= 10 || s.Ehp >= 1500, "EHB >= 1000 OR pets >= 10 OR EHP >= 1500"),
        ("Astral", s => s.Ehb >= 1250 || s.PetCount >= 15 || s.Ehp >= 1750 || s.Collections >= 800, "EHB >= 1250 OR pets >= 15 OR EHP >= 1750 OR collections >= 800"),
        ("General", s => s.Ehb >= 1500 || s.PetCount >= 20 || s.Ehp >= 2000 || s.Collections >= 950, "EHB >= 1500 OR pets >= 20 OR EHP >= 2000 OR collections >= 950"),
        ("Brigadier", s => s.Ehb >= 2000 || s.PetCount >= 30 || s.Ehp >= 2500 || s.Collections >= 1050, "EHB >= 2000 OR pets >= 30 OR EHP >= 2500 OR collections >= 1050"),
        ("Admiral", s => s.Ehb >= 3000 || s.PetCount >= 40 || s.Ehp >= 4000 || s.Collections >= 1300, "EHB >= 3000 OR pets >= 40 OR EHP >= 4000 OR collections >= 1300"),
        ("Marshal", s => s.Ehb >= 4000 || s.PetCount >= 50 || s.Ehp >= 5000 || s.Collections >= 1450, "EHB >= 4000 OR pets >= 50 OR EHP >= 5000 OR collections >= 1450"),
        ("Beast", s => s.Ehb >= 5000 || s.PetCount >= 60 || s.Ehp >= 7000 || s.Collections >= 1550, "EHB >= 5000 OR pets >= 60 OR EHP >= 7000 OR collections >= 1550")
    ];

    public static RankResult Evaluate(PlayerSnapshot snapshot)
    {
        string best = "Recruit";
        string why = "No rank requirement met.";
        foreach (var rule in Rules)
        {
            if (!rule.Rule(snapshot)) continue;
            best = rule.Rank;
            why = rule.Why;
        }
        return new RankResult { Rank = best, Explanation = $"{best} via: {why}" };
    }
}

public static class RankRules
{
    private static readonly string[] OrderedRanks =
    [
        "Recruit", "Officer", "Commander", "Lieutenant", "Captain", "Astral", "General", "Brigadier", "Admiral", "Marshal", "Beast"
    ];

    private static readonly string[] SpecialWomRoles =
    [
        "imp", "kitten", "administrator", "deputy owner", "owner", "short green guy", "recruit", "apothecary", "wily"
    ];

    private static readonly HashSet<string> NormalizedSpecialWomRoles =
        SpecialWomRoles.Select(NormalizeRankName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string NormalizeRankName(string rank) => (rank ?? "").Replace('_', ' ').Trim();

    public static int RankOrder(string rank)
    {
        var normalized = NormalizeRankName(rank);
        for (var i = 0; i < OrderedRanks.Length; i++)
        {
            if (string.Equals(OrderedRanks[i], normalized, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return 0;
    }

    public static bool IsKnownClanRank(string rank)
    {
        var normalized = NormalizeRankName(rank);
        return OrderedRanks.Any(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSpecialWomRole(string role)
    {
        var normalized = NormalizeRankName(role);
        return NormalizedSpecialWomRoles.Contains(normalized);
    }

    public static bool IsExactKnownClanRankMatch(string left, string right)
    {
        if (!IsKnownClanRank(left) || !IsKnownClanRank(right)) return false;
        return string.Equals(NormalizeRankName(left), NormalizeRankName(right), StringComparison.OrdinalIgnoreCase);
    }

    public static PromotionCandidateType ClassifyPromotionCandidate(string newRank, string? womRole)
    {
        if (string.IsNullOrWhiteSpace(womRole)) return PromotionCandidateType.unknown_wom_role;
        if (string.Equals(NormalizeRankName(womRole), "member", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(NormalizeRankName(newRank), "Recruit", StringComparison.OrdinalIgnoreCase)
                ? PromotionCandidateType.wom_already_at_new_rank
                : PromotionCandidateType.needs_wom_rank_update;
        }
        if (IsSpecialWomRole(womRole)) return PromotionCandidateType.unknown_wom_role;
        if (!IsKnownClanRank(womRole)) return PromotionCandidateType.unknown_wom_role;

        return string.Equals(NormalizeRankName(newRank), NormalizeRankName(womRole), StringComparison.OrdinalIgnoreCase)
            ? PromotionCandidateType.wom_already_at_new_rank
            : PromotionCandidateType.needs_wom_rank_update;
    }
}

public static class UsernameRules
{
    public static string NormalizeUsername(string input) => (input ?? "").Replace('_', ' ').Trim();
}

public static class JsonUtil
{
    public static string Serialize(object input) => JsonSerializer.Serialize(input);
}

public static class AppStatusConstants
{
    public const string EventType = "APP_STATUS";
}
