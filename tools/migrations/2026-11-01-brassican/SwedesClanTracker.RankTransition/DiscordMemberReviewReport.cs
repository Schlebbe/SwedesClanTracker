using System.Globalization;
using System.Text;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.RankTransition;

public sealed record DiscordMemberReviewReportRow(
    string? DiscordUserId,
    string? DiscordUsername,
    string? GlobalName,
    string? Nick,
    IReadOnlyList<string> ObservedRoleIds,
    IReadOnlyList<string> ObservedManagedRanks,
    IReadOnlyList<int> LinkedPlayerIds,
    IReadOnlyList<string> LinkedTrackerPlayers,
    string? FinalMappedRank,
    string? ApprovedPromotionFloor,
    string? SuggestedTarget,
    string Action,
    string ReviewReason,
    string IdentityStatus,
    IReadOnlyList<string> CandidateDiscordUserIds,
    bool IsTrackerReviewRow);

public sealed class DiscordMemberReviewReportDocument(
    IReadOnlyList<DiscordMemberReviewReportRow> rows,
    string? manifestId = null,
    DateTimeOffset? createdAtUtc = null,
    bool freshObservationUsed = false,
    bool freshObservationComplete = true,
    bool manualMemberRanks = false)
{
    public IReadOnlyList<DiscordMemberReviewReportRow> Rows { get; } = rows;
    public string? ManifestId { get; } = manifestId;
    public DateTimeOffset? CreatedAtUtc { get; } = createdAtUtc;
    public bool FreshObservationUsed { get; } = freshObservationUsed;
    public bool FreshObservationComplete { get; } = freshObservationComplete;
    public bool ManualMemberRanks { get; } = manualMemberRanks;
    public bool ManualReviewRequired => ManualMemberRanks || !FreshObservationComplete || Rows.Any(x => x.Action != DiscordMemberReviewReport.AlreadyAlignedAction);
    public int AccountCount => Rows.Count(x => !x.IsTrackerReviewRow);
    public int UpdateCount => Rows.Count(x => x.Action == DiscordMemberReviewReport.UpdateAction);
    public int AlreadyAlignedCount => Rows.Count(x => x.Action == DiscordMemberReviewReport.AlreadyAlignedAction);
    public int HoldIdentityCount => Rows.Count(x => x.Action == DiscordMemberReviewReport.HoldIdentityAction);
    public int HigherOrSpecialProtectedCount => Rows.Count(x => x.Action == DiscordMemberReviewReport.HigherOrSpecialProtectedAction);
    public int UnresolvedTrackerCount => Rows.Count(x => x.IsTrackerReviewRow);

    public string ToCsv() => DiscordMemberReviewReport.ToCsv(this);
    public string ToMarkdown() => DiscordMemberReviewReport.ToMarkdown(this);
    public string ToConsoleSummary() => DiscordMemberReviewReport.ToConsoleSummary(this);
}

/// <summary>
/// Builds a manual Discord rank review from a frozen manifest and a complete
/// member observation. It performs no database or network work and never
/// guesses an identity, lowers a higher role, or removes an unrelated role.
/// </summary>
public static partial class DiscordMemberReviewReport
{
    public const string UpdateAction = "Update";
    public const string AlreadyAlignedAction = "AlreadyAligned";
    public const string HoldIdentityAction = "HoldIdentity";
    public const string HigherOrSpecialProtectedAction = "HigherOrSpecialProtected";

    private static readonly string[] CsvHeaders =
    [
        "DiscordUserId", "DiscordUsername", "GlobalName", "Nick", "ObservedRoleIds",
        "ObservedManagedRanks", "LinkedPlayerIds", "LinkedTrackerPlayers", "FinalMappedRank",
        "ApprovedPromotionFloor", "SuggestedTarget", "Action", "ReviewReason", "IdentityStatus",
        "CandidateDiscordUserIds", "IsTrackerReviewRow"
    ];

    public static DiscordMemberReviewReportDocument Build(
        RankTransitionManifest manifest,
        IReadOnlyDictionary<int, string>? reviewedIdentityOverrides = null) =>
        BuildCore(manifest, manifest.DiscordMembers, null, null, reviewedIdentityOverrides);

    public static DiscordMemberReviewReportDocument Build(
        RankTransitionManifest manifest,
        IReadOnlyList<DiscordMemberObservation> frozenMembers,
        IReadOnlyDictionary<int, string>? reviewedIdentityOverrides = null) =>
        BuildCore(manifest, frozenMembers, null, null, reviewedIdentityOverrides);

    /// <summary>
    /// Uses fresh read-only member/role state for current columns while
    /// retaining frozen manifest targets for recommendations. Partial fresh
    /// observations are carried as a manual-review condition.
    /// </summary>
    public static DiscordMemberReviewReportDocument Build(
        RankTransitionManifest manifest,
        IReadOnlyList<DiscordMemberObservation> frozenMembers,
        DiscordGuildObservation? freshGuildObservation,
        IReadOnlyDictionary<int, string>? reviewedIdentityOverrides = null) =>
        BuildCore(manifest, freshGuildObservation?.Members ?? frozenMembers, freshGuildObservation, null, reviewedIdentityOverrides);

    public static DiscordMemberReviewReportDocument Build(
        RankTransitionManifest manifest,
        IReadOnlyList<DiscordMemberObservation> frozenMembers,
        DiscordGuildObservation? freshGuildObservation,
        IReadOnlyDictionary<string, string>? frozenRankRoleIds,
        IReadOnlyDictionary<int, string>? reviewedIdentityOverrides = null) =>
        BuildCore(manifest, freshGuildObservation?.Members ?? frozenMembers, freshGuildObservation, frozenRankRoleIds, reviewedIdentityOverrides);

    private static DiscordMemberReviewReportDocument BuildCore(
        RankTransitionManifest manifest,
        IReadOnlyList<DiscordMemberObservation> sourceMembers,
        DiscordGuildObservation? freshGuildObservation,
        IReadOnlyDictionary<string, string>? frozenRankRoleIds,
        IReadOnlyDictionary<int, string>? reviewedIdentityOverrides)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(sourceMembers);
        var members = sourceMembers
            .Where(x => !string.IsNullOrWhiteSpace(x.UserId))
            .GroupBy(x => x.UserId, StringComparer.Ordinal)
            .Select(x => x.First())
            .OrderBy(x => x.UserId, StringComparer.Ordinal)
            .ToArray();
        var players = manifest.Players.Select(x => new DiscordPlayerIdentity(x.PlayerId, x.Username)).ToArray();
        var resolutions = DiscordMemberIdentityResolver.Resolve(players, members, reviewedIdentityOverrides);
        var resolutionByMemberId = resolutions
            .Where(x => x.IsResolved && !string.IsNullOrWhiteSpace(x.DiscordUserId))
            .GroupBy(x => x.DiscordUserId!, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
        var playersById = manifest.Players.ToDictionary(x => x.PlayerId);
        var playerTargets = BuildPlayerTargets(manifest);
        var playerHoldReasons = BuildPlayerHoldReasons(manifest);
        var rankByRoleId = BuildRankByRoleId(manifest, frozenRankRoleIds);
        var protectedRoleIds = manifest.DiscordRoles.Where(x => x.Protected).Select(x => x.RoleId).ToHashSet(StringComparer.Ordinal);
        var knownRoleIds = manifest.DiscordRoles.Select(x => x.RoleId).ToHashSet(StringComparer.Ordinal);
        var rows = new List<DiscordMemberReviewReportRow>(members.Length + resolutions.Count(x => x.RequiresReview));

        foreach (var member in members)
        {
            resolutionByMemberId.TryGetValue(member.UserId, out var linkedResolutions);
            linkedResolutions ??= [];
            var linkedPlayers = linkedResolutions
                .Where(x => playersById.ContainsKey(x.PlayerId))
                .Select(x => playersById[x.PlayerId])
                .OrderBy(x => x.PlayerId)
                .ToArray();
            var linkedTargets = linkedPlayers
                .Select(x => playerTargets.GetValueOrDefault(x.PlayerId))
                .Where(x => x is not null)
                .Cast<PlayerRankTarget>()
                .ToArray();
            var finalRank = HighestRank(linkedTargets.Select(x => x.FinalMappedRank));
            var approvedFloor = HighestRank(linkedTargets.Where(x => x.ApprovedPromotionTarget is not null).Select(x => x.ApprovedPromotionTarget));
            var currentRanks = member.RoleIds
                .Where(rankByRoleId.ContainsKey)
                .Select(roleId => rankByRoleId[roleId])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(RankOrder)
                .ToArray();
            var identityStatus = linkedResolutions.Length == 0
                ? "UnmatchedMember"
                : linkedResolutions.Any(x => x.Status == DiscordMemberIdentityStatus.ReviewedOverride)
                    ? DiscordMemberIdentityStatus.ReviewedOverride.ToString()
                    : DiscordMemberIdentityStatus.UniqueExact.ToString();
            var linkedHoldReasons = linkedPlayers
                .SelectMany(x => playerHoldReasons.GetValueOrDefault(x.PlayerId) ?? [])
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            rows.Add(BuildMemberRow(member, linkedPlayers, finalRank, approvedFloor, currentRanks, identityStatus, linkedHoldReasons, rankByRoleId, protectedRoleIds, knownRoleIds));
        }

        foreach (var resolution in resolutions.Where(x => x.RequiresReview).OrderBy(x => x.PlayerId))
        {
            if (!playersById.TryGetValue(resolution.PlayerId, out var player)) continue;
            var target = playerTargets.GetValueOrDefault(player.PlayerId);
            var reason = resolution.Reason;
            if (resolution.CandidateUserIds.Count > 0) reason += $" Candidate Discord IDs: {string.Join(", ", resolution.CandidateUserIds)}.";
            rows.Add(new DiscordMemberReviewReportRow(null, player.Username, null, null, [], [], [player.PlayerId], [player.Username], target?.FinalMappedRank, target?.ApprovedPromotionTarget, null, HoldIdentityAction, reason, resolution.Status.ToString(), resolution.CandidateUserIds, true));
        }

        return new DiscordMemberReviewReportDocument(rows, manifest.ManifestId, manifest.CreatedAtUtc, freshGuildObservation is not null, freshGuildObservation?.MembersFullyObserved ?? true, manifest.DiscordMemberRanksManual);
    }

    public static string CsvPathFor(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var fullPath = Path.GetFullPath(manifestPath);
        var directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(fullPath)}.discord-members.csv");
    }

    public static string MarkdownPathFor(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var fullPath = Path.GetFullPath(manifestPath);
        var directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(fullPath)}.discord-members.md");
    }

    public static async Task<string> WriteCsvAsync(
        DiscordMemberReviewReportDocument report,
        string manifestPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var path = CsvPathFor(manifestPath);
        EnsureParentDirectory(path);
        await File.WriteAllTextAsync(path, report.ToCsv(), new UTF8Encoding(false), ct);
        return path;
    }

    public static async Task<string> WriteMarkdownAsync(
        DiscordMemberReviewReportDocument report,
        string manifestPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var path = MarkdownPathFor(manifestPath);
        EnsureParentDirectory(path);
        await File.WriteAllTextAsync(path, report.ToMarkdown(), new UTF8Encoding(false), ct);
        return path;
    }

    public static string ToCsv(DiscordMemberReviewReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.Append(string.Join(',', CsvHeaders)).Append("\r\n");
        foreach (var row in report.Rows)
        {
            string?[] fields =
            [
                row.DiscordUserId,
                row.DiscordUsername,
                row.GlobalName,
                row.Nick,
                string.Join(';', row.ObservedRoleIds),
                string.Join(';', row.ObservedManagedRanks),
                string.Join(';', row.LinkedPlayerIds.Select(x => x.ToString(CultureInfo.InvariantCulture))),
                string.Join(';', row.LinkedTrackerPlayers),
                row.FinalMappedRank,
                row.ApprovedPromotionFloor,
                row.SuggestedTarget,
                row.Action,
                row.ReviewReason,
                row.IdentityStatus,
                string.Join(';', row.CandidateDiscordUserIds),
                row.IsTrackerReviewRow ? "true" : "false"
            ];
            builder.Append(string.Join(',', fields.Select(EscapeCsv))).Append("\r\n");
        }

        return builder.ToString();
    }

    public static string ToConsoleSummary(DiscordMemberReviewReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return $"Discord member review: {report.AccountCount} guild accounts, " +
               $"{report.UpdateCount} updates, {report.AlreadyAlignedCount} already aligned, " +
               $"{report.HigherOrSpecialProtectedCount} higher/special protected, " +
               $"{report.HoldIdentityCount} identity holds, {report.UnresolvedTrackerCount} unresolved tracker identities.";
    }

    public static string ToMarkdown(DiscordMemberReviewReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# Discord member rank review");
        builder.AppendLine();
        builder.Append("Manifest: `").Append(MarkdownCell(report.ManifestId)).AppendLine("`");
        builder.Append("Frozen observation (UTC): `")
            .Append(MarkdownCell(report.CreatedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)))
            .AppendLine("`");
        builder.Append("Member rank mode: `").Append(report.ManualMemberRanks ? "manual review" : "review-only").AppendLine("`");
        if (report.FreshObservationUsed)
        {
            builder.Append("Current Discord observation: `")
                .Append(report.FreshObservationComplete ? "complete" : "partial — review required")
                .AppendLine("`");
        }
        builder.AppendLine();
        builder.AppendLine("This is a frozen, manual review list. It is advisory only: verify the live account and role state before changing one managed rank role. Preserve every unrelated/special role and never lower a higher confirmed rank. Refresh this review after cutover with discord-report using the same frozen manifest and fresh Discord observations; do not re-plan the migration or apply this file as a bulk Discord operation.");
        builder.AppendLine();
        builder.AppendLine($"Summary: {report.AccountCount} accounts; {report.UpdateCount} updates; {report.AlreadyAlignedCount} already aligned; {report.HigherOrSpecialProtectedCount} higher/special protected; {report.HoldIdentityCount} identity holds; {report.UnresolvedTrackerCount} unresolved tracker identities.");

        AppendSection(builder, "Accounts needing a managed-rank update", report.Rows.Where(x => x.Action == UpdateAction));
        AppendSection(builder, "Already aligned accounts", report.Rows.Where(x => x.Action == AlreadyAlignedAction));
        AppendSection(builder, "Higher/special protected or identity-held accounts", report.Rows.Where(x => (x.Action is HigherOrSpecialProtectedAction or HoldIdentityAction) && !x.IsTrackerReviewRow));
        AppendTrackerSection(builder, report.Rows.Where(x => x.IsTrackerReviewRow));
        return builder.ToString();
    }

    private static void AppendSection(StringBuilder builder, string title, IEnumerable<DiscordMemberReviewReportRow> source)
    {
        var rows = source.OrderBy(x => x.DiscordUsername ?? x.GlobalName ?? x.DiscordUserId, StringComparer.OrdinalIgnoreCase).ToArray();
        builder.AppendLine().Append("## ").AppendLine(title);
        if (rows.Length == 0)
        {
            builder.AppendLine("_None._");
            return;
        }

        builder.AppendLine("| Discord account | Current managed rank(s) | Linked tracker player(s) | Final mapped / approved floor | Suggested target | Review |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var row in rows)
        {
            var final = row.FinalMappedRank is null
                ? row.ApprovedPromotionFloor
                : row.ApprovedPromotionFloor is null
                    ? row.FinalMappedRank
                    : $"{row.FinalMappedRank} / {row.ApprovedPromotionFloor}";
            builder.Append("| ").Append(MarkdownCell(row.DiscordUsername ?? row.GlobalName ?? row.DiscordUserId))
                .Append(" | ").Append(MarkdownCell(string.Join(", ", row.ObservedManagedRanks)))
                .Append(" | ").Append(MarkdownCell(string.Join(", ", row.LinkedTrackerPlayers)))
                .Append(" | ").Append(MarkdownCell(final))
                .Append(" | ").Append(MarkdownCell(row.SuggestedTarget))
                .Append(" | ").Append(MarkdownCell(row.ReviewReason)).AppendLine(" |");
        }
    }

    private static void AppendTrackerSection(StringBuilder builder, IEnumerable<DiscordMemberReviewReportRow> source)
    {
        var rows = source.OrderBy(x => x.DiscordUsername, StringComparer.OrdinalIgnoreCase).ToArray();
        builder.AppendLine().AppendLine("## Unresolved tracker identities");
        if (rows.Length == 0)
        {
            builder.AppendLine("_None._");
            return;
        }

        builder.AppendLine("These tracker rows have no unique exact Discord member or require an explicit reviewed override. The final mapped/approved rank remains visible, but no Discord target is suggested.");
        builder.AppendLine();
        builder.AppendLine("| Tracker player | Identity status | Final mapped rank | Approved floor | Candidate Discord IDs | Review |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var row in rows)
        {
            builder.Append("| ").Append(MarkdownCell(row.LinkedTrackerPlayers.FirstOrDefault() ?? row.DiscordUsername))
                .Append(" | ").Append(MarkdownCell(row.IdentityStatus))
                .Append(" | ").Append(MarkdownCell(row.FinalMappedRank))
                .Append(" | ").Append(MarkdownCell(row.ApprovedPromotionFloor))
                .Append(" | ").Append(MarkdownCell(string.Join(", ", row.CandidateDiscordUserIds)))
                .Append(" | ").Append(MarkdownCell(row.ReviewReason)).AppendLine(" |");
        }
    }

    private static DiscordMemberReviewReportRow BuildMemberRow(
        DiscordMemberObservation member,
        IReadOnlyList<RankTransitionPlayerObservation> linkedPlayers,
        string? finalRank,
        string? approvedFloor,
        IReadOnlyList<string> currentRanks,
        string identityStatus,
        IReadOnlyList<string> linkedHoldReasons,
        IReadOnlyDictionary<string, string> rankByRoleId,
        IReadOnlySet<string> protectedRoleIds,
        IReadOnlySet<string> knownRoleIds)
    {
        var linkedIds = linkedPlayers.Select(x => x.PlayerId).ToArray();
        var linkedNames = linkedPlayers.Select(x => x.Username).ToArray();
        var roles = OrderedRoleIds(member.RoleIds);
        if (linkedPlayers.Count == 0)
        {
            return new DiscordMemberReviewReportRow(member.UserId, member.Username, member.GlobalName, member.Nick, roles, currentRanks, linkedIds, linkedNames, null, null, null, HoldIdentityAction, "No tracker player matched this Discord account by a unique exact identity or reviewed override.", identityStatus, [], false);
        }

        var reasons = new List<string>();
        if (linkedPlayers.Count > 1) reasons.Add("Multiple tracker players resolve to this Discord account; the highest confirmed target is shown.");
        if (linkedHoldReasons.Count > 0)
        {
            reasons.AddRange(linkedHoldReasons);
            return new DiscordMemberReviewReportRow(member.UserId, member.Username, member.GlobalName, member.Nick, roles, currentRanks, linkedIds, linkedNames, finalRank, approvedFloor, null, HoldIdentityAction, string.Join(" ", reasons), identityStatus, [], false);
        }
        if (string.IsNullOrWhiteSpace(finalRank))
        {
            reasons.Add("The frozen tracker manifest contains no known final mapped rank for the linked player.");
            return new DiscordMemberReviewReportRow(member.UserId, member.Username, member.GlobalName, member.Nick, roles, currentRanks, linkedIds, linkedNames, null, approvedFloor, null, HoldIdentityAction, string.Join(" ", reasons), identityStatus, [], false);
        }

        var targetRole = rankByRoleId.FirstOrDefault(x => RanksEqual(x.Value, finalRank));
        if (targetRole.Key is null)
        {
            reasons.Add($"No frozen configured Discord role ID was observed for final target '{finalRank}'; review the target role before changing membership.");
            return new DiscordMemberReviewReportRow(member.UserId, member.Username, member.GlobalName, member.Nick, roles, currentRanks, linkedIds, linkedNames, finalRank, approvedFloor, null, HoldIdentityAction, string.Join(" ", reasons), identityStatus, [], false);
        }
        var hasUnknownRole = member.RoleIds.Any(x => !knownRoleIds.Contains(x));
        var hasProtectedRole = member.RoleIds.Any(protectedRoleIds.Contains) ||
                               targetRole.Key is not null && protectedRoleIds.Contains(targetRole.Key);
        var currentHighest = HighestRank(currentRanks);
        if (hasUnknownRole) reasons.Add("One or more observed role IDs are absent from the frozen role registry; preserve them and review before changing rank.");
        if (hasProtectedRole) reasons.Add("A protected/special role is present or is the required target; no automatic role operation is suggested.");
        if (currentHighest is not null && CompareRanks(currentHighest, finalRank) > 0)
        {
            reasons.Add($"Observed managed rank '{currentHighest}' is higher than the mapped target '{finalRank}'; do not lower or strip it.");
        }

        var action = hasUnknownRole || hasProtectedRole || currentHighest is not null && CompareRanks(currentHighest, finalRank) > 0
            ? HigherOrSpecialProtectedAction
            : currentHighest is not null && RanksEqual(currentHighest, finalRank)
                ? AlreadyAlignedAction
                : UpdateAction;
        if (action == AlreadyAlignedAction) reasons.Add("The highest observed managed rank already matches the frozen final mapped target.");
        if (action == UpdateAction) reasons.Add("Review the live account, then update only the managed rank role to the suggested target; preserve unrelated roles.");

        return new DiscordMemberReviewReportRow(member.UserId, member.Username, member.GlobalName, member.Nick, roles, currentRanks, linkedIds, linkedNames, finalRank, approvedFloor, action == HigherOrSpecialProtectedAction ? null : finalRank, action, string.Join(" ", reasons), identityStatus, [], false);
    }

    private static Dictionary<int, PlayerRankTarget> BuildPlayerTargets(RankTransitionManifest manifest)
    {
        var finalTargets = manifest.Operations
            .Where(x => x.Kind == TransitionOperationKind.FinalizeApprovedPromotion && int.TryParse(x.TargetKey, out _))
            .GroupBy(x => int.Parse(x.TargetKey, CultureInfo.InvariantCulture))
            .ToDictionary(x => x.Key, x => x.Last().After);
        var approved = manifest.PromotionDecisions
            .Where(x => x.IsActive && x.ApprovedForCutover)
            .GroupBy(x => x.PlayerId)
            .ToDictionary(x => x.Key, x => x.Last());
        var result = new Dictionary<int, PlayerRankTarget>();
        foreach (var player in manifest.Players)
        {
            var finalRank = CanonicalKnownRank(finalTargets.GetValueOrDefault(player.PlayerId) ?? player.DatabaseCurrentRankTarget);
            var approvedTarget = approved.TryGetValue(player.PlayerId, out var decision)
                ? CanonicalKnownRank(finalTargets.GetValueOrDefault(player.PlayerId) ?? decision.EvaluatedEligibleRank)
                : null;
            result[player.PlayerId] = new PlayerRankTarget(finalRank, approvedTarget);
        }

        return result;
    }

    private static Dictionary<int, IReadOnlyList<string>> BuildPlayerHoldReasons(RankTransitionManifest manifest)
    {
        var womByName = manifest.WomObservations
            .GroupBy(x => FrozenRankMapping.Normalize(x.Username), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        var warningByName = manifest.Warnings
            .Where(x => !x.Code.StartsWith("DISCORD", StringComparison.OrdinalIgnoreCase) &&
                        !x.Code.StartsWith("CUTOVER_MEMBER", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => FrozenRankMapping.Normalize(x.Subject), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Details).ToArray(), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<int, IReadOnlyList<string>>();
        foreach (var player in manifest.Players)
        {
            var reasons = new List<string>();
            if (!player.IsActive || player.Status.Equals("REMOVED_CONFIRMED", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("Tracker player is inactive/removed; no Discord rank update is suggested.");
            }
            if (player.Protected)
            {
                reasons.Add(string.IsNullOrWhiteSpace(player.ProtectionReason)
                    ? "Tracker standing/eligibility is protected; no Discord rank update is suggested."
                    : player.ProtectionReason);
            }
            if (womByName.TryGetValue(FrozenRankMapping.Normalize(player.Username), out var wom))
            {
                if (wom.IgnoredMismatch) reasons.Add("Ignored WOM mismatch is preserved; review the actual Discord/game roles independently.");
                if (wom.Protected) reasons.Add("WOM role is special/protected; no forced Discord rank alignment is suggested.");
            }
            if (warningByName.TryGetValue(FrozenRankMapping.Normalize(player.Username), out var warningReasons)) reasons.AddRange(warningReasons);
            if (reasons.Count > 0) result[player.PlayerId] = reasons.Distinct(StringComparer.Ordinal).ToArray();
        }

        return result;
    }

    private static Dictionary<string, string> BuildRankByRoleId(
        RankTransitionManifest manifest,
        IReadOnlyDictionary<string, string>? frozenRankRoleIds)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (frozenRankRoleIds is not null)
        {
            foreach (var entry in frozenRankRoleIds)
            {
                var canonical = CanonicalKnownRank(entry.Key);
                if (!string.IsNullOrWhiteSpace(entry.Value) && canonical is not null)
                {
                    result[entry.Value] = canonical;
                }
            }
        }
        foreach (var role in manifest.DiscordRoles)
        {
            if (result.ContainsKey(role.RoleId)) continue;
            var canonical = !string.IsNullOrWhiteSpace(role.NameTarget)
                ? CanonicalKnownRank(role.NameTarget)
                : CanonicalKnownRank(role.NameBefore, allowLegacyMapping: true);
            if (canonical is not null) result[role.RoleId] = canonical;
        }

        return result;
    }

    private static string? HighestRank(IEnumerable<string?> ranks)
    {
        var known = ranks.Select(x => CanonicalKnownRank(x)).Where(x => x is not null).Cast<string>().OrderBy(RankOrder).ToArray();
        return known.Length == 0 ? null : known[^1];
    }

    private static string? CanonicalKnownRank(string? rank, bool allowLegacyMapping = false)
    {
        if (string.IsNullOrWhiteSpace(rank)) return null;
        var normalized = FrozenRankMapping.Normalize(rank);
        if (allowLegacyMapping && FrozenRankMapping.LegacyMappings.TryGetValue(normalized, out var mappedLegacy))
        {
            return RankRules.IsKnownClanRank(mappedLegacy) ? RankRules.NormalizeRankName(mappedLegacy) : null;
        }
        if (RankRules.IsKnownClanRank(normalized)) return RankRules.NormalizeRankName(normalized);
        return null;
    }

    private static int CompareRanks(string left, string right) => RankOrder(left).CompareTo(RankOrder(right));
    private static int RankOrder(string rank) => RankRules.RankOrder(rank);

    private static bool RanksEqual(string? left, string? right) =>
        left is not null && right is not null &&
        string.Equals(CanonicalKnownRank(left), CanonicalKnownRank(right), StringComparison.OrdinalIgnoreCase);

    private static string[] OrderedRoleIds(IEnumerable<string> roleIds) =>
        roleIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
    }

    private static string MarkdownCell(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "✗";
        return value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    }

    private static string EscapeCsv(string? value)
    {
        var text = value ?? "";
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : text;
    }

    private sealed record PlayerRankTarget(string? FinalMappedRank, string? ApprovedPromotionTarget);
}
