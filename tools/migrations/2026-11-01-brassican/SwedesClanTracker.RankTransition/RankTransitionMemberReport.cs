using System.Globalization;
using System.Text;

namespace SwedesClanTracker.RankTransition;

/// <summary>
/// One row in the operator-facing manual game cutover list. A null target is
/// intentional: the row is a hold, an approval item, or an inactive/WOM-only
/// record and must not be used to force an in-game change. Approved promotion
/// fields describe a frozen planned cutover and never mean a live approval.
/// </summary>
public sealed record RankTransitionMemberReportRow(
    int? PlayerId,
    string Username,
    string Status,
    string? TrackerRankBefore,
    string? TrackerRankAfter,
    string? WomRoleBefore,
    string? WomRoleAfter,
    string? InGameCutoverTarget,
    string? CalculatedEligibleRank,
    string? PendingPromotionTarget,
    string Action,
    string ReviewReason,
    string? FrozenSourceRole,
    string? ApprovedPromotionTarget = null,
    string? ApprovalState = null,
    string? DiscordReviewReason = null);

public sealed class RankTransitionMemberReportDocument(
    IReadOnlyList<RankTransitionMemberReportRow> rows,
    string? manifestId = null,
    DateTimeOffset? createdAtUtc = null,
    string? cutoverApprovalPolicyVersion = null,
    IReadOnlyList<RankTransitionManualWomCheck>? manualWomChecks = null,
    IReadOnlyDictionary<string, string>? frozenLegacyMappings = null,
    IReadOnlyDictionary<string, string>? frozenWomMappings = null)
{
    public IReadOnlyList<RankTransitionMemberReportRow> Rows { get; } = rows;
    public string? ManifestId { get; } = manifestId;
    public DateTimeOffset? CreatedAtUtc { get; } = createdAtUtc;
    public string? CutoverApprovalPolicyVersion { get; } = cutoverApprovalPolicyVersion;
    public IReadOnlyList<RankTransitionManualWomCheck> ManualWomChecks { get; } = manualWomChecks ?? [];
    public IReadOnlyDictionary<string, string> FrozenLegacyMappings { get; } = frozenLegacyMappings ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> FrozenWomMappings { get; } = frozenWomMappings ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public int ManualCutoverReviewCount => Rows.Count(x => x.Action == RankTransitionMemberReport.CutoverReviewAction);
    public int PendingApprovalCount => Rows.Count(x => !string.IsNullOrWhiteSpace(x.PendingPromotionTarget));
    public int ApprovedCutoverCount => Rows.Count(x => x.ApprovalState == RankTransitionMemberReport.ApprovedForCutoverState);
    public int HoldCount => Rows.Count(x => x.Action == RankTransitionMemberReport.HoldAction);
    public int InactiveNoChangeCount => Rows.Count(x => x.Action == RankTransitionMemberReport.InactiveNoChangeAction);
    public int NoChangeCount => Rows.Count(x => x.Action == RankTransitionMemberReport.NoChangeAction);
    public int WomOnlyHoldCount => Rows.Count(x => x.Status == RankTransitionMemberReport.WomOnlyStatus);

    public string ToCsv() => RankTransitionMemberReport.ToCsv(this);

    public string ToConsoleSummary() => RankTransitionMemberReport.ToConsoleSummary(this);

    public string ToMarkdown() => RankTransitionMemberReport.ToMarkdown(this);
}

/// <summary>
/// Builds the human review list from a frozen manifest only. It deliberately
/// has no database, HTTP, or live policy dependency: the reviewed manifest is
/// the complete input for this export.
/// </summary>
public static class RankTransitionMemberReport
{
    public const string CutoverReviewAction = "CUTOVER_REVIEW";
    public const string PendingApprovalAction = "PENDING_APPROVAL";
    public const string ApprovedForCutoverState = "APPROVED_FOR_CUTOVER";
    public const string PendingApprovalState = "PENDING_APPROVAL";
    public const string HoldAction = "HOLD";
    public const string InactiveNoChangeAction = "INACTIVE_NO_CHANGE";
    public const string NoChangeAction = "NO_CHANGE";
    public const string WomOnlyStatus = "WOM_ONLY";

    private static readonly string[] OrderedRanks =
    [
        "Brassican", "Commander", "Lieutenant", "Captain", "General",
        "Colonel", "Brigadier", "Admiral", "Marshal", "Beast"
    ];

    private static readonly string[] CsvHeaders =
    [
        "PlayerId", "Username", "Status", "TrackerRankBefore", "TrackerRankAfter",
        "WomRoleBefore", "WomRoleAfter", "InGameCutoverTarget", "CalculatedEligibleRank",
        "PendingPromotionTarget", "ApprovedPromotionTarget", "ApprovalState", "DiscordReviewReason",
        "Action", "ReviewReason", "FrozenSourceRole"
    ];

    public static RankTransitionMemberReportDocument Build(RankTransitionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var frozen = manifest.FrozenMapping;
        var womByUsername = manifest.WomObservations
            .GroupBy(x => Normalize(x.Username), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        var playersByUsername = manifest.Players
            .GroupBy(x => Normalize(x.Username), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        var pendingByPlayer = ReadPendingPromotions(manifest);
        var approvedByPlayer = ReadApprovedPromotions(manifest);
        var finalTrackerRanks = ReadFinalTrackerRanks(manifest);
        var finalWomRanks = ReadFinalWomRanks(manifest);
        var warningReasons = ReadHoldWarningReasons(manifest);
        var discordReviewReasons = ReadDiscordReviewReasons(manifest);
        var rows = new List<RankTransitionMemberReportRow>(manifest.Players.Count + manifest.WomObservations.Count);

        foreach (var player in manifest.Players)
        {
            womByUsername.TryGetValue(Normalize(player.Username), out var wom);
            pendingByPlayer.TryGetValue(player.PlayerId, out var pendingPromotionTarget);
            approvedByPlayer.TryGetValue(player.PlayerId, out var approvedPromotion);
            finalTrackerRanks.TryGetValue(player.PlayerId, out var finalTrackerRank);
            finalWomRanks.TryGetValue(Normalize(player.Username), out var finalWomRank);
            warningReasons.TryGetValue(Normalize(player.Username), out var holdWarningReasons);
            discordReviewReasons.TryGetValue(Normalize(player.Username), out var discordReviewReason);
            rows.Add(BuildPlayerRow(
                player,
                wom,
                pendingPromotionTarget,
                approvedPromotion,
                finalTrackerRank,
                finalWomRank,
                holdWarningReasons,
                discordReviewReason,
                frozen.LegacyToCanonical,
                frozen.DiscordRoleRenames));
        }

        // Preserve WOM-only observations as explicit holds. A role with no
        // stored player identity cannot safely become an in-game instruction.
        foreach (var wom in manifest.WomObservations)
        {
            if (playersByUsername.ContainsKey(Normalize(wom.Username))) continue;
            rows.Add(BuildWomOnlyRow(wom));
        }

        return new RankTransitionMemberReportDocument(
            rows,
            manifest.ManifestId,
            manifest.CreatedAtUtc,
            manifest.CutoverApprovalPolicyVersion,
            manifest.ManualWomChecks,
            manifest.FrozenMapping.LegacyToCanonical,
            manifest.FrozenMapping.DiscordRoleRenames);
    }

    public static string CsvPathFor(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var fullPath = Path.GetFullPath(manifestPath);
        var directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var fileName = Path.GetFileNameWithoutExtension(fullPath);
        return Path.Combine(directory, $"{fileName}.members.csv");
    }

    /// <summary>Writes the sidecar report beside the supplied manifest.</summary>
    public static async Task<string> WriteCsvAsync(
        RankTransitionManifest manifest,
        string manifestPath,
        CancellationToken ct = default)
    {
        return await WriteCsvAsync(Build(manifest), manifestPath, ct);
    }

    public static async Task<string> WriteCsvAsync(
        RankTransitionMemberReportDocument report,
        string manifestPath,
        CancellationToken ct = default)
    {
        var path = CsvPathFor(manifestPath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(report.ToCsv().AsMemory(), ct);
        await writer.FlushAsync(ct);
        return path;
    }

    public static string ToCsv(RankTransitionMemberReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.Append(string.Join(',', CsvHeaders)).Append("\r\n");
        foreach (var row in report.Rows)
        {
            string?[] fields =
            [
                row.PlayerId?.ToString(CultureInfo.InvariantCulture),
                row.Username,
                row.Status,
                row.TrackerRankBefore,
                row.TrackerRankAfter,
                row.WomRoleBefore,
                row.WomRoleAfter,
                row.InGameCutoverTarget,
                row.CalculatedEligibleRank,
                row.PendingPromotionTarget,
                row.ApprovedPromotionTarget,
                row.ApprovalState,
                row.DiscordReviewReason,
                row.Action,
                row.ReviewReason,
                row.FrozenSourceRole
            ];
            builder.Append(string.Join(',', fields.Select(EscapeCsv))).Append("\r\n");
        }

        return builder.ToString();
    }

    public static string ToConsoleSummary(RankTransitionMemberReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return $"Member cutover report: {report.Rows.Count} rows; " +
               $"{report.ManualCutoverReviewCount} manual cutover review, " +
               $"{report.ApprovedCutoverCount} approved for cutover, " +
               $"{report.PendingApprovalCount} pending approval, " +
               $"{report.HoldCount} holds, " +
               $"{report.InactiveNoChangeCount} inactive/no-change, " +
               $"{report.NoChangeCount} already aligned, " +
               $"{report.WomOnlyHoldCount} WOM-only holds.";
    }

    public static string MarkdownPathFor(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var fullPath = Path.GetFullPath(manifestPath);
        var directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var fileName = Path.GetFileNameWithoutExtension(fullPath);
        return Path.Combine(directory, $"{fileName}.members.md");
    }

    public static async Task<string> WriteMarkdownAsync(
        RankTransitionManifest manifest,
        string manifestPath,
        CancellationToken ct = default)
    {
        return await WriteMarkdownAsync(Build(manifest), manifestPath, ct);
    }

    public static async Task<string> WriteMarkdownAsync(
        RankTransitionMemberReportDocument report,
        string manifestPath,
        CancellationToken ct = default)
    {
        var path = MarkdownPathFor(manifestPath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(ToMarkdown(report).AsMemory(), ct);
        await writer.FlushAsync(ct);
        return path;
    }

    public static string ToMarkdown(RankTransitionMemberReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# Member cutover checklist");
        builder.AppendLine();
        builder.Append("Manifest: `")
            .Append(MarkdownCell(report.ManifestId))
            .AppendLine("`");
        builder.Append("Pre-cutover observation (UTC): `")
            .Append(MarkdownCell(report.CreatedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)))
            .AppendLine("`");
        builder.Append("Cutover policy: `")
            .Append(MarkdownCell(report.CutoverApprovalPolicyVersion))
            .AppendLine("`");
        builder.AppendLine();
        builder.AppendLine("`✗` means the optional value was absent from the frozen manifest. Current tracker / WOM shows the observed tracker rank first and observed WOM role second; the actual in-game role still requires manual verification. Included promotion is a frozen planned target, not a live approval; the complete row-level report remains in the CSV sidecar.");
        builder.AppendLine();
        builder.AppendLine("## In-game cutover checklist");
        var actionableRows = report.Rows
            .Where(x => !string.IsNullOrWhiteSpace(x.InGameCutoverTarget))
            .OrderBy(x => x.Username, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (actionableRows.Length == 0)
        {
            builder.AppendLine("_No in-game cutover rows are included in this frozen manifest._");
        }
        else
        {
            builder.AppendLine("| Member | Current tracker / WOM | Final rank | Included promotion |");
            builder.AppendLine("| --- | --- | --- | --- |");
            foreach (var row in actionableRows)
            {
                builder.Append("| ")
                    .Append(MarkdownCell(row.Username)).Append(" | ")
                    .Append(MarkdownCurrentRole(row)).Append(" | ")
                    .Append(MarkdownCell(row.InGameCutoverTarget)).Append(" | ")
                    .Append(MarkdownCell(row.ApprovedPromotionTarget)).AppendLine(" |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Review summary");
        builder.AppendLine($"- Rows: {report.Rows.Count}");
        builder.AppendLine($"- Included in-game cutover checklist: {actionableRows.Length}");
        builder.AppendLine($"- Approved for cutover: {report.ApprovedCutoverCount}");
        builder.AppendLine($"- Pending approval (not included): {report.PendingApprovalCount}");
        builder.AppendLine($"- Holds: {report.HoldCount}");
        builder.AppendLine($"- Inactive/no-change: {report.InactiveNoChangeCount}");
        builder.AppendLine($"- Already aligned/no change: {report.NoChangeCount}");
        builder.AppendLine($"- WOM-only holds: {report.WomOnlyHoldCount}");

        var reviewRows = report.Rows
            .Where(x => x.Action is HoldAction or InactiveNoChangeAction or PendingApprovalAction ||
                        !string.IsNullOrWhiteSpace(x.DiscordReviewReason))
            .OrderBy(x => x.Username, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (reviewRows.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("### Holds and separate review");
            builder.AppendLine("| Member | Status | Action | Final rank | Review detail |");
            builder.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var row in reviewRows)
            {
                var detail = string.IsNullOrWhiteSpace(row.DiscordReviewReason)
                    ? row.ReviewReason
                    : $"{row.ReviewReason} Discord review: {row.DiscordReviewReason}";
                builder.Append("| ")
                    .Append(MarkdownCell(row.Username)).Append(" | ")
                    .Append(MarkdownCell(row.Status)).Append(" | ")
                    .Append(MarkdownCell(row.Action)).Append(" | ")
                    .Append(MarkdownCell(row.TrackerRankAfter)).Append(" | ")
                    .Append(MarkdownCell(detail)).AppendLine(" |");
            }
        }

        var legacyHoldRows = report.Rows
            .Where(x => IsLegacyHoldReviewRow(report, x))
            .OrderBy(x => x.Username, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (legacyHoldRows.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("### Legacy tier-renames held for review");
            builder.AppendLine("The frozen WOM and tracker pairs below are review evidence only; they do not authorize guessing the actual in-game role. Verify the authoritative game roster before any manual change.");
            builder.AppendLine("| Member | WOM before → expected after | Tracker before → tracker after | Hold reason |");
            builder.AppendLine("| --- | --- | --- | --- |");
            foreach (var row in legacyHoldRows)
            {
                builder.Append("| ")
                    .Append(MarkdownCell(row.Username)).Append(" | ")
                    .Append(MarkdownCell(row.WomRoleBefore)).Append(" → ")
                    .Append(MarkdownCell(row.WomRoleAfter)).Append(" | ")
                    .Append(MarkdownCell(row.TrackerRankBefore)).Append(" → ")
                    .Append(MarkdownCell(row.TrackerRankAfter)).Append(" | ")
                    .Append(MarkdownCell(row.ReviewReason)).AppendLine(" |");
            }
        }

        return builder.ToString();
    }

    private static RankTransitionMemberReportRow BuildPlayerRow(
        RankTransitionPlayerObservation player,
        RankTransitionWomObservation? wom,
        string? pendingPromotionTarget,
        PromotionDecision? approvedPromotion,
        string? finalTrackerRank,
        string? finalWomRank,
        IReadOnlyList<string>? holdWarningReasons,
        string? discordReviewReason,
        IReadOnlyDictionary<string, string> frozenLegacyMappings,
        IReadOnlyDictionary<string, string> frozenWomMappings)
    {
        var trackerBefore = Display(player.DatabaseCurrentRankBefore);
        var baselineTrackerAfter = Display(player.DatabaseCurrentRankTarget);
        var trackerAfter = Display(finalTrackerRank) ?? baselineTrackerAfter;
        var womBefore = Display(wom?.RoleBefore);
        var womAfter = Display(finalWomRank) ?? Display(wom?.RoleTarget);
        var calculatedEligible = Display(player.EvaluatedEligibleRank);
        var hasMappedTrackerBefore = TryFrozenMapping(frozenLegacyMappings, trackerBefore, out var mappedTrackerBefore);
        var hasMappedWomBefore = TryFrozenMapping(frozenWomMappings, womBefore, out var mappedWomBefore);
        var isFrozenLegacySource = hasMappedWomBefore;
        var legacyRenameRequired = isFrozenLegacySource && !RanksEqual(womBefore, womAfter);
        var isOrdinaryLegacyEquivalentRename =
            hasMappedTrackerBefore &&
            hasMappedWomBefore &&
            !string.IsNullOrWhiteSpace(womAfter) &&
            legacyRenameRequired &&
            RanksEqual(mappedTrackerBefore, womAfter) &&
            RanksEqual(mappedWomBefore, womAfter);
        var reasons = new List<string>();
        if (holdWarningReasons is not null) reasons.AddRange(holdWarningReasons);
        if (!string.IsNullOrWhiteSpace(player.ProtectionReason)) reasons.Add(player.ProtectionReason);
        if (wom?.Notes is not null)
        {
            reasons.Add(isOrdinaryLegacyEquivalentRename && wom.Protected
                ? "Eligibility is below the frozen legacy standing; retain the authorized ordinary tier-equivalent rename and verify the actual game roster."
                : wom.Notes);
        }

        var source = SourceRole("WOM", womBefore) ?? SourceRole("DB", trackerBefore);
        var action = HoldAction;
        string? inGameTarget = null;
        var pendingTarget = pendingPromotionTarget;
        var approvedTarget = approvedPromotion is null
            ? null
            : Display(finalTrackerRank) ?? Display(approvedPromotion.EvaluatedEligibleRank);
        var approvalState = approvedPromotion is not null
            ? ApprovedForCutoverState
            : pendingPromotionTarget is not null
                ? PendingApprovalState
                : null;
        var standingForComparison = hasMappedTrackerBefore
            ? mappedTrackerBefore
            : IsKnownRank(trackerBefore) ? trackerBefore : baselineTrackerAfter;

        if (approvedTarget is not null &&
            CompareRanks(approvedTarget, trackerAfter) >= 0 &&
            CompareRanks(approvedTarget, standingForComparison) > 0)
        {
            trackerAfter = approvedTarget;
        }

        if (IsIdentityStatus(player.Status))
        {
            reasons.Add("Player status requires identity/review handling; no in-game alignment is suggested.");
        }
        else if (!player.IsActive || IsRemovedStatus(player.Status))
        {
            action = InactiveNoChangeAction;
            reasons.Add("Inactive stored player; make no in-game rank change during this cutover.");
        }
        else if (player.Protected)
        {
            reasons.Add("Database standing/eligibility is protected; manual review is required.");
        }
        else if (holdWarningReasons is not null)
        {
            reasons.Add("A manifest review warning is open; no in-game alignment is suggested.");
        }
        else if (wom?.IgnoredMismatch == true)
        {
            reasons.Add("Ignored mismatch is preserved; equivalent external renames remain independent, but no forced in-game alignment is suggested.");
        }
        else if (wom is null || !wom.FreshObservation)
        {
            reasons.Add("Fresh WOM role observation is unavailable; hold for review.");
        }
        else if ((wom.Protected && !isOrdinaryLegacyEquivalentRename) || string.IsNullOrWhiteSpace(womBefore))
        {
            reasons.Add("WOM role is special, protected, or missing; do not force an in-game target.");
        }
        else if (approvedPromotion is not null && approvedTarget is not null)
        {
            if (CompareRanks(approvedTarget, standingForComparison) <= 0)
            {
                reasons.Add($"Approved target '{approvedTarget}' does not exceed the tracker standing; no downgrade or lower in-game target is suggested.");
            }
            else
            {
                action = CutoverReviewAction;
                inGameTarget = approvedTarget;
                pendingTarget = null;
                reasons.Add($"Approved for cutover to final rank '{approvedTarget}'. This is a planned cutover target, not a live approval.");
            }
        }
        else if (string.IsNullOrWhiteSpace(womAfter) || !IsKnownRank(womAfter))
        {
            reasons.Add("WOM target is not a known assignable clan rank; hold for review.");
        }
        else
        {
            var comparisonToTracker = CompareRanks(womAfter, trackerAfter);
            if (comparisonToTracker < 0)
            {
                reasons.Add("The mapped WOM target is below the tracker standing; no forced lower in-game target is suggested.");
            }
            else if (pendingPromotionTarget is not null && legacyRenameRequired)
            {
                // A real upgrade and the frozen baseline cutover can coexist.
                // Retire the legacy WOM/game label first while keeping the
                // earned upgrade as a separate approval item.
                action = CutoverReviewAction;
                inGameTarget = womAfter;
                reasons.Add($"Manual in-game baseline target from the frozen WOM role; promotion target '{pendingPromotionTarget}' remains separately pending approval.");
            }
            else if (pendingPromotionTarget is not null)
            {
                action = PendingApprovalAction;
                reasons.Add($"Promotion target '{pendingPromotionTarget}' remains in the approval queue; do not include it in automatic game cutover.");
            }
            else if (!isFrozenLegacySource && !RanksEqual(womAfter, trackerAfter))
            {
                if (comparisonToTracker > 0)
                {
                    action = PendingApprovalAction;
                    pendingTarget = null;
                    reasons.Add($"Canonical WOM rank '{womAfter}' is above the tracker standing; approval is required before any in-game upgrade.");
                }
                else
                {
                    reasons.Add("Tracker and canonical WOM standing do not match; hold for identity/review rather than forcing alignment.");
                }
            }
            else if (legacyRenameRequired)
            {
                action = CutoverReviewAction;
                inGameTarget = womAfter;
                reasons.Add("Manual in-game equivalent target from the frozen WOM role; verify the actual game roster before changing it.");
            }
            else
            {
                action = NoChangeAction;
                reasons.Add("The active player already has the frozen equivalent role; no in-game change is suggested.");
            }
        }

        return new RankTransitionMemberReportRow(
            player.PlayerId,
            player.Username,
            player.Status,
            trackerBefore,
            trackerAfter,
            womBefore,
            womAfter,
            inGameTarget,
            calculatedEligible,
            pendingTarget,
            action,
            JoinReasons(reasons),
            source,
            approvedTarget,
            approvalState,
            discordReviewReason);
    }

    private static RankTransitionMemberReportRow BuildWomOnlyRow(RankTransitionWomObservation wom)
    {
        var womBefore = Display(wom.RoleBefore);
        var womAfter = Display(wom.RoleTarget);
        var reason = wom.Notes ?? "WOM-only observation; no player identity was inferred.";
        if (wom.Protected) reason = $"{reason} No in-game target is suggested for a protected/special role.";
        else reason = $"{reason} Resolve the identity before any game change.";

        return new RankTransitionMemberReportRow(
            null,
            wom.Username,
            WomOnlyStatus,
            null,
            null,
            womBefore,
            womAfter,
            null,
            null,
            null,
            HoldAction,
            reason,
            SourceRole("WOM", womBefore),
            null,
            null,
            null);
    }

    private static Dictionary<int, string> ReadPendingPromotions(RankTransitionManifest manifest)
    {
        var pending = manifest.PromotionDecisions
            .Where(x => x.IsActive && x.QueuedThroughApprovalFlow && !x.ApprovedForCutover && !string.IsNullOrWhiteSpace(x.EvaluatedEligibleRank))
            .GroupBy(x => x.PlayerId)
            .ToDictionary(x => x.Key, x => Display(x.Last().EvaluatedEligibleRank)!);
        var approved = manifest.PromotionDecisions
            .Where(x => x.IsActive && x.ApprovedForCutover)
            .Select(x => x.PlayerId)
            .ToHashSet();

        foreach (var operation in manifest.Operations.Where(x => x.Kind == TransitionOperationKind.QueuePromotionCandidate))
        {
            if (!int.TryParse(operation.TargetKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var playerId) ||
                string.IsNullOrWhiteSpace(operation.After) ||
                approved.Contains(playerId) ||
                pending.ContainsKey(playerId)) continue;
            pending[playerId] = Display(operation.After)!;
        }

        return pending;
    }

    private static Dictionary<int, PromotionDecision> ReadApprovedPromotions(RankTransitionManifest manifest)
    {
        return manifest.PromotionDecisions
            .Where(x => x.IsActive && x.ApprovedForCutover && !string.IsNullOrWhiteSpace(x.EvaluatedEligibleRank))
            .GroupBy(x => x.PlayerId)
            .ToDictionary(x => x.Key, x => x.Last());
    }

    private static Dictionary<int, string> ReadFinalTrackerRanks(RankTransitionManifest manifest)
    {
        return manifest.Operations
            .Where(x => (x.Kind is TransitionOperationKind.DatabaseCurrentRank or TransitionOperationKind.FinalizeApprovedPromotion) && int.TryParse(x.TargetKey, out _))
            .GroupBy(x => int.Parse(x.TargetKey, NumberStyles.Integer, CultureInfo.InvariantCulture))
            .Where(x => !string.IsNullOrWhiteSpace(x.Last().After))
            .ToDictionary(x => x.Key, x => Display(x.Last().After)!);
    }

    private static Dictionary<string, string> ReadFinalWomRanks(RankTransitionManifest manifest)
    {
        return manifest.Operations
            .Where(x => x.Kind == TransitionOperationKind.WiseOldManRole && !string.IsNullOrWhiteSpace(x.After))
            .GroupBy(x => Normalize(x.TargetKey), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => Display(x.Last().After)!, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, IReadOnlyList<string>> ReadHoldWarningReasons(RankTransitionManifest manifest)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var warning in manifest.Warnings.Where(x => IsHoldWarning(x.Code) && !IsDiscordWarning(x.Code)))
        {
            var subject = Normalize(warning.Subject);
            if (string.IsNullOrWhiteSpace(subject)) continue;
            if (result.TryGetValue(subject, out var existing))
            {
                result[subject] = existing.Concat([warning.Details]).ToArray();
            }
            else
            {
                result[subject] = [warning.Details];
            }
        }

        return result;
    }

    private static Dictionary<string, string> ReadDiscordReviewReasons(RankTransitionManifest manifest)
    {
        return manifest.Warnings
            .Where(x => IsDiscordWarning(x.Code))
            .GroupBy(x => Normalize(x.Subject), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => JoinReasons(x.Select(y => y.Details)), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsHoldWarning(string code) =>
        code.Contains("IDENTITY", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("MERGE", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("CONTAMINATION", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("REVIEW_SUPPRESSED", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("NAME_CHANGE", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("TEMPLE", StringComparison.OrdinalIgnoreCase) ||
        code.Equals("STALE_PLAYER_SNAPSHOT", StringComparison.OrdinalIgnoreCase) ||
        code.Equals("NO_PLAYER_SNAPSHOT", StringComparison.OrdinalIgnoreCase);

    private static bool IsDiscordWarning(string code) =>
        code.StartsWith("DISCORD", StringComparison.OrdinalIgnoreCase) ||
        code.StartsWith("CUTOVER_MEMBER", StringComparison.OrdinalIgnoreCase);

    private static bool IsIdentityStatus(string status) =>
        status.Equals("NEW_PENDING_REVIEW", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("MISSING_PENDING_REVIEW", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("MERGE_SUGGESTED", StringComparison.OrdinalIgnoreCase);

    private static bool IsRemovedStatus(string status) =>
        status.Equals("REMOVED_CONFIRMED", StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacyHoldReviewRow(
        RankTransitionMemberReportDocument report,
        RankTransitionMemberReportRow row) =>
        row.Action == HoldAction &&
        row.PlayerId is not null &&
        row.FrozenSourceRole?.StartsWith("WOM:", StringComparison.OrdinalIgnoreCase) == true &&
        !string.IsNullOrWhiteSpace(row.WomRoleBefore) &&
        !string.IsNullOrWhiteSpace(row.WomRoleAfter) &&
        !RanksEqual(row.WomRoleBefore, row.WomRoleAfter) &&
        TryFrozenMapping(report.FrozenWomMappings, row.WomRoleBefore, out var mappedWomBefore) &&
        RanksEqual(mappedWomBefore, row.WomRoleAfter) &&
        row.ReviewReason.Contains("Ignored mismatch", StringComparison.OrdinalIgnoreCase) &&
        report.ManualWomChecks.Any(check =>
            RanksEqual(check.Username, row.Username) &&
            RanksEqual(check.RoleBefore, row.WomRoleBefore) &&
            RanksEqual(check.RoleAfter, row.WomRoleAfter));

    private static bool TryFrozenMapping(
        IReadOnlyDictionary<string, string> mappings,
        string? source,
        out string target)
    {
        target = "";
        if (string.IsNullOrWhiteSpace(source)) return false;
        foreach (var mapping in mappings)
        {
            if (!RanksEqual(mapping.Key, source)) continue;
            target = Display(mapping.Value) ?? "";
            return true;
        }

        return false;
    }

    private static string? SourceRole(string system, string? role) =>
        string.IsNullOrWhiteSpace(role) ? null : $"{system}:{role}";

    private static int CompareRanks(string? left, string? right)
    {
        var leftOrder = RankOrder(left);
        var rightOrder = RankOrder(right);
        if (leftOrder < 0 || rightOrder < 0) return 0;
        return leftOrder.CompareTo(rightOrder);
    }

    private static int RankOrder(string? rank)
    {
        if (string.IsNullOrWhiteSpace(rank)) return -1;
        var index = Array.FindIndex(OrderedRanks, x => RanksEqual(x, rank));
        return index;
    }

    private static bool IsKnownRank(string? rank) => RankOrder(rank) >= 0;

    private static bool RanksEqual(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string? Display(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().Replace('_', ' ');
    }

    private static string Normalize(string? value) => Display(value) ?? "";

    private static string JoinReasons(IEnumerable<string> reasons) =>
        string.Join(" ", reasons
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal));

    private static string MarkdownCell(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "✗";
        return value
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    private static string MarkdownCurrentRole(RankTransitionMemberReportRow row) =>
        $"{MarkdownCell(row.TrackerRankBefore)} / {MarkdownCell(row.WomRoleBefore)}";

    private static string EscapeCsv(string? value)
    {
        var text = value ?? "";
        return $"\"{text.Replace("\"", "\"\"")}\"";
    }
}
