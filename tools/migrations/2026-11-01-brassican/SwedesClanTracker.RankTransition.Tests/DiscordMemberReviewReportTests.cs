using SwedesClanTracker.Core;
using SwedesClanTracker.RankTransition;

namespace SwedesClanTracker.RankTransition.Tests;

public sealed class DiscordMemberReviewReportTests
{
    [Fact]
    public void Frozen_report_lists_every_guild_account_and_unresolved_tracker_identity_without_guessing()
    {
        var manifest = CreateManifest();

        var report = DiscordMemberReviewReport.Build(
            manifest,
            new Dictionary<int, string> { [4] = "u4" });

        Assert.Equal(8, report.AccountCount);
        Assert.Equal(1, report.UpdateCount);
        Assert.Equal(3, report.AlreadyAlignedCount);
        Assert.Equal(1, report.HigherOrSpecialProtectedCount);
        Assert.Equal(5, report.HoldIdentityCount);
        Assert.Equal(2, report.UnresolvedTrackerCount);

        var update = Assert.Single(report.Rows, x => x.DiscordUserId == "u1");
        Assert.Equal(DiscordMemberReviewReport.UpdateAction, update.Action);
        Assert.Equal("Commander", update.FinalMappedRank);
        Assert.Equal("Commander", update.ApprovedPromotionFloor);
        Assert.Equal("Commander", update.SuggestedTarget);
        Assert.Equal(new[] { "Brassican" }, update.ObservedManagedRanks);
        Assert.Equal("UniqueExact", update.IdentityStatus);
        Assert.Contains("preserve unrelated", update.ReviewReason, StringComparison.OrdinalIgnoreCase);

        var aligned = Assert.Single(report.Rows, x => x.DiscordUserId == "u2");
        Assert.Equal(DiscordMemberReviewReport.AlreadyAlignedAction, aligned.Action);
        Assert.Equal("Commander", aligned.SuggestedTarget);

        var canonicalGeneral = Assert.Single(report.Rows, x => x.DiscordUserId == "u6");
        Assert.Equal(DiscordMemberReviewReport.AlreadyAlignedAction, canonicalGeneral.Action);
        Assert.Equal(new[] { "General" }, canonicalGeneral.ObservedManagedRanks);

        var oldGeneral = Assert.Single(report.Rows, x => x.DiscordUserId == "u7");
        Assert.Equal(DiscordMemberReviewReport.AlreadyAlignedAction, oldGeneral.Action);
        Assert.Equal(new[] { "Colonel" }, oldGeneral.ObservedManagedRanks);

        var overrideHold = Assert.Single(report.Rows, x => x.DiscordUserId == "u4");
        Assert.Equal(DiscordMemberReviewReport.HigherOrSpecialProtectedAction, overrideHold.Action);
        Assert.Equal("ReviewedOverride", overrideHold.IdentityStatus);
        Assert.Null(overrideHold.SuggestedTarget);
        Assert.Contains("higher", overrideHold.ReviewReason, StringComparison.OrdinalIgnoreCase);

        var ambiguous = Assert.Single(report.Rows, x => x.IsTrackerReviewRow && x.DiscordUsername == "Same");
        Assert.Equal("Ambiguous", ambiguous.IdentityStatus);
        Assert.Null(ambiguous.SuggestedTarget);
        Assert.Equal(new[] { "u3a", "u3b" }, ambiguous.CandidateDiscordUserIds);

        var missing = Assert.Single(report.Rows, x => x.IsTrackerReviewRow && x.DiscordUsername == "Unmatched");
        Assert.Equal("Missing", missing.IdentityStatus);
        Assert.Equal("Commander", missing.FinalMappedRank);
        Assert.Equal("Commander", missing.ApprovedPromotionFloor);
        Assert.Null(missing.SuggestedTarget);

        var unlinkedAccount = Assert.Single(report.Rows, x => x.DiscordUserId == "u5");
        Assert.Equal(DiscordMemberReviewReport.HoldIdentityAction, unlinkedAccount.Action);
        Assert.Empty(unlinkedAccount.LinkedPlayerIds);
    }

    [Fact]
    public void Fresh_read_only_observation_updates_current_columns_but_retains_frozen_target_and_partial_is_reviewed()
    {
        var manifest = CreateManifest();
        var frozen = CreateMembers();
        var freshMember = frozen.Single(x => x.UserId == "u1") with { RoleIds = ["200", "900"] };
        var freshGuild = new DiscordGuildObservation(
            "guild",
            CreateRoles(),
            MembersFullyObserved: true,
            Notes: null,
            Members: [freshMember],
            MemberHash: DiscordMemberTransitionHelpers.ComputeMemberHash([freshMember]));

        var fresh = DiscordMemberReviewReport.Build(manifest, frozen, freshGuild);
        var freshRow = Assert.Single(fresh.Rows, x => x.DiscordUserId == "u1");
        Assert.True(fresh.FreshObservationUsed);
        Assert.True(fresh.FreshObservationComplete);
        Assert.Equal(DiscordMemberReviewReport.AlreadyAlignedAction, freshRow.Action);
        Assert.Equal(new[] { "Commander" }, freshRow.ObservedManagedRanks);
        Assert.Equal("Commander", freshRow.FinalMappedRank);

        var partialGuild = freshGuild with { MembersFullyObserved = false, Notes = "page failed" };
        var partial = DiscordMemberReviewReport.Build(manifest, frozen, partialGuild);
        Assert.True(partial.FreshObservationUsed);
        Assert.False(partial.FreshObservationComplete);
        Assert.True(partial.ManualReviewRequired);
    }

    [Fact]
    public async Task Csv_and_markdown_exports_are_utf8_rfc4180_and_include_unresolved_section()
    {
        var manifest = CreateManifest(usernameForPlayerOne: "Åke,\"One\"");
        var members = CreateMembers();
        var renamed = members.Select(x => x.UserId == "u1" ? x with { Username = "Åke,\"One\"", GlobalName = "Åke,\"One\"" } : x).ToArray();
        var report = DiscordMemberReviewReport.Build(manifest, renamed);

        var csv = report.ToCsv();
        var markdown = report.ToMarkdown();
        Assert.StartsWith("DiscordUserId,DiscordUsername", csv, StringComparison.Ordinal);
        Assert.Contains("\"Åke,\"\"One\"\"\"", csv, StringComparison.Ordinal);
        Assert.Contains("## Unresolved tracker identities", markdown, StringComparison.Ordinal);
        Assert.Contains("preserve every unrelated/special role", markdown, StringComparison.OrdinalIgnoreCase);

        var directory = Path.Combine(Path.GetTempPath(), $"discord-review-{Guid.NewGuid():N}");
        var manifestPath = Path.Combine(directory, "frozen.json");
        try
        {
            var csvPath = await DiscordMemberReviewReport.WriteCsvAsync(report, manifestPath);
            var markdownPath = await DiscordMemberReviewReport.WriteMarkdownAsync(report, manifestPath);
            Assert.Equal(csv, await File.ReadAllTextAsync(csvPath));
            Assert.Equal(markdown, await File.ReadAllTextAsync(markdownPath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Inactive_protected_and_ignored_players_are_holds_even_when_identity_is_exact()
    {
        var manifest = CreateManifest();
        var changedPlayers = manifest.Players
            .Select(x => x.PlayerId == 1
                ? x with { IsActive = false, Status = PlayerStatus.MISSING_PENDING_REVIEW.ToString() }
                : x)
            .ToArray();
        var changedWom = new RankTransitionWomObservation("Already", "Commander", "Commander", true, true, true, "ignored fixture");
        var heldManifest = CopyManifest(manifest, players: changedPlayers, womObservations: [changedWom]);

        var report = DiscordMemberReviewReport.Build(heldManifest, CreateMembers());

        var inactive = Assert.Single(report.Rows, x => x.DiscordUserId == "u1");
        Assert.Equal(DiscordMemberReviewReport.HoldIdentityAction, inactive.Action);
        Assert.Null(inactive.SuggestedTarget);
        Assert.Contains("inactive", inactive.ReviewReason, StringComparison.OrdinalIgnoreCase);
        var ignored = Assert.Single(report.Rows, x => x.DiscordUserId == "u2");
        Assert.Equal(DiscordMemberReviewReport.HoldIdentityAction, ignored.Action);
        Assert.Contains("ignored", ignored.ReviewReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_frozen_target_role_is_a_hold_not_a_suggested_update()
    {
        var source = CreateManifest();
        var players = source.Players.Select(x => x.PlayerId == 1
            ? x with { DatabaseCurrentRankTarget = "Marshal", EvaluatedEligibleRank = "Marshal" }
            : x).ToArray();
        var decisions = source.PromotionDecisions.Select(x => x.PlayerId == 1
            ? x with { EvaluatedEligibleRank = "Marshal" }
            : x).ToArray();
        var operations = source.Operations.Select(x => x.TargetKey == "1"
            ? x with { Before = "Officer", After = "Marshal" }
            : x).ToArray();
        var manifest = CopyManifest(source, players, decisions, operations);

        var report = DiscordMemberReviewReport.Build(manifest, CreateMembers());
        var row = Assert.Single(report.Rows, x => x.DiscordUserId == "u1");
        Assert.Equal("Marshal", row.FinalMappedRank);
        Assert.Equal(DiscordMemberReviewReport.HoldIdentityAction, row.Action);
        Assert.Null(row.SuggestedTarget);
        Assert.Contains("No frozen configured Discord role ID", row.ReviewReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Full_canonical_role_id_map_decodes_decorated_role_names_without_legacy_double_mapping()
    {
        var source = CreateManifest();
        var decoratedRoles = CreateRoles()
            .Select(x => x.RoleId == "500" ? x with { NameBefore = "General badge", NameTarget = null } : x)
            .ToArray();
        var manifest = CopyManifest(source, discordRoles: decoratedRoles);

        var report = DiscordMemberReviewReport.Build(
            manifest,
            CreateMembers(),
            freshGuildObservation: null,
            frozenRankRoleIds: new Dictionary<string, string> { ["General"] = "500" });

        var row = Assert.Single(report.Rows, x => x.DiscordUserId == "u6");
        Assert.Equal(new[] { "General" }, row.ObservedManagedRanks);
        Assert.Equal(DiscordMemberReviewReport.AlreadyAlignedAction, row.Action);
    }

    [Fact]
    public void Frozen_role_id_meaning_wins_over_a_fresh_display_name_change()
    {
        var source = CreateManifest();
        var freshRoles = CreateRoles()
            .Select(x => x.RoleId == "600" ? x with { NameBefore = "General", NameTarget = null } : x)
            .ToArray();
        var fresh = new DiscordGuildObservation(
            "guild",
            freshRoles,
            MembersFullyObserved: true,
            Notes: null,
            Members: CreateMembers(),
            MemberHash: DiscordMemberTransitionHelpers.ComputeMemberHash(CreateMembers()));

        var report = DiscordMemberReviewReport.Build(
            source,
            source.DiscordMembers,
            fresh,
            new Dictionary<string, string> { ["Colonel"] = "600" });

        var row = Assert.Single(report.Rows, x => x.DiscordUserId == "u7");
        Assert.Equal(new[] { "Colonel" }, row.ObservedManagedRanks);
    }

    private static RankTransitionManifest CreateManifest(string usernameForPlayerOne = "Arvld")
    {
        var now = DateTimeOffset.UtcNow;
        var players = new[]
        {
            Player(1, usernameForPlayerOne, "Officer", "Commander"),
            Player(2, "Already", "Brassican", "Commander"),
            Player(3, "Same", "Brassican", "Brassican"),
            Player(4, "Override", "Brassican", "Commander"),
            Player(5, "Unmatched", "Officer", "Commander"),
            Player(6, "CanonicalGeneral", "General", "General"),
            Player(7, "OldGeneral", "General", "Colonel")
        };
        var decisions = new[]
        {
            ApprovedDecision(1, usernameForPlayerOne),
            ApprovedDecision(2, "Already"),
            ApprovedDecision(4, "Override"),
            ApprovedDecision(5, "Unmatched")
        };
        var operations = new[]
        {
            Finalization(1, "Officer", "Commander"),
            Finalization(2, "Brassican", "Commander"),
            Finalization(4, "Brassican", "Commander"),
            Finalization(5, "Officer", "Commander")
        };
        return new RankTransitionManifest
        {
            ManifestId = "frozen-discord-review",
            CreatedAtUtc = now,
            FrozenMappingVersion = FrozenRankMapping.Version,
            FrozenMapping = FrozenRankMapping.Document(),
            CompleteReadOnlyScope = true,
            ApprovedForCutover = true,
            Players = players,
            PromotionDecisions = decisions,
            Operations = operations,
            DiscordRoles = CreateRoles(),
            DiscordMembers = CreateMembers(),
            Warnings = []
        };
    }

    private static RankTransitionManifest CopyManifest(
        RankTransitionManifest source,
        IReadOnlyList<RankTransitionPlayerObservation>? players = null,
        IReadOnlyList<PromotionDecision>? decisions = null,
        IReadOnlyList<RankTransitionOperation>? operations = null,
        IReadOnlyList<RankTransitionWomObservation>? womObservations = null,
        IReadOnlyList<DiscordRoleObservation>? discordRoles = null) =>
        new()
        {
            SchemaVersion = source.SchemaVersion,
            ManifestId = source.ManifestId,
            CreatedAtUtc = source.CreatedAtUtc,
            FrozenMappingVersion = source.FrozenMappingVersion,
            CompleteReadOnlyScope = source.CompleteReadOnlyScope,
            ApprovedForCutover = source.ApprovedForCutover,
            CutoverApprovalPolicyVersion = source.CutoverApprovalPolicyVersion,
            DiscordMemberRanksManual = source.DiscordMemberRanksManual,
            FrozenMapping = source.FrozenMapping,
            Environment = source.Environment,
            Players = players ?? source.Players,
            WomObservations = womObservations ?? source.WomObservations,
            DiscordRoles = discordRoles ?? source.DiscordRoles,
            DiscordMembers = source.DiscordMembers,
            DiscordMemberRankChanges = source.DiscordMemberRankChanges,
            PromotionDecisions = decisions ?? source.PromotionDecisions,
            Warnings = source.Warnings,
            Operations = operations ?? source.Operations,
            ConfigKeyGuidance = source.ConfigKeyGuidance,
            IntegritySha256 = source.IntegritySha256
        };

    private static RankTransitionPlayerObservation Player(int id, string username, string before, string target) =>
        new(
            id,
            username,
            PlayerStatus.ACTIVE.ToString(),
            true,
            before,
            target,
            before,
            target,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            target,
            $"{target} fixture",
            false,
            null);

    private static PromotionDecision ApprovedDecision(int id, string username) =>
        new(
            id,
            username,
            "Brassican",
            "Commander",
            "Commander",
            true,
            false,
            "approved report fixture",
            ApprovedForCutover: true);

    private static RankTransitionOperation Finalization(int id, string before, string after) =>
        new(
            RankTransitionPlanner.ComputeOperationId(TransitionOperationKind.FinalizeApprovedPromotion, id.ToString(), before, after),
            TransitionOperationKind.FinalizeApprovedPromotion,
            id.ToString(),
            "PromotionApproval",
            before,
            after,
            IsExternal: false,
            IsActive: true,
            Protected: false,
            RequiresReview: false,
            SnapshotId: null,
            SnapshotHash: null,
            LastSyncedAtUtc: null,
            Reason: "report fixture");

    private static DiscordMemberObservation[] CreateMembers() =>
    [
        Member("u1", "Arvld", ["100", "900"]),
        Member("u2", "Already", ["200", "900"]),
        Member("u3a", "Same", ["100", "900"]),
        Member("u3b", "Same", ["100", "900"]),
        Member("u4", "DifferentDiscordName", ["300", "901"]),
        Member("u5", "Unlinked", ["100", "900"]),
        Member("u6", "CanonicalGeneral", ["500"]),
        Member("u7", "OldGeneral", ["600"])
    ];

    private static DiscordRoleObservation[] CreateRoles() =>
    [
        Role("100", "Brassican", false),
        Role("200", "Commander", false),
        Role("300", "General", false),
        Role("900", "Raider", false),
        Role("901", "Owner", true),
        Role("500", "General", false, "General"),
        Role("600", "General", false) with { NameTarget = null }
    ];

    private static DiscordMemberObservation Member(string id, string username, IReadOnlyList<string> roles) =>
        new(id, username, username, null, roles);

    private static DiscordRoleObservation Role(string id, string name, bool protectedRole, string? targetName = null) =>
        new(id, name, targetName ?? name, "0", 1, 0, protectedRole, protectedRole ? "special role" : null, [], DiscordMemberTransitionHelpers.ComputeRoleHash([]));
}
