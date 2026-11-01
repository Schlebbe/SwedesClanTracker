using Microsoft.EntityFrameworkCore;
using SwedesClanTracker.Core;
using SwedesClanTracker.RankTransition;

namespace SwedesClanTracker.RankTransition.Tests;

public sealed class ManualWomPolicyTests
{
    [Fact]
    public async Task Planner_manual_wom_mode_freezes_legacy_rename_as_a_read_only_check()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new PlayerSnapshot
        {
            Timestamp = now,
            Ehp = 1750.6124
        };
        var player = new Player
        {
            Id = 1,
            Username = "Irgie",
            CurrentRank = "Astral",
            EligibleRank = "Astral",
            Status = PlayerStatus.ACTIVE,
            LastSeen = now,
            LastSynced = now,
            Snapshots = [snapshot]
        };
        snapshot.Player = player;
        db.Players.Add(player);
        await db.SaveChangesAsync();

        var planner = new RankTransitionPlanner(
            db,
            new CountingWom(new Dictionary<string, string> { ["Irgie"] = "Astral" }),
            discordRoleGateway: null,
            clock: new FixedClock(now),
            environment: new TransitionEnvironmentIdentity("test-db", 7173, "guild"));

        var manifest = await planner.PlanAsync(new RankTransitionPlannerOptions(
            IncludeDiscord: false,
            WomUpdatesManual: true));

        Assert.True(manifest.WomUpdatesManual);
        Assert.DoesNotContain(manifest.Operations, x => x.Kind == TransitionOperationKind.WiseOldManRole);
        var check = Assert.Single(manifest.ManualWomChecks);
        Assert.Equal("Irgie", check.Username);
        Assert.Equal("Astral", check.RoleBefore);
        Assert.Equal("General", check.RoleAfter);
        Assert.DoesNotContain(manifest.ManualWomChecks, x => string.IsNullOrWhiteSpace(x.RoleAfter));
    }

    [Fact]
    public async Task Approved_manual_wom_case_Irgie_Captain_with_current_ehp_targets_general_directly()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new PlayerSnapshot
        {
            Timestamp = now,
            Ehp = 1750.6124
        };
        var player = new Player
        {
            Id = 1,
            Username = "Irgie",
            CurrentRank = "Captain",
            EligibleRank = "Captain",
            Status = PlayerStatus.ACTIVE,
            LastSeen = now,
            LastSynced = now,
            Snapshots = [snapshot]
        };
        snapshot.Player = player;
        db.Players.Add(player);
        await db.SaveChangesAsync();

        var planner = new RankTransitionPlanner(
            db,
            new CountingWom(new Dictionary<string, string> { ["Irgie"] = "Captain" }),
            new ApprovedDiscord(
                [
                    Role("officer-id", "Officer", 1),
                    Role("astral-id", "Astral", 2),
                    Role("general-id", "General", 3),
                    Role("commander-id", "Commander", 4)
                ],
                [new DiscordMemberObservation("irgie-user", "Irgie", null, null, ["officer-id"])]),
            new FixedClock(now),
            new TransitionEnvironmentIdentity("test-db", 7173, "guild"));

        var manifest = await planner.PlanAsync(new RankTransitionPlannerOptions(
            ApproveQualifyingPromotions: true,
            DiscordMemberRanksManual: true,
            WomUpdatesManual: true,
            DiscordRankRoleIds: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Brassican"] = "officer-id",
                ["General"] = "astral-id",
                ["Colonel"] = "general-id",
                ["Commander"] = "commander-id"
            },
            DiscordMemberIdentityOverrides: new Dictionary<int, string> { [1] = "irgie-user" }));

        var decision = Assert.Single(manifest.PromotionDecisions);
        Assert.True(decision.ApprovedForCutover);
        Assert.Equal("General", decision.EvaluatedEligibleRank);
        Assert.Equal("General", manifest.Players.Single().DatabaseCurrentRankTarget);
        Assert.DoesNotContain(manifest.Operations, x => x.Kind == TransitionOperationKind.WiseOldManRole);
        Assert.Contains(manifest.Operations, x =>
            x.Kind == TransitionOperationKind.FinalizeApprovedPromotion &&
            x.Before == "Captain" &&
            x.After == "General");
        var womCheck = Assert.Single(manifest.ManualWomChecks);
        Assert.Equal("Captain", womCheck.RoleBefore);
        Assert.Equal("General", womCheck.RoleAfter);
    }

    [Fact]
    public async Task Manual_wom_mismatch_fails_before_tracker_database_writes_and_never_calls_update()
    {
        await using var db = CreateDb();
        db.Players.Add(new Player
        {
            Id = 1,
            Username = "Irgie",
            CurrentRank = "Astral",
            EligibleRank = "Astral",
            Status = PlayerStatus.ACTIVE,
            LastSeen = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var operation = Operation(
            TransitionOperationKind.DatabaseCurrentRank,
            "1",
            "Astral",
            "General");
        var manifest = ManualManifest(
            operation,
            new RankTransitionManualWomCheck(
                "wom-check-irgie",
                "Irgie",
                "Astral",
                "General",
                true,
                "Manual in-game reconciliation target."));
        var wom = new CountingWom(new Dictionary<string, string> { ["Irgie"] = "Astral" });
        var path = TemporaryJournalPath();

        try
        {
            var report = await new RankTransitionApplier(db, wom, null)
                .ApplyAsync(manifest, path, Gates());

            Assert.False(report.Completed);
            Assert.Contains("Irgie", report.Failure, StringComparison.Ordinal);
            Assert.Contains("expected final WOM role 'General'", report.Failure, StringComparison.Ordinal);
            Assert.Equal(0, wom.UpdateCount);
            Assert.Equal(1, wom.FreshReadCount);
            Assert.Equal(1, wom.GroupReadCount);
            Assert.Equal("Astral", (await db.Players.AsNoTracking().SingleAsync()).CurrentRank);
            Assert.DoesNotContain(db.LifecycleEvents, x => x.EventType == "RANK_TRANSITION_POLICY_MARKER");
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Fact]
    public async Task Manual_wom_checks_batch_to_one_fresh_roster_read_for_many_players()
    {
        await using var db = CreateDb();
        db.Players.Add(new Player
        {
            Id = 1,
            Username = "Irgie",
            CurrentRank = "Astral",
            EligibleRank = "Astral",
            Status = PlayerStatus.ACTIVE,
            LastSeen = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var checks = Enumerable.Range(1, 100)
            .Select(index => new RankTransitionManualWomCheck(
                $"wom-check-{index}",
                $"Player{index}",
                "Astral",
                "General",
                true,
                "Manual in-game reconciliation target."))
            .ToArray();
        var roles = checks.ToDictionary(x => x.Username, _ => "General", StringComparer.OrdinalIgnoreCase);
        var manifest = ManualManifest(
            Operation(TransitionOperationKind.DatabaseCurrentRank, "1", "Astral", "General"),
            checks,
            roles);
        var wom = new CountingWom(roles);
        var path = TemporaryJournalPath();

        try
        {
            var report = await new RankTransitionApplier(db, wom, null)
                .ApplyAsync(manifest, path, Gates());

            Assert.True(report.Completed, report.Failure);
            Assert.Equal(1, wom.FreshReadCount);
            Assert.Equal(1, wom.GroupReadCount);
            Assert.Equal(0, wom.UpdateCount);
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Fact]
    public async Task Manual_wom_apply_and_rollback_update_tracker_only()
    {
        await using var db = CreateDb();
        db.Players.Add(new Player
        {
            Id = 1,
            Username = "Irgie",
            CurrentRank = "Astral",
            EligibleRank = "Astral",
            Status = PlayerStatus.ACTIVE,
            LastSeen = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var operation = Operation(
            TransitionOperationKind.DatabaseCurrentRank,
            "1",
            "Astral",
            "General");
        var manifest = ManualManifest(
            operation,
            new RankTransitionManualWomCheck(
                "wom-check-irgie",
                "Irgie",
                "Astral",
                "General",
                true,
                "Manual in-game reconciliation target."));
        var wom = new CountingWom(new Dictionary<string, string> { ["Irgie"] = "General" });
        var path = TemporaryJournalPath();

        try
        {
            var applier = new RankTransitionApplier(db, wom, null);
            var applied = await applier.ApplyAsync(manifest, path, Gates());
            Assert.True(applied.Completed, applied.Failure);
            Assert.Equal("General", (await db.Players.AsNoTracking().SingleAsync()).CurrentRank);

            var rollback = await applier.RollbackAsync(manifest, path, Gates());
            Assert.True(rollback.Completed, rollback.Failure);
            Assert.Equal("Astral", (await db.Players.AsNoTracking().SingleAsync()).CurrentRank);
            Assert.Equal("General", wom.Roles["Irgie"]);
            Assert.Equal(0, wom.UpdateCount);
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Fact]
    public async Task Manual_approved_finalization_retries_from_a_target_wom_read_without_wom_mutation()
    {
        await using var db = CreateDb();
        db.Players.Add(new Player
        {
            Id = 1,
            Username = "Irgie",
            CurrentRank = "Astral",
            EligibleRank = "General",
            Status = PlayerStatus.ACTIVE,
            LastSeen = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var operation = Operation(
            TransitionOperationKind.FinalizeApprovedPromotion,
            "1",
            "Astral",
            "General");
        var manifest = ManualManifest(
            operation,
            new RankTransitionManualWomCheck(
                "wom-check-irgie",
                "Irgie",
                "Astral",
                "General",
                true,
                "Manual in-game reconciliation target."),
            approved: true);
        var wom = new CountingWom(new Dictionary<string, string> { ["Irgie"] = "General" });
        var path = TemporaryJournalPath();

        try
        {
            var applier = new RankTransitionApplier(db, wom, null);
            var first = await applier.ApplyAsync(manifest, path, Gates());
            Assert.True(first.Completed, first.Failure);
            var approved = await db.PromotionCandidates.AsNoTracking().SingleAsync();
            Assert.Equal(PromotionStatus.APPROVED, approved.Status);

            var journalStore = new RankTransitionJournalStore();
            var journal = await journalStore.LoadOrCreateAsync(path, manifest);
            journal.Results = journal.Results
                .Where(x => x.OperationId != operation.OperationId)
                .ToArray();
            await journalStore.SaveAsync(path, journal);

            var retry = await applier.ApplyAsync(manifest, path, Gates());
            Assert.True(retry.Completed, retry.Failure);
            Assert.Contains(retry.Results, x =>
                x.OperationId == operation.OperationId &&
                x.Status == TransitionOperationStatus.AlreadyApplied &&
                x.RelatedEntityId == approved.Id);
            Assert.Single(await db.PromotionCandidates.AsNoTracking().ToListAsync());
            Assert.Equal(0, wom.UpdateCount);
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Fact]
    public void Manual_wom_manifest_rejects_a_legacy_wom_mutation_operation()
    {
        var manifest = ManualManifest(
            Operation(TransitionOperationKind.WiseOldManRole, "Irgie", "Astral", "General"),
            new RankTransitionManualWomCheck(
                "wom-check-irgie",
                "Irgie",
                "Astral",
                "General",
                true,
                "Manual in-game reconciliation target."));

        var exception = Assert.Throws<InvalidOperationException>(() => Gates().Validate(manifest));

        Assert.Contains("manual-WOM", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static RankTransitionManifest ManualManifest(
        RankTransitionOperation operation,
        RankTransitionManualWomCheck check,
        bool approved = false) => ManualManifest(operation, [check],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [check.Username] = check.RoleAfter ?? ""
            }, approved);

    private static RankTransitionManifest ManualManifest(
        RankTransitionOperation operation,
        IReadOnlyList<RankTransitionManualWomCheck> checks,
        IReadOnlyDictionary<string, string> roles,
        bool approved = false)
    {
        var manifest = new RankTransitionManifest
        {
            ManifestId = Guid.NewGuid().ToString("N"),
            FrozenMappingVersion = FrozenRankMapping.Version,
            FrozenMapping = FrozenRankMapping.Document(),
            ApprovedForCutover = approved,
            WomUpdatesManual = true,
            Operations = [operation],
            ManualWomChecks = checks,
            Warnings = []
        };
        manifest.IntegritySha256 = RankTransitionJson.ComputeIntegrity(manifest);
        return manifest;
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
            kind == TransitionOperationKind.FinalizeApprovedPromotion ? "PromotionApproval" : "CurrentRank",
            before,
            after,
            IsExternal: kind == TransitionOperationKind.WiseOldManRole,
            IsActive: true,
            Protected: false,
            RequiresReview: false,
            SnapshotId: null,
            SnapshotHash: null,
            LastSyncedAtUtc: null,
            Reason: "manual-WOM test");

    private static ApplyPrerequisites Gates() => new("manual-wom-test", true, true, true);

    private static TrackerDbContext CreateDb() => new(new DbContextOptionsBuilder<TrackerDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    private static string TemporaryJournalPath() =>
        Path.Combine(Path.GetTempPath(), $"rank-transition-manual-wom-{Guid.NewGuid():N}.journal.json");

    private static void DeleteJournal(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists($"{path}.lock")) File.Delete($"{path}.lock");
    }

    private sealed class FixedClock(DateTimeOffset now) : ITransitionClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class CountingWom(IReadOnlyDictionary<string, string> initialRoles) : IWiseOldManClient
    {
        public Dictionary<string, string> Roles { get; } = new(initialRoles, StringComparer.OrdinalIgnoreCase);
        public int FreshReadCount { get; private set; }
        public int GroupReadCount { get; private set; }
        public int UpdateCount { get; private set; }

        public Task<string?> GetMemberRoleAsync(string username, CancellationToken ct) =>
            Task.FromResult(Roles.TryGetValue(username, out var role) ? role : null);

        public Task<string?> GetMemberRoleFreshAsync(string username, CancellationToken ct)
        {
            FreshReadCount++;
            return GetMemberRoleAsync(username, ct);
        }

        public Task<IReadOnlyDictionary<string, string>> GetMemberRolesAsync(CancellationToken ct)
        {
            GroupReadCount++;
            return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(Roles, StringComparer.OrdinalIgnoreCase));
        }

        public Task<bool> IsImpAccountAsync(string username, CancellationToken ct) => Task.FromResult(false);

        public Task<WomRoleUpdateResult> UpdateMemberRoleAsync(string username, string role, CancellationToken ct, bool invalidateCache = true)
        {
            UpdateCount++;
            Roles[username] = role;
            return Task.FromResult(new WomRoleUpdateResult(true, 200, "unexpected WOM mutation", role, null, username));
        }

        public Task<(bool Success, string Details)> RemoveMemberAsync(string username, CancellationToken ct) =>
            Task.FromResult((true, "ok"));

        public Task InvalidateCacheAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ApprovedDiscord(
        IReadOnlyList<DiscordRoleObservation> roles,
        IReadOnlyList<DiscordMemberObservation> members) : IDiscordRoleGateway
    {
        public Task<DiscordGuildObservation?> GetGuildObservationAsync(CancellationToken ct) =>
            Task.FromResult<DiscordGuildObservation?>(new DiscordGuildObservation(
                "guild",
                roles,
                true,
                null,
                members));

        public Task<DiscordRoleMutationResult> RenameRoleAsync(string roleId, string expectedName, string targetName, CancellationToken ct) =>
            Task.FromResult(new DiscordRoleMutationResult(true, 200, "ok", null));

        public Task<DiscordMemberObservation?> GetMemberObservationAsync(string userId, CancellationToken ct) =>
            Task.FromResult(members.FirstOrDefault(x => x.UserId == userId));
    }

    private static DiscordRoleObservation Role(string roleId, string name, int position) =>
        new(
            roleId,
            name,
            null,
            "1",
            position,
            0,
            false,
            null,
            [],
            DiscordMemberTransitionHelpers.ComputeRoleHash([]));
}
