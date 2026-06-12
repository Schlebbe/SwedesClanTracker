namespace SwedesClanTracker.Core;

public static class TempleNameChangeReviewEventTypes
{
    public const string Required = "TEMPLE_NAME_CHANGE_REQUIRED";
    public const string DiscordPosted = "TEMPLE_NAME_CHANGE_DISCORD_POSTED";
    public const string ActionApplied = "TEMPLE_NAME_CHANGE_ACTION_APPLIED";
    public const string SuppressedCard = "TEMPLE_NAME_CHANGE_SUPPRESSED_CARD";
}

public sealed record TempleNameChangeOldCandidate(
    int PlayerId,
    string Username,
    string CurrentRank,
    int? WomMissingEventId,
    DateTimeOffset? WomMissingCreatedAt,
    int? TempleMissingEventId,
    DateTimeOffset? TempleMissingCreatedAt);

public sealed record TempleNameChangeWomOnlyCandidate(
    int RequiredEventId,
    string Username,
    string WomRole,
    DateTimeOffset CreatedAt);

public sealed record TempleNameChangeHandledPair(
    string PreviousUsername,
    string NewUsername,
    string Action,
    DateTimeOffset HandledAt);

public sealed record TempleNameChangeOpenMerge(
    string? PreviousUsername,
    string? NewUsername);

public sealed record TempleNameChangeDetectionInput(
    DateTimeOffset Now,
    TimeSpan RecentWindow,
    IReadOnlyList<TempleNameChangeOldCandidate> OldCandidates,
    IReadOnlyList<TempleNameChangeWomOnlyCandidate> WomOnlyCandidates,
    IReadOnlyList<TempleNameChangeOpenMerge> OpenMerges,
    IReadOnlyList<TempleNameChangeHandledPair> HandledPairs);

public sealed record TempleNameChangeDetection(
    int PreviousPlayerId,
    string PreviousUsername,
    string NewUsername,
    string Rank,
    string WomRole,
    int? WomMissingEventId,
    int? TempleMissingEventId,
    int WomOnlyEventId);

public static class TempleNameChangeDetector
{
    public static TempleNameChangeDetection? Detect(TempleNameChangeDetectionInput input)
    {
        if (input.WomOnlyCandidates.Count != 1) return null;

        var newCandidate = input.WomOnlyCandidates[0];
        if (!IsRecent(newCandidate.CreatedAt, input.Now, input.RecentWindow)) return null;
        if (!RankRules.IsKnownClanRank(newCandidate.WomRole)) return null;

        var openMergePrevious = input.OpenMerges
            .Select(x => Normalize(x.PreviousUsername ?? ""))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var openMergeNew = input.OpenMerges
            .Select(x => Normalize(x.NewUsername ?? ""))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (openMergeNew.Contains(Normalize(newCandidate.Username))) return null;

        var plausible = input.OldCandidates
            .Where(x => HasRecentRequirement(x, input.Now, input.RecentWindow))
            .Where(x => !openMergePrevious.Contains(Normalize(x.Username)))
            .Where(x => IsRankCompatible(x.CurrentRank, newCandidate.WomRole))
            .Where(x => !WasHandled(input.HandledPairs, x, newCandidate))
            .ToList();

        if (plausible.Count != 1) return null;

        var oldCandidate = plausible[0];
        return new TempleNameChangeDetection(
            oldCandidate.PlayerId,
            UsernameRules.NormalizeUsername(oldCandidate.Username),
            UsernameRules.NormalizeUsername(newCandidate.Username),
            RankRules.NormalizeRankName(oldCandidate.CurrentRank),
            RankRules.NormalizeRankName(newCandidate.WomRole),
            oldCandidate.WomMissingEventId,
            oldCandidate.TempleMissingEventId,
            newCandidate.RequiredEventId);
    }

    private static bool HasRecentRequirement(TempleNameChangeOldCandidate candidate, DateTimeOffset now, TimeSpan window)
    {
        return (candidate.WomMissingCreatedAt.HasValue && IsRecent(candidate.WomMissingCreatedAt.Value, now, window)) ||
            (candidate.TempleMissingCreatedAt.HasValue && IsRecent(candidate.TempleMissingCreatedAt.Value, now, window));
    }

    private static bool IsRecent(DateTimeOffset value, DateTimeOffset now, TimeSpan window)
    {
        return value <= now && now - value <= window;
    }

    private static bool IsRankCompatible(string previousRank, string womRole)
    {
        if (!RankRules.IsKnownClanRank(previousRank)) return false;
        if (!RankRules.IsKnownClanRank(womRole)) return false;
        return string.Equals(
            RankRules.NormalizeRankName(previousRank),
            RankRules.NormalizeRankName(womRole),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool WasHandled(
        IReadOnlyList<TempleNameChangeHandledPair> handledPairs,
        TempleNameChangeOldCandidate oldCandidate,
        TempleNameChangeWomOnlyCandidate newCandidate)
    {
        var latestEvidenceAt = new[]
            {
                oldCandidate.WomMissingCreatedAt,
                oldCandidate.TempleMissingCreatedAt,
                newCandidate.CreatedAt
            }
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();

        return handledPairs.Any(x =>
            (string.Equals(x.Action, "confirm", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.Action, "decline", StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(Normalize(x.PreviousUsername), Normalize(oldCandidate.Username), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Normalize(x.NewUsername), Normalize(newCandidate.Username), StringComparison.OrdinalIgnoreCase) &&
            x.HandledAt >= latestEvidenceAt);
    }

    private static string Normalize(string input) => UsernameRules.NormalizeUsername(input);
}
