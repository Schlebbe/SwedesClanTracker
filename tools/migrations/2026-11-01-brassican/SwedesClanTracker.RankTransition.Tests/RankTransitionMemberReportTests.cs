using System.Text;
using SwedesClanTracker.Core;
using SwedesClanTracker.RankTransition;

namespace SwedesClanTracker.RankTransition.Tests;

public sealed class RankTransitionMemberReportTests
{
    [Fact]
    public void Build_uses_frozen_source_provenance_without_double_mapping_general()
    {
        var manifest = CreateManifest(
            new RankTransitionPlayerObservation(
                1,
                "AstralMember",
                "ACTIVE",
                true,
                "Astral",
                "General",
                "Astral",
                "General",
                101,
                "snapshot",
                null,
                null,
                "General",
                "General via: collections",
                false,
                null),
            new RankTransitionWomObservation(
                "AstralMember",
                "General",
                "Colonel",
                true,
                false,
                false,
                null));

        var row = Assert.Single(RankTransitionMemberReport.Build(manifest).Rows);

        Assert.Equal("General", row.TrackerRankAfter);
        Assert.Equal("General", row.WomRoleBefore);
        Assert.Equal("Colonel", row.WomRoleAfter);
        Assert.Equal("Colonel", row.InGameCutoverTarget);
        Assert.Equal("WOM:General", row.FrozenSourceRole);
        Assert.Equal(RankTransitionMemberReport.CutoverReviewAction, row.Action);
        Assert.DoesNotContain("WOM:Colonel", row.ReviewReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_keeps_real_upgrade_pending_and_never_suggests_commander_game_target()
    {
        var manifest = CreateManifest(
            new RankTransitionPlayerObservation(
                2,
                "UpgradeMember",
                "ACTIVE",
                true,
                "Officer",
                "Brassican",
                "Officer",
                "Commander",
                202,
                "snapshot",
                null,
                null,
                "Commander",
                "Commander via: total level",
                false,
                null),
            new RankTransitionWomObservation(
                "UpgradeMember",
                "Officer",
                "Brassican",
                true,
                false,
                false,
                null),
            new PromotionDecision(
                2,
                "UpgradeMember",
                "Brassican",
                "Commander",
                "Officer",
                true,
                true,
                "Total level >= 2200"));

        var report = RankTransitionMemberReport.Build(manifest);
        var row = Assert.Single(report.Rows);

        Assert.Equal(RankTransitionMemberReport.CutoverReviewAction, row.Action);
        Assert.Equal("Commander", row.PendingPromotionTarget);
        Assert.Equal("Brassican", row.InGameCutoverTarget);
        Assert.DoesNotContain("Commander", row.InGameCutoverTarget, StringComparison.Ordinal);
        Assert.Contains("separately pending approval", row.ReviewReason, StringComparison.Ordinal);
        Assert.Equal(1, report.PendingApprovalCount);
    }

    [Fact]
    public void Build_projects_approved_officer_upgrade_as_one_direct_final_cutover_rank()
    {
        var manifest = CreateManifest(
            new RankTransitionPlayerObservation(
                20,
                "Arvld",
                "ACTIVE",
                true,
                "Officer",
                "Commander",
                "Officer",
                "Commander",
                2020,
                "snapshot",
                null,
                null,
                "Commander",
                "Commander via: total level",
                false,
                null),
            new RankTransitionWomObservation(
                "Arvld",
                "Officer",
                "Commander",
                true,
                false,
                false,
                null),
            new PromotionDecision(
                20,
                "Arvld",
                "Brassican",
                "Commander",
                "Commander",
                true,
                false,
                "Total level >= 2200",
                ApprovedForCutover: true),
            Operation(TransitionOperationKind.FinalizeApprovedPromotion, "20", "Officer", "Commander"),
            Operation(TransitionOperationKind.WiseOldManRole, "Arvld", "Officer", "Commander"));

        var row = Assert.Single(RankTransitionMemberReport.Build(manifest).Rows);

        Assert.Equal("Officer", row.TrackerRankBefore);
        Assert.Equal("Commander", row.TrackerRankAfter);
        Assert.Equal("Commander", row.WomRoleAfter);
        Assert.Equal("Commander", row.InGameCutoverTarget);
        Assert.Equal("Commander", row.ApprovedPromotionTarget);
        Assert.Equal(RankTransitionMemberReport.ApprovedForCutoverState, row.ApprovalState);
        Assert.Null(row.PendingPromotionTarget);
        Assert.Equal(RankTransitionMemberReport.CutoverReviewAction, row.Action);
        Assert.Contains("planned cutover target", row.ReviewReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_projects_approved_legacy_general_upgrade_directly_to_brigadier()
    {
        var manifest = CreateManifest(
            new RankTransitionPlayerObservation(
                21,
                "LegacyGeneral",
                "ACTIVE",
                true,
                "General",
                "Colonel",
                "General",
                "Brigadier",
                2121,
                "snapshot",
                null,
                null,
                "Brigadier",
                "Brigadier via: EHB",
                false,
                null),
            new RankTransitionWomObservation(
                "LegacyGeneral",
                "General",
                "Colonel",
                true,
                false,
                false,
                null),
            new PromotionDecision(
                21,
                "LegacyGeneral",
                "Colonel",
                "Brigadier",
                "General",
                true,
                false,
                "EHB >= 2000",
                ApprovedForCutover: true),
            Operation(TransitionOperationKind.FinalizeApprovedPromotion, "21", "General", "Brigadier"),
            Operation(TransitionOperationKind.WiseOldManRole, "LegacyGeneral", "General", "Brigadier"));

        var row = Assert.Single(RankTransitionMemberReport.Build(manifest).Rows);

        Assert.Equal("Brigadier", row.TrackerRankAfter);
        Assert.Equal("Brigadier", row.WomRoleAfter);
        Assert.Equal("Brigadier", row.InGameCutoverTarget);
        Assert.Equal("Brigadier", row.ApprovedPromotionTarget);
        Assert.Equal(RankTransitionMemberReport.ApprovedForCutoverState, row.ApprovalState);
        Assert.Null(row.PendingPromotionTarget);
    }

    [Fact]
    public void Build_preserves_protected_floor_when_approved_target_would_be_a_downgrade()
    {
        var manifest = CreateManifest(
            new RankTransitionPlayerObservation(
                22,
                "ProtectedFloor",
                "ACTIVE",
                true,
                "Colonel",
                "Colonel",
                "Colonel",
                "Commander",
                null,
                null,
                null,
                null,
                "Commander",
                "Commander via: total level",
                true,
                "Database standing/eligibility is protected."),
            new RankTransitionWomObservation(
                "ProtectedFloor",
                "Colonel",
                "Colonel",
                true,
                false,
                false,
                null),
            new PromotionDecision(
                22,
                "ProtectedFloor",
                "Colonel",
                "Commander",
                "Colonel",
                true,
                false,
                "approved fixture",
                ApprovedForCutover: true));

        var row = Assert.Single(RankTransitionMemberReport.Build(manifest).Rows);

        Assert.Equal("Colonel", row.TrackerRankAfter);
        Assert.Equal("Commander", row.ApprovedPromotionTarget);
        Assert.Null(row.InGameCutoverTarget);
        Assert.Equal(RankTransitionMemberReport.HoldAction, row.Action);
        Assert.Contains("protected", row.ReviewReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_allows_grandfathered_legacy_tier_renames_below_eligibility_but_keeps_ignored_hold()
    {
        var report = RankTransitionMemberReport.Build(CreateManifest(
            new RankTransitionPlayerObservation(
                24,
                "Aqtony",
                "ACTIVE",
                true,
                "General",
                "Colonel",
                "General",
                "General",
                null,
                null,
                null,
                null,
                "General",
                "General via: legacy standing",
                false,
                null),
            new RankTransitionWomObservation(
                "Aqtony",
                "General",
                "Colonel",
                true,
                true,
                false,
                "Mapped WOM standing 'Colonel' exceeds evaluated eligibility 'General'; hold promotion and in-game alignment for review."),
            new RankTransitionPlayerObservation(
                25,
                "Heezt",
                "ACTIVE",
                true,
                "Astral",
                "General",
                "Captain",
                "Captain",
                null,
                null,
                null,
                null,
                "Captain",
                "Captain via: legacy standing",
                false,
                null),
            new RankTransitionWomObservation(
                "Heezt",
                "Astral",
                "General",
                true,
                true,
                false,
                "Mapped WOM standing 'General' exceeds evaluated eligibility 'Captain'; hold promotion and in-game alignment for review."),
            new RankTransitionPlayerObservation(
                26,
                "Alkolistt",
                "ACTIVE",
                true,
                "Astral",
                "General",
                "Captain",
                "Captain",
                null,
                null,
                null,
                null,
                "Captain",
                "Captain via: ignored mismatch fixture",
                false,
                null),
            new RankTransitionWomObservation(
                "Alkolistt",
                "General",
                "Colonel",
                true,
                true,
                true,
                "Open ignored mismatch preserved.")));

        var aqtony = report.Rows.Single(x => x.Username == "Aqtony");
        Assert.Equal(RankTransitionMemberReport.CutoverReviewAction, aqtony.Action);
        Assert.Equal("Colonel", aqtony.InGameCutoverTarget);
        Assert.Contains("tier-equivalent rename", aqtony.ReviewReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no in-game alignment", aqtony.ReviewReason, StringComparison.OrdinalIgnoreCase);

        var heezt = report.Rows.Single(x => x.Username == "Heezt");
        Assert.Equal(RankTransitionMemberReport.CutoverReviewAction, heezt.Action);
        Assert.Equal("General", heezt.InGameCutoverTarget);
        Assert.Contains("tier-equivalent rename", heezt.ReviewReason, StringComparison.OrdinalIgnoreCase);

        var ignored = report.Rows.Single(x => x.Username == "Alkolistt");
        Assert.Equal(RankTransitionMemberReport.HoldAction, ignored.Action);
        Assert.Null(ignored.InGameCutoverTarget);
        Assert.Contains("Ignored mismatch", ignored.ReviewReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_keeps_approved_game_target_visible_when_discord_identity_needs_separate_review()
    {
        var manifest = CreateManifest(
            new RankTransitionPlayerObservation(
                23,
                "DiscordReview",
                "ACTIVE",
                true,
                "Officer",
                "Brassican",
                "Officer",
                "Commander",
                null,
                null,
                null,
                null,
                "Commander",
                "Commander via: total level",
                false,
                null),
            new RankTransitionWomObservation(
                "DiscordReview",
                "Officer",
                "Brassican",
                true,
                false,
                false,
                null),
            new PromotionDecision(
                23,
                "DiscordReview",
                "Brassican",
                "Commander",
                "Officer",
                true,
                false,
                "approved fixture",
                ApprovedForCutover: true),
            new TransitionWarning(
                "DISCORD_IDENTITY_MISSING",
                "DiscordReview",
                "Discord member identity requires separate review.",
                BlocksApply: false),
            new TransitionWarning(
                "CUTOVER_MEMBER_PLAN_MISSING",
                "DiscordReview",
                "Discord member role plan requires separate review.",
                BlocksApply: true));

        var row = Assert.Single(RankTransitionMemberReport.Build(manifest).Rows);

        Assert.Equal("Commander", row.InGameCutoverTarget);
        Assert.Equal(RankTransitionMemberReport.CutoverReviewAction, row.Action);
        Assert.Contains("Discord member identity requires separate review.", row.DiscordReviewReason, StringComparison.Ordinal);
        Assert.Contains("Discord member role plan requires separate review.", row.DiscordReviewReason, StringComparison.Ordinal);
        Assert.DoesNotContain("manifest review warning", row.ReviewReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RankTransitionMemberReport.ApprovedForCutoverState, row.ApprovalState);
    }

    [Fact]
    public void Build_marks_an_unchanged_canonical_captain_as_no_change()
    {
        var manifest = CreateManifest(
            new RankTransitionPlayerObservation(
                8,
                "CaptainMember",
                "ACTIVE",
                true,
                "Captain",
                "Captain",
                "Captain",
                "Captain",
                808,
                "snapshot",
                null,
                null,
                "Captain",
                "Captain via: collections",
                false,
                null),
            new RankTransitionWomObservation(
                "CaptainMember",
                "Captain",
                "Captain",
                true,
                false,
                false,
                null));

        var row = Assert.Single(RankTransitionMemberReport.Build(manifest).Rows);

        Assert.Equal(RankTransitionMemberReport.NoChangeAction, row.Action);
        Assert.Null(row.InGameCutoverTarget);
        Assert.Contains("already has", row.ReviewReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_keeps_an_unchanged_captain_baseline_without_game_target_when_general_is_pending()
    {
        var manifest = CreateManifest(
            new RankTransitionPlayerObservation(
                9,
                "CaptainUpgradeMember",
                "ACTIVE",
                true,
                "Captain",
                "Captain",
                "Captain",
                "General",
                909,
                "snapshot",
                null,
                null,
                "General",
                "General via: collections",
                false,
                null),
            new RankTransitionWomObservation(
                "CaptainUpgradeMember",
                "Captain",
                "Captain",
                true,
                false,
                false,
                null),
            new PromotionDecision(
                9,
                "CaptainUpgradeMember",
                "Captain",
                "General",
                "Captain",
                true,
                true,
                "Collections >= 900"));

        var report = RankTransitionMemberReport.Build(manifest);
        var row = Assert.Single(report.Rows);

        Assert.Equal(RankTransitionMemberReport.PendingApprovalAction, row.Action);
        Assert.Equal("General", row.PendingPromotionTarget);
        Assert.Null(row.InGameCutoverTarget);
        Assert.Equal(1, report.PendingApprovalCount);
    }

    [Fact]
    public void Build_marks_protected_ignored_identity_and_inactive_rows_without_targets()
    {
        var manifest = CreateManifest(
            new RankTransitionPlayerObservation(
                3,
                "ProtectedMember",
                "ACTIVE",
                true,
                "Brassican",
                "Brassican",
                "Brassican",
                "Brassican",
                null,
                null,
                null,
                null,
                null,
                null,
                true,
                "Database rank requires manual review."),
            new RankTransitionWomObservation(
                "ProtectedMember",
                "Recruit",
                null,
                true,
                true,
                false,
                "Special WOM role is protected."),
            new RankTransitionPlayerObservation(
                4,
                "IgnoredMember",
                "ACTIVE",
                true,
                "Astral",
                "General",
                "Astral",
                "General",
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                null),
            new RankTransitionWomObservation(
                "IgnoredMember",
                "General",
                "Colonel",
                true,
                false,
                true,
                "Open ignored mismatch preserved."),
            new RankTransitionPlayerObservation(
                5,
                "InactiveMember",
                "INACTIVE",
                false,
                "Astral",
                "General",
                "General",
                "Colonel",
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                null),
            new RankTransitionWomObservation(
                "InactiveMember",
                "General",
                "Colonel",
                true,
                false,
                false,
                null),
            new RankTransitionPlayerObservation(
                6,
                "HigherTrackerMember",
                "ACTIVE",
                true,
                "General",
                "Colonel",
                "General",
                "Colonel",
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                null),
            new RankTransitionWomObservation(
                "HigherTrackerMember",
                "Officer",
                "Brassican",
                true,
                false,
                false,
                null),
            new RankTransitionPlayerObservation(
                7,
                "RemovedMember",
                "REMOVED_CONFIRMED",
                false,
                "Officer",
                "Brassican",
                "Officer",
                "Brassican",
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                null));

        var rows = RankTransitionMemberReport.Build(manifest).Rows;

        var protectedRow = rows.Single(x => x.Username == "ProtectedMember");
        Assert.Equal(RankTransitionMemberReport.HoldAction, protectedRow.Action);
        Assert.Null(protectedRow.InGameCutoverTarget);

        var ignoredRow = rows.Single(x => x.Username == "IgnoredMember");
        Assert.Equal(RankTransitionMemberReport.HoldAction, ignoredRow.Action);
        Assert.Equal("Colonel", ignoredRow.WomRoleAfter);
        Assert.Null(ignoredRow.InGameCutoverTarget);

        var inactiveRow = rows.Single(x => x.Username == "InactiveMember");
        Assert.Equal(RankTransitionMemberReport.InactiveNoChangeAction, inactiveRow.Action);
        Assert.Null(inactiveRow.InGameCutoverTarget);

        var lowerTargetRow = rows.Single(x => x.Username == "HigherTrackerMember");
        Assert.Equal(RankTransitionMemberReport.HoldAction, lowerTargetRow.Action);
        Assert.Null(lowerTargetRow.InGameCutoverTarget);
        Assert.Contains("no forced lower", lowerTargetRow.ReviewReason, StringComparison.Ordinal);

        var removedRow = rows.Single(x => x.Username == "RemovedMember");
        Assert.Equal(RankTransitionMemberReport.InactiveNoChangeAction, removedRow.Action);
        Assert.Null(removedRow.InGameCutoverTarget);
        Assert.Null(removedRow.WomRoleBefore);
    }

    [Fact]
    public void Build_includes_wom_only_identity_as_a_hold()
    {
        var manifest = CreateManifest(wom: new RankTransitionWomObservation(
            "WomOnly",
            "General",
            "Colonel",
            true,
            false,
            false,
            "WOM-only observation; no player identity was inferred."));

        var row = Assert.Single(RankTransitionMemberReport.Build(manifest).Rows);

        Assert.Null(row.PlayerId);
        Assert.Equal(RankTransitionMemberReport.WomOnlyStatus, row.Status);
        Assert.Equal(RankTransitionMemberReport.HoldAction, row.Action);
        Assert.Null(row.InGameCutoverTarget);
        Assert.Equal("WOM:General", row.FrozenSourceRole);
    }

    [Fact]
    public void Markdown_puts_actionable_checklist_before_compact_review_details()
    {
        var manifest = CreateManifest(
            new RankTransitionPlayerObservation(
                30,
                "ApprovedMember",
                "ACTIVE",
                true,
                "Astral",
                "General",
                "General",
                "Colonel",
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                null),
            new RankTransitionWomObservation(
                "ApprovedMember",
                "General",
                "Colonel",
                true,
                false,
                false,
                null),
            new RankTransitionPlayerObservation(
                31,
                "HoldMember",
                "ACTIVE",
                true,
                "Brassican",
                "Brassican",
                "Brassican",
                "Brassican",
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                null),
            new RankTransitionWomObservation(
                "HoldMember",
                "Recruit",
                null,
                true,
                true,
                false,
                "Protected special role."));

        var markdown = RankTransitionMemberReport.Build(manifest).ToMarkdown();
        var checklistIndex = markdown.IndexOf("## In-game cutover checklist", StringComparison.Ordinal);
        var summaryIndex = markdown.IndexOf("## Review summary", StringComparison.Ordinal);

        Assert.True(checklistIndex >= 0 && summaryIndex > checklistIndex);
        Assert.Contains("| ApprovedMember | Astral / General | Colonel | ✗ |", markdown[..summaryIndex], StringComparison.Ordinal);
        Assert.DoesNotContain("| HoldMember |", markdown[..summaryIndex], StringComparison.Ordinal);
        Assert.Contains("| HoldMember |", markdown[summaryIndex..], StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_adds_compact_legacy_hold_review_without_authorizing_a_game_guess()
    {
        var markdown = RankTransitionMemberReport.Build(CreateManifest(
            new RankTransitionPlayerObservation(
                32,
                "LegacyHold",
                "ACTIVE",
                true,
                "Astral",
                "General",
                "Captain",
                "Captain",
                null,
                null,
                null,
                null,
                "Captain",
                "Ignored mismatch fixture",
                false,
                null),
            new RankTransitionWomObservation(
                "LegacyHold",
                "General",
                "Colonel",
                true,
                false,
                true,
                "Open ignored mismatch preserved."),
            new RankTransitionManualWomCheck(
                RankTransitionPlanner.ComputeOperationId(
                    TransitionOperationKind.WiseOldManRole,
                    "LegacyHold",
                    "General",
                    "Colonel"),
                "LegacyHold",
                "General",
                "Colonel",
                true,
                "Open ignored mismatch preserved."))).ToMarkdown();

        Assert.Contains("### Legacy tier-renames held for review", markdown, StringComparison.Ordinal);
        Assert.Contains("| LegacyHold | General → Colonel | Astral → General |", markdown, StringComparison.Ordinal);
        Assert.Contains("do not authorize guessing the actual in-game role", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Csv_is_rfc4180_quoted_utf8_and_written_beside_manifest()
    {
        var manifest = CreateManifest(
            new RankTransitionPlayerObservation(
                6,
                "Åke, \"Ärlig\"",
                "ACTIVE",
                true,
                "Officer",
                "Brassican",
                "Officer",
                "Brassican",
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                null),
            new RankTransitionWomObservation(
                "Åke, \"Ärlig\"",
                "Officer",
                "Brassican",
                true,
                false,
                false,
                null));
        var report = RankTransitionMemberReport.Build(manifest);
        var csv = report.ToCsv();

        Assert.StartsWith("PlayerId,Username,Status,TrackerRankBefore,TrackerRankAfter,", csv, StringComparison.Ordinal);
        Assert.Contains("ApprovedPromotionTarget,ApprovalState,DiscordReviewReason,Action", csv, StringComparison.Ordinal);
        Assert.Contains("\"Åke, \"\"Ärlig\"\"\"", csv, StringComparison.Ordinal);
        Assert.Contains("\r\n", csv, StringComparison.Ordinal);
        Assert.Equal(1, report.ManualCutoverReviewCount);
        Assert.Contains("1 manual cutover review", report.ToConsoleSummary(), StringComparison.Ordinal);

        var directory = Path.Combine(Path.GetTempPath(), $"swedes-rank-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var manifestPath = Path.Combine(directory, "brassican-transition.json");
            var path = await RankTransitionMemberReport.WriteCsvAsync(manifest, manifestPath);
            var markdownPath = await RankTransitionMemberReport.WriteMarkdownAsync(manifest, manifestPath);

            Assert.Equal(Path.Combine(directory, "brassican-transition.members.csv"), path);
            Assert.Equal(Path.Combine(directory, "brassican-transition.members.md"), markdownPath);
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            Assert.Contains("Åke", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
            var markdown = await File.ReadAllTextAsync(markdownPath, Encoding.UTF8);
            Assert.Contains("Manifest:", markdown, StringComparison.Ordinal);
            Assert.Contains("Pre-cutover observation (UTC):", markdown, StringComparison.Ordinal);
            Assert.Contains("| Member | Current tracker / WOM | Final rank | Included promotion |", markdown, StringComparison.Ordinal);
            Assert.Contains("✗", markdown, StringComparison.Ordinal);
            Assert.Contains("Åke", markdown, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static RankTransitionOperation Operation(
        TransitionOperationKind kind,
        string target,
        string before,
        string after) =>
        new(
            RankTransitionPlanner.ComputeOperationId(kind, target, before, after),
            kind,
            target,
            kind == TransitionOperationKind.WiseOldManRole ? "Role" : nameof(Player.CurrentRank),
            before,
            after,
            IsExternal: kind is TransitionOperationKind.WiseOldManRole or TransitionOperationKind.DiscordRoleRename,
            IsActive: true,
            Protected: false,
            RequiresReview: true,
            SnapshotId: null,
            SnapshotHash: null,
            LastSyncedAtUtc: null,
            Reason: "report fixture");

    private static RankTransitionManifest CreateManifest(
        RankTransitionPlayerObservation? player = null,
        RankTransitionWomObservation? wom = null,
        PromotionDecision? promotion = null)
    {
        return new RankTransitionManifest
        {
            FrozenMapping = new FrozenRankMappingDocument(
                "legacy-rank-transition-v1",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Recruit"] = "Brassican",
                    ["Officer"] = "Brassican",
                    ["Astral"] = "General",
                    ["General"] = "Colonel"
                },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Officer"] = "Brassican",
                    ["Astral"] = "General",
                    ["General"] = "Colonel"
                }),
            Players = player is null ? [] : [player],
            WomObservations = wom is null ? [] : [wom],
            PromotionDecisions = promotion is null ? [] : [promotion]
        };
    }

    private static RankTransitionManifest CreateManifest(
        params object[] values)
    {
        var players = values.OfType<RankTransitionPlayerObservation>().ToArray();
        var wom = values.OfType<RankTransitionWomObservation>().ToArray();
        var promotions = values.OfType<PromotionDecision>().ToArray();
        var manualWomChecks = values.OfType<RankTransitionManualWomCheck>().ToArray();
        var operations = values.OfType<RankTransitionOperation>().ToArray();
        var warnings = values.OfType<TransitionWarning>().ToArray();
        return new RankTransitionManifest
        {
            FrozenMapping = new FrozenRankMappingDocument(
                "legacy-rank-transition-v1",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Recruit"] = "Brassican",
                    ["Officer"] = "Brassican",
                    ["Astral"] = "General",
                    ["General"] = "Colonel"
                },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Officer"] = "Brassican",
                    ["Astral"] = "General",
                    ["General"] = "Colonel"
                }),
            Players = players,
            WomObservations = wom,
            ManualWomChecks = manualWomChecks,
            PromotionDecisions = promotions,
            Operations = operations,
            Warnings = warnings
        };
    }
}
