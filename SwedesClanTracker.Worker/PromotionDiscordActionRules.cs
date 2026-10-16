using SwedesClanTracker.Core;

namespace SwedesClanTracker.Worker;

public static class PromotionDiscordActionRules
{
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
        var existingRanks = existingRankNames
            .Where(RankRules.IsKnownClanRank)
            .Select(RankRules.NormalizeRankName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!RankRules.AssignableClanRanks.Contains(normalizedNewRank, StringComparer.OrdinalIgnoreCase))
        {
            return DiscordPromotionRankRoleDecision.Reject(
                $"`{normalizedNewRank}` is not a configured assignable clan rank.");
        }

        if (!RankRules.IsKnownClanRank(normalizedOldRank))
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

        if (string.Equals(normalizedOldRank, "Recruit", StringComparison.OrdinalIgnoreCase))
        {
            return existingRanks.Count == 0
                ? new DiscordPromotionRankRoleDecision(
                    DiscordPromotionRankRoleAction.Add,
                    existingRanks,
                    "No configured rank role was present, so the new rank role can be added.")
                : DiscordPromotionRankRoleDecision.Reject(
                    $"Expected no configured rank role for Recruit, but found {FormatRanks(existingRanks)}.");
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
