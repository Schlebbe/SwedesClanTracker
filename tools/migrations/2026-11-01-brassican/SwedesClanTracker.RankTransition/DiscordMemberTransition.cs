using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SwedesClanTracker.RankTransition;

/// <summary>
/// The identity and role state observed for one Discord guild member.  The
/// transition tool deliberately keeps all four Discord display fields: a
/// username alone is not enough to safely resolve a renamed account.
/// </summary>
public sealed record DiscordMemberObservation(
    string UserId,
    string Username,
    string? GlobalName,
    string? Nick,
    IReadOnlyList<string> RoleIds,
    string? RoleHash = null,
    string? IdentityHash = null)
{
    public string EffectiveRoleHash =>
        string.IsNullOrWhiteSpace(RoleHash)
            ? DiscordMemberTransitionHelpers.ComputeRoleHash(RoleIds)
            : RoleHash;

    public string EffectiveIdentityHash =>
        string.IsNullOrWhiteSpace(IdentityHash)
            ? DiscordMemberTransitionHelpers.ComputeIdentityHash(this)
            : IdentityHash;
}

public enum DiscordMemberIdentityStatus
{
    UniqueExact,
    ReviewedOverride,
    Missing,
    Ambiguous,
    OverrideTargetMissing,
    InvalidOverride
}

public sealed record DiscordPlayerIdentity(
    int PlayerId,
    string Username,
    IReadOnlyList<string>? VerifiedAliases = null);

public sealed record DiscordMemberIdentityResolution(
    int PlayerId,
    string Username,
    DiscordMemberIdentityStatus Status,
    string? DiscordUserId,
    IReadOnlyList<string> CandidateUserIds,
    string Reason)
{
    public bool IsResolved =>
        Status is DiscordMemberIdentityStatus.UniqueExact or DiscordMemberIdentityStatus.ReviewedOverride;

    public bool RequiresReview => !IsResolved;
}

/// <summary>
/// Resolves tracker identities to Discord members using only compact exact
/// matches and explicitly reviewed overrides.  It intentionally has no
/// fuzzy, leet, edit-distance, or first-result fallback.
/// </summary>
public static class DiscordMemberIdentityResolver
{
    public static IReadOnlyList<DiscordMemberIdentityResolution> Resolve(
        IEnumerable<DiscordPlayerIdentity> players,
        IEnumerable<DiscordMemberObservation> members,
        IReadOnlyDictionary<int, string>? reviewedOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(members);

        var memberList = members
            .Where(x => !string.IsNullOrWhiteSpace(x.UserId))
            .GroupBy(x => x.UserId, StringComparer.Ordinal)
            .Select(x => x.First())
            .ToArray();
        var byKey = memberList
            .SelectMany(member => IdentityKeys(member).Select(key => (Key: key, Member: member)))
            .Where(x => x.Key.Length > 0)
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(
                x => x.Key,
                x => x.Select(y => y.Member).DistinctBy(y => y.UserId, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        return players
            .Select(player => ResolveOne(player, memberList, byKey, reviewedOverrides))
            .ToArray();
    }

    private static DiscordMemberIdentityResolution ResolveOne(
        DiscordPlayerIdentity player,
        IReadOnlyList<DiscordMemberObservation> members,
        IReadOnlyDictionary<string, DiscordMemberObservation[]> byKey,
        IReadOnlyDictionary<int, string>? reviewedOverrides)
    {
        if (reviewedOverrides is not null && reviewedOverrides.TryGetValue(player.PlayerId, out var overrideId))
        {
            if (string.IsNullOrWhiteSpace(overrideId))
            {
                return Review(player, DiscordMemberIdentityStatus.InvalidOverride, [],
                    "The reviewed Discord override is blank; no member may be guessed.");
            }

            var member = members.FirstOrDefault(x => string.Equals(x.UserId, overrideId.Trim(), StringComparison.Ordinal));
            if (member is null)
            {
                return Review(player, DiscordMemberIdentityStatus.OverrideTargetMissing, [],
                    $"The reviewed Discord override '{overrideId.Trim()}' is not present in the frozen guild observation.");
            }

            return new DiscordMemberIdentityResolution(
                player.PlayerId,
                player.Username,
                DiscordMemberIdentityStatus.ReviewedOverride,
                member.UserId,
                [member.UserId],
                "Matched by an explicitly reviewed PlayerId-to-DiscordUserId override.");
        }

        var keys = new[] { player.Username }
            .Concat(player.VerifiedAliases ?? [])
            .Select(DiscordMemberTransitionHelpers.NormalizeCompact)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var candidates = keys
            .SelectMany(key => byKey.GetValueOrDefault(key) ?? [])
            .DistinctBy(x => x.UserId, StringComparer.Ordinal)
            .OrderBy(x => x.UserId, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 1)
        {
            return new DiscordMemberIdentityResolution(
                player.PlayerId,
                player.Username,
                DiscordMemberIdentityStatus.UniqueExact,
                candidates[0].UserId,
                [candidates[0].UserId],
                "Matched to one Discord member by compact exact username/display-name identity.");
        }

        if (candidates.Length > 1)
        {
            return Review(player, DiscordMemberIdentityStatus.Ambiguous,
                candidates.Select(x => x.UserId).ToArray(),
                "More than one Discord member matched an exact normalized identity; an explicit reviewed override is required.");
        }

        return Review(player, DiscordMemberIdentityStatus.Missing, [],
            "No Discord member matched an exact normalized username or verified alias; an explicit reviewed override is required.");
    }

    private static DiscordMemberIdentityResolution Review(
        DiscordPlayerIdentity player,
        DiscordMemberIdentityStatus status,
        IReadOnlyList<string> candidates,
        string reason) =>
        new(player.PlayerId, player.Username, status, null, candidates, reason);

    private static IEnumerable<string> IdentityKeys(DiscordMemberObservation member)
    {
        yield return DiscordMemberTransitionHelpers.NormalizeCompact(member.Username);
        yield return DiscordMemberTransitionHelpers.NormalizeCompact(member.GlobalName);
        yield return DiscordMemberTransitionHelpers.NormalizeCompact(member.Nick);
    }
}

public sealed record DiscordRankMembershipCandidate(
    int PlayerId,
    string Username,
    string DiscordUserId,
    string TargetRank,
    string TargetRoleId,
    int TargetFloor,
    bool Confirmed = true);

public sealed record DiscordRankMembershipPlan(
    string DiscordUserId,
    IReadOnlyList<int> PlayerIds,
    IReadOnlyList<string> TargetRanks,
    string TargetRoleId,
    int TargetFloor,
    IReadOnlyList<string> ManagedRankRoleIds,
    IReadOnlyList<string> OriginalRoleIds,
    IReadOnlyList<string> ExpectedAfterRoleIds,
    IReadOnlyList<string> SupersededManagedRoleIds,
    string ExpectedBeforeRoleHash,
    string ExpectedAfterRoleHash,
    string OwnedIntentId,
    bool IsNoOp,
    bool RequiresReview,
    string? ReviewReason,
    string? ExpectedIdentityHash = null)
{
    public IReadOnlyList<string> PreservedRoleIds =>
        OriginalRoleIds
            .Except(SupersededManagedRoleIds, StringComparer.Ordinal)
            .Except([TargetRoleId], StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

    public DiscordRankMembershipMutationRequest ToMutationRequest(bool resumeOwnedIntent = false) =>
        new(
            DiscordUserId,
            TargetRoleId,
            ManagedRankRoleIds,
            OriginalRoleIds,
            ExpectedAfterRoleIds,
            ExpectedBeforeRoleHash,
            ExpectedAfterRoleHash,
            OwnedIntentId,
            resumeOwnedIntent,
            ExpectedIdentityHash);
}

/// <summary>
/// The frozen role-set contract consumed by the Discord gateway.  The two
/// complete sets include non-rank roles so a mutation can prove that no
/// unrelated membership changed.  Only the managed IDs derived by the helper
/// may be deleted.
/// </summary>
public sealed record DiscordRankMembershipMutationRequest(
    string DiscordUserId,
    string TargetRoleId,
    IReadOnlyList<string> ManagedRankRoleIds,
    IReadOnlyList<string> ExpectedBeforeRoleIds,
    IReadOnlyList<string> ExpectedAfterRoleIds,
    string ExpectedBeforeRoleHash,
    string ExpectedAfterRoleHash,
    string OwnedIntentId,
    bool ResumeOwnedIntent = false,
    string? ExpectedIdentityHash = null)
{
    public DiscordRankMembershipMutationRequest ForRollback() => this with
    {
        TargetRoleId = "",
        ExpectedBeforeRoleIds = ExpectedAfterRoleIds,
        ExpectedAfterRoleIds = ExpectedBeforeRoleIds,
        ExpectedBeforeRoleHash = ExpectedAfterRoleHash,
        ExpectedAfterRoleHash = ExpectedBeforeRoleHash,
        ResumeOwnedIntent = true
    };
}

public sealed record DiscordRankMembershipMutationResult(
    bool Success,
    int HttpStatus,
    string Details,
    DiscordMemberObservation? MemberBefore,
    DiscordMemberObservation? MemberAfter,
    IReadOnlyList<string> AddedRoleIds,
    IReadOnlyList<string> RemovedRoleIds,
    bool AlreadyApplied,
    bool Partial,
    bool OwnedIntent,
    string? OwnedIntentId);

public static class DiscordMemberTransitionHelpers
{
    public static string NormalizeCompact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    public static string ComputeRoleHash(IEnumerable<string> roleIds)
    {
        var canonical = roleIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", canonical)))).ToLowerInvariant();
    }

    public static string ComputeIdentityHash(DiscordMemberObservation member) =>
        ComputeIdentityHash(member.UserId, member.Username, member.GlobalName, member.Nick);

    public static string ComputeIdentityHash(string userId, string username, string? globalName, string? nick)
    {
        var payload = string.Join("\n", [
            userId ?? "",
            username ?? "",
            globalName ?? "",
            nick ?? ""
        ]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public static string ComputeMemberHash(IEnumerable<DiscordMemberObservation> members)
    {
        var canonical = members
            .Where(x => !string.IsNullOrWhiteSpace(x.UserId))
            .OrderBy(x => x.UserId, StringComparer.Ordinal)
            .Select(x => string.Join("|", [
                x.UserId,
                x.EffectiveIdentityHash,
                x.EffectiveRoleHash
            ]));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", canonical)))).ToLowerInvariant();
    }

    public static IReadOnlyList<string> CanonicalRoleIds(IEnumerable<string> roleIds) =>
        roleIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

    public static IReadOnlySet<string> ExpectedAfterRoleSet(
        IEnumerable<string> beforeRoleIds,
        IEnumerable<string> managedRankRoleIds,
        string targetRoleId)
    {
        var before = beforeRoleIds.ToHashSet(StringComparer.Ordinal);
        var managed = managedRankRoleIds.ToHashSet(StringComparer.Ordinal);
        foreach (var roleId in managed) if (!string.Equals(roleId, targetRoleId, StringComparison.Ordinal)) before.Remove(roleId);
        if (!string.IsNullOrWhiteSpace(targetRoleId)) before.Add(targetRoleId);
        return before;
    }

    public static bool UnaffectedRolesPreserved(
        IEnumerable<string> beforeRoleIds,
        IEnumerable<string> afterRoleIds,
        IEnumerable<string> managedRankRoleIds)
    {
        var before = beforeRoleIds.ToHashSet(StringComparer.Ordinal);
        var after = afterRoleIds.ToHashSet(StringComparer.Ordinal);
        var managed = managedRankRoleIds.ToHashSet(StringComparer.Ordinal);
        return before.Except(managed).ToHashSet(StringComparer.Ordinal)
            .SetEquals(after.Except(managed));
    }
}

public static class DiscordRankMembershipPlanner
{
    public static IReadOnlyList<DiscordRankMembershipPlan> BuildPlans(
        IEnumerable<DiscordRankMembershipCandidate> candidates,
        IEnumerable<DiscordMemberObservation> members,
        IReadOnlyCollection<string> managedRankRoleIds,
        Func<string, string>? ownedIntentIdFactory = null,
        IReadOnlyDictionary<string, int>? rankFloorByRoleId = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(managedRankRoleIds);

        var managed = managedRankRoleIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var memberById = members
            .Where(x => !string.IsNullOrWhiteSpace(x.UserId))
            .GroupBy(x => x.UserId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        return candidates
            .Where(x => x.Confirmed && !string.IsNullOrWhiteSpace(x.DiscordUserId))
            .GroupBy(x => x.DiscordUserId, StringComparer.Ordinal)
            .Select(group => BuildOne(group.Key, group.ToArray(), memberById, managed, ownedIntentIdFactory, rankFloorByRoleId))
            .OrderBy(x => x.DiscordUserId, StringComparer.Ordinal)
            .ToArray();
    }

    private static DiscordRankMembershipPlan BuildOne(
        string discordUserId,
        IReadOnlyList<DiscordRankMembershipCandidate> candidates,
        IReadOnlyDictionary<string, DiscordMemberObservation> memberById,
        IReadOnlyList<string> managed,
        Func<string, string>? ownedIntentIdFactory,
        IReadOnlyDictionary<string, int>? rankFloorByRoleId)
    {
        var playerIds = candidates.Select(x => x.PlayerId).OrderBy(x => x).ToArray();
        var targetFloor = candidates.Max(x => x.TargetFloor);
        var top = candidates.Where(x => x.TargetFloor == targetFloor).ToArray();
        var targetRoleIds = top.Select(x => x.TargetRoleId).Distinct(StringComparer.Ordinal).ToArray();
        var targetRoleId = targetRoleIds.Length == 1 ? targetRoleIds[0] : "";
        var targetRanks = candidates.Select(x => x.TargetRank).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var intent = ownedIntentIdFactory?.Invoke(discordUserId)
            ?? $"discord-member-rank:{discordUserId}:{string.Join(",", playerIds)}:{targetRoleId}";

        if (!memberById.TryGetValue(discordUserId, out var member))
        {
            return ReviewPlan(discordUserId, playerIds, targetRanks, targetRoleId, targetFloor, managed, intent,
                "The resolved Discord member is absent from the frozen member observation.");
        }

        if (targetRoleIds.Length != 1 || string.IsNullOrWhiteSpace(targetRoleId) || !managed.Contains(targetRoleId, StringComparer.Ordinal))
        {
            return ReviewPlan(discordUserId, playerIds, targetRanks, targetRoleId, targetFloor, managed, intent,
                "The highest confirmed accounts do not agree on one configured target rank role.");
        }

        var original = DiscordMemberTransitionHelpers.CanonicalRoleIds(member.RoleIds);
        var currentManaged = original.Where(managed.Contains).ToArray();
        var unknownManagedRole = currentManaged
            .Where(roleId => !string.Equals(roleId, targetRoleId, StringComparison.Ordinal))
            .Any(roleId => rankFloorByRoleId is null || !rankFloorByRoleId.ContainsKey(roleId));
        var currentHighest = currentManaged
            .Select(roleId =>
                string.Equals(roleId, targetRoleId, StringComparison.Ordinal)
                    ? targetFloor
                    : rankFloorByRoleId?.GetValueOrDefault(roleId, int.MinValue) ?? int.MinValue)
            .DefaultIfEmpty(-1)
            .Max();
        if (unknownManagedRole)
        {
            return new DiscordRankMembershipPlan(
                discordUserId, playerIds, targetRanks, targetRoleId, targetFloor, managed, original, original, [],
                member.EffectiveRoleHash, member.EffectiveRoleHash, intent, true, true,
                "An existing managed rank role has no frozen rank-floor mapping; refusing to risk a downgrade.");
        }
        if (currentHighest > targetFloor)
        {
            return new DiscordRankMembershipPlan(
                discordUserId, playerIds, targetRanks, targetRoleId, targetFloor, managed, original, original, [],
                member.EffectiveRoleHash, member.EffectiveRoleHash, intent, true, true,
                "The member already has a higher confirmed managed rank; refusing to downgrade or strip it.",
                member.EffectiveIdentityHash);
        }

        var expectedAfter = DiscordMemberTransitionHelpers.ExpectedAfterRoleSet(original, managed, targetRoleId)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        var superseded = currentManaged
            .Where(x => !string.Equals(x, targetRoleId, StringComparison.Ordinal))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        var expectedAfterHash = DiscordMemberTransitionHelpers.ComputeRoleHash(expectedAfter);
        return new DiscordRankMembershipPlan(
            discordUserId, playerIds, targetRanks, targetRoleId, targetFloor, managed, original, expectedAfter,
            superseded, member.EffectiveRoleHash, expectedAfterHash, intent,
            original.SequenceEqual(expectedAfter, StringComparer.Ordinal), false, null,
            member.EffectiveIdentityHash);
    }

    private static DiscordRankMembershipPlan ReviewPlan(
        string discordUserId,
        IReadOnlyList<int> playerIds,
        IReadOnlyList<string> targetRanks,
        string targetRoleId,
        int targetFloor,
        IReadOnlyList<string> managed,
        string intent,
        string reason) =>
        new(discordUserId, playerIds, targetRanks, targetRoleId, targetFloor, managed, [], [], [], "", "", intent, false, true, reason);
}
