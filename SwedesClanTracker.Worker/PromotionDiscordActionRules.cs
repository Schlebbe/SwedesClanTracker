using SwedesClanTracker.Core;

namespace SwedesClanTracker.Worker;

public static class PromotionDiscordActionRules
{
    public const string GuestRoleName = "Guest";

    public static DiscordMemberGuessMatch? FindUniqueExactMatch(DiscordMemberGuessResult guess)
    {
        var exactMatches = guess.Matches
            .Where(x => x.Strength == DiscordMemberMatchStrength.Exact)
            .Take(2)
            .ToList();

        return exactMatches.Count == 1 ? exactMatches[0] : null;
    }

    public static DiscordPromotionRankRoleDecision EvaluateRankRoleState(
        string oldRank,
        string newRank,
        IEnumerable<string> existingRankNames)
    {
        var normalizedOldRank = RankRules.NormalizeRankName(oldRank);
        var normalizedNewRank = RankRules.NormalizeRankName(newRank);
        var isLegacyRecruit = string.Equals(normalizedOldRank, "Recruit", StringComparison.OrdinalIgnoreCase);
        var existingRoleNames = existingRankNames
            .Select(RankRules.NormalizeRankName)
            .ToList();
        var existingRanks = existingRoleNames
            .Where(RankRules.IsKnownClanRank)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!RankRules.AssignableClanRanks.Contains(normalizedNewRank, StringComparer.OrdinalIgnoreCase))
        {
            return DiscordPromotionRankRoleDecision.Reject(
                $"`{normalizedNewRank}` is not a configured assignable clan rank.");
        }

        // Recruit is no longer an assignable clan rank. Keep this narrow
        // compatibility path for stale pre-transition promotion candidates;
        // external WOM Recruit roles remain special and are never treated as
        // an ordinary rank role by this method.
        if (!RankRules.IsKnownClanRank(normalizedOldRank) && !isLegacyRecruit)
        {
            return DiscordPromotionRankRoleDecision.Reject(
                $"The promotion's previous rank `{normalizedOldRank}` is not a recognized clan rank.");
        }

        if (existingRanks.Count == 1 &&
            string.Equals(existingRanks[0], normalizedNewRank, StringComparison.OrdinalIgnoreCase))
        {
            return new DiscordPromotionRankRoleDecision(
                DiscordPromotionRankRoleAction.AlreadyCorrect,
                existingRanks,
                "The Discord member already has the requested rank role.");
        }

        if (string.Equals(normalizedOldRank, "Brassican", StringComparison.OrdinalIgnoreCase) &&
            existingRanks.Count == 0 &&
            (existingRoleNames.Count == 0 || existingRoleNames.All(x => string.Equals(
                x,
                GuestRoleName,
                StringComparison.OrdinalIgnoreCase))) &&
            !existingRoleNames.Any(x => string.Equals(x, "Recruit", StringComparison.OrdinalIgnoreCase)))
        {
            return new DiscordPromotionRankRoleDecision(
                DiscordPromotionRankRoleAction.Add,
                existingRanks,
                "The member has no configured previous rank role, so the new rank role can be added.");
        }

        if (isLegacyRecruit)
        {
            return existingRanks.Count == 0
                ? new DiscordPromotionRankRoleDecision(
                    DiscordPromotionRankRoleAction.Add,
                    existingRanks,
                    "Legacy Recruit had no configured rank role, so the new rank role can be added.")
                : DiscordPromotionRankRoleDecision.Reject(
                    $"Expected no configured rank role for legacy Recruit, but found {FormatRanks(existingRanks)}.");
        }

        if (existingRanks.Count == 1 &&
            string.Equals(existingRanks[0], normalizedOldRank, StringComparison.OrdinalIgnoreCase))
        {
            return new DiscordPromotionRankRoleDecision(
                DiscordPromotionRankRoleAction.Replace,
                existingRanks,
                $"The expected previous rank role `{normalizedOldRank}` can be replaced.");
        }

        var found = existingRanks.Count == 0 ? "no configured rank role" : FormatRanks(existingRanks);
        return DiscordPromotionRankRoleDecision.Reject(
            $"Expected the previous rank role `{normalizedOldRank}`, but found {found}.");
    }

    private static string FormatRanks(IReadOnlyCollection<string> ranks) =>
        ranks.Count == 0 ? "no configured rank role" : string.Join(", ", ranks);
}

public enum DiscordPromotionRankRoleAction
{
    Reject,
    Add,
    Replace,
    AlreadyCorrect
}

public sealed record DiscordPromotionRankRoleDecision(
    DiscordPromotionRankRoleAction Action,
    IReadOnlyList<string> ExistingRanks,
    string Details)
{
    public bool CanApply => Action != DiscordPromotionRankRoleAction.Reject;

    public static DiscordPromotionRankRoleDecision Reject(string details) =>
        new(DiscordPromotionRankRoleAction.Reject, [], details);
}
