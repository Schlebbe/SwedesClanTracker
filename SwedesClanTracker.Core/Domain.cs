using System.Text.Json;

namespace SwedesClanTracker.Core;

public enum PlayerStatus { ACTIVE, NEW_PENDING_REVIEW, MISSING_PENDING_REVIEW, MERGE_SUGGESTED, REMOVED_CONFIRMED }
public enum PromotionStatus { PENDING, APPROVED, DISMISSED }
public enum PromotionCandidateType { wom_already_at_new_rank, needs_wom_rank_update, unknown_wom_role }

public class Player
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string CurrentRank { get; set; } = "Brassican";
    public string EligibleRank { get; set; } = "Brassican";
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
    public PromotionCandidateType CandidateType { get; set; } = PromotionCandidateType.unknown_wom_role;
    public string? WomRoleAtEvaluation { get; set; }
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
    public string Rank { get; init; } = "Brassican";
    public string Explanation { get; init; } = "No rank requirement met.";
}

public sealed record RankRequirement(string Rank, string Description);

public static class RankEvaluator
{
    private sealed record RankEvaluationRule(string Rank, Func<PlayerSnapshot, bool> Rule, string Why);

    private static readonly IReadOnlyList<RankEvaluationRule> Rules =
    [
        new("Brassican", _ => true, "No rank requirement met."),
        new("Commander", s => s.TotalLevel >= 2200, "Total level >= 2200"),
        new("Lieutenant", s => s.Ehb >= 750 || s.Ehp >= 1000, "EHB >= 750 OR EHP >= 1000"),
        new("Captain", s => s.Ehb >= 1000 || s.Ehp >= 1500 || s.PetCount >= 10 || s.Collections >= 800, "EHB >= 1000 OR EHP >= 1500 OR pets >= 10 OR collections >= 800"),
        new("General", s => s.Ehb >= 1250 || s.Ehp >= 1750 || s.PetCount >= 15 || s.Collections >= 900, "EHB >= 1250 OR EHP >= 1750 OR pets >= 15 OR collections >= 900"),
        new("Colonel", s => s.Ehb >= 1500 || s.Ehp >= 2000 || s.PetCount >= 25 || s.Collections >= 1050, "EHB >= 1500 OR EHP >= 2000 OR pets >= 25 OR collections >= 1050"),
        new("Brigadier", s => s.Ehb >= 2000 || s.Ehp >= 2500 || s.PetCount >= 35 || s.Collections >= 1200, "EHB >= 2000 OR EHP >= 2500 OR pets >= 35 OR collections >= 1200"),
        new("Admiral", s => s.Ehb >= 3000 || s.Ehp >= 4000 || s.PetCount >= 45 || s.Collections >= 1350, "EHB >= 3000 OR EHP >= 4000 OR pets >= 45 OR collections >= 1350"),
        new("Marshal", s => s.Ehb >= 4000 || s.Ehp >= 5000 || s.PetCount >= 55 || s.Collections >= 1450, "EHB >= 4000 OR EHP >= 5000 OR pets >= 55 OR collections >= 1450"),
        new("Beast", s => s.Ehb >= 5000 || s.Ehp >= 7000 || s.PetCount >= 65 || s.Collections >= 1550, "EHB >= 5000 OR EHP >= 7000 OR pets >= 65 OR collections >= 1550")
    ];

    public static IReadOnlyList<RankRequirement> Requirements =>
        Rules.Select(x => new RankRequirement(x.Rank, x.Why)).ToArray();

    public static RankResult Evaluate(PlayerSnapshot snapshot)
    {
        string best = "Brassican";
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
    private static readonly string[] OrderedRankNames =
    [
        "Brassican", "Commander", "Lieutenant", "Captain", "General", "Colonel", "Brigadier", "Admiral", "Marshal", "Beast"
    ];

    private static readonly string[] AssignableClanRankNames = OrderedRankNames.ToArray();

    private static readonly string[] SpecialWomRoles =
    [
        "imp", "kitten", "administrator", "deputy owner", "owner", "short green guy", "recruit", "apothecary", "wily", "goblin"
    ];

    private static readonly HashSet<string> NormalizedSpecialWomRoles =
        SpecialWomRoles.Select(NormalizeRankName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> NormalizedKnownNonClanWomRoles =
        new(["member"], StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> OrderedClanRanks => OrderedRankNames;

    public static IReadOnlyList<string> AssignableClanRanks => AssignableClanRankNames;

    public static string NormalizeRankName(string rank) => (rank ?? "").Replace('_', ' ').Trim();

    public static string ToWomRoleValue(string rank) => NormalizeRankName(rank).ToLowerInvariant().Replace(' ', '_');

    public static int RankOrder(string rank)
    {
        var normalized = NormalizeRankName(rank);
        for (var i = 0; i < OrderedRankNames.Length; i++)
        {
            if (string.Equals(OrderedRankNames[i], normalized, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return 0;
    }

    public static bool IsKnownClanRank(string rank)
    {
        var normalized = NormalizeRankName(rank);
        return OrderedRankNames.Any(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSpecialWomRole(string role)
    {
        var normalized = NormalizeRankName(role);
        if (string.IsNullOrWhiteSpace(normalized) || NormalizedKnownNonClanWomRoles.Contains(normalized)) return false;

        return NormalizedSpecialWomRoles.Contains(normalized) || !IsKnownClanRank(normalized);
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
            // Member is a Wise Old Man non-clan role, not an alias for the
            // assignable Brassican rank (or any other clan rank).
            return PromotionCandidateType.needs_wom_rank_update;
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
