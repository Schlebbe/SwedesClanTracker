using Microsoft.EntityFrameworkCore;
using SwedesClanTracker.Core;
using SwedesClanTracker.RankTransition;

namespace SwedesClanTracker.RankTransition.Tests;

public sealed class RankTransitionPlannerTests
{
    [Fact]
    public async Task Planner_maps_each_system_independently_and_orders_discord_moves_without_collision()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var ignored = AddPlayer(db, 1, "AstralMember", "Astral", "Astral", PlayerStatus.ACTIVE, Snapshot(now));
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = ignored.Id,
            EventType = "WOM_RANK_MISMATCH_IGNORED",
            MetadataJson = "{\"Username\":\"AstralMember\"}",
            Status = "OPEN",
            CreatedAt = now
        });
        AddPlayer(db, 2, "GeneralMember", "General", "General", PlayerStatus.ACTIVE, Snapshot(now));
        AddPlayer(db, 3, "InactiveOfficer", "Officer", "Officer", PlayerStatus.MISSING_PENDING_REVIEW, Snapshot(now));
        var upgrade = AddPlayer(db, 4, "UpgradeMember", "Officer", "Officer", PlayerStatus.ACTIVE, Snapshot(now));
        db.PromotionCandidates.Add(new PromotionCandidate
        {
            PlayerId = upgrade.Id,
            Player = upgrade,
            OldRank = "Officer",
            NewRank = "Astral",
            Reason = "old policy",
            Status = PromotionStatus.PENDING,
            CreatedAt = now
        });
        await db.SaveChangesAsync();

        var wom = new FakeWom(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AstralMember"] = "General",
            ["GeneralMember"] = "General",
            ["InactiveOfficer"] = "Officer",
            ["UpgradeMember"] = "Officer"
        });
        var discord = new FakeDiscordGateway(
        [
            new DiscordRoleObservation("r-officer", "Officer", null, "1", 1, 2, false, null),
            new DiscordRoleObservation("r-astral", "Astral", null, "2", 2, 3, false, null),
            new DiscordRoleObservation("r-general", "General", null, "3", 3, 4, false, null)
        ]);
        var planner = new RankTransitionPlanner(db, wom, discord, new FixedClock(now));

        var manifest = await planner.PlanAsync();

        Assert.Equal("General", manifest.Players.Single(x => x.PlayerId == ignored.Id).DatabaseCurrentRankTarget);
        Assert.Equal("General", manifest.Players.Single(x => x.PlayerId == ignored.Id).EvaluatedEligibleRank);
        Assert.Contains(manifest.Operations, x => x.Kind == TransitionOperationKind.DatabaseCurrentRank && x.TargetKey == "1" && x.After == "General");
        Assert.Contains(manifest.Operations, x => x.Kind == TransitionOperationKind.WiseOldManRole && x.TargetKey == "AstralMember" && x.After == "Colonel");
        Assert.DoesNotContain(manifest.Operations, x => x.Kind == TransitionOperationKind.QueuePromotionCandidate && x.TargetKey == "1");
        Assert.DoesNotContain(manifest.Operations, x => x.Kind == TransitionOperationKind.WiseOldManRole && x.TargetKey == "InactiveOfficer");
        Assert.DoesNotContain(manifest.Operations, x => x.Kind == TransitionOperationKind.QueuePromotionCandidate && x.TargetKey == "3");
        Assert.Contains(manifest.Operations, x => x.Kind == TransitionOperationKind.SupersedePromotionCandidate);
        Assert.Contains(manifest.Operations, x => x.Kind == TransitionOperationKind.QueuePromotionCandidate && x.TargetKey == "4" && x.After == "General");

        var discordMoves = manifest.Operations.Where(x => x.Kind == TransitionOperationKind.DiscordRoleRename).ToArray();
        Assert.Equal(3, discordMoves.Length);
        Assert.Contains(discordMoves, x => x.TargetKey == "r-general");
        Assert.Contains(discordMoves, x => x.TargetKey == "r-astral");
        Assert.Contains(discordMoves, x => x.TargetKey == "r-officer");
        Assert.True(Array.FindIndex(discordMoves, x => x.TargetKey == "r-general") <
                    Array.FindIndex(discordMoves, x => x.TargetKey == "r-astral"));
    }

    [Fact]
    public async Task Planner_fails_closed_for_unknown_db_rank_and_does_not_create_external_work()
    {
        await using var db = CreateDb();
        var player = AddPlayer(db, 1, "Unknown", "FutureRank", "FutureRank", PlayerStatus.ACTIVE, Snapshot(DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        var planner = new RankTransitionPlanner(db, new FakeWom(new Dictionary<string, string> { ["Unknown"] = "Officer" }), new FakeDiscordGateway([]));

        var manifest = await planner.PlanAsync();

        Assert.True(manifest.Players.Single().Protected);
        Assert.Contains(manifest.Warnings, x => x.Code == "WOM_SPECIAL_PROTECTED" || x.Subject == "Unknown");
        Assert.DoesNotContain(manifest.Operations, x => x.IsExternal && x.TargetKey == "Unknown");
    }

    [Fact]
    public async Task Planner_refuses_a_second_plan_after_policy_marker()
    {
        await using var db = CreateDb();
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = 0,
            EventType = "RANK_TRANSITION_POLICY_MARKER",
            MetadataJson = "{\"MappingVersion\":\"legacy-rank-transition-v1\"}",
            Status = "OPEN",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var planner = new RankTransitionPlanner(db, new FakeWom(new Dictionary<string, string>()), new FakeDiscordGateway([]));

        await Assert.ThrowsAsync<InvalidOperationException>(() => planner.PlanAsync());
    }

    [Fact]
    public async Task Planner_uses_configured_role_ids_and_blocks_true_target_collisions()
    {
        await using var db = CreateDb();
        await db.SaveChangesAsync();
        var roles = new[]
        {
            new DiscordRoleObservation("officer-id", "Officer", null, "1", 1, 1, false, null),
            new DiscordRoleObservation("astral-id", "Astral", null, "2", 2, 2, false, null),
            new DiscordRoleObservation("general-id", "General", null, "3", 3, 3, false, null),
            new DiscordRoleObservation("custom-general", "General", null, "4", 4, 4, false, "Unconfigured duplicate")
        };
        var planner = new RankTransitionPlanner(
            db,
            new FakeWom(new Dictionary<string, string>()),
            new FakeDiscordGateway(roles),
            environment: new TransitionEnvironmentIdentity("test-db", 7173, "guild"));

        var manifest = await planner.PlanAsync(new RankTransitionPlannerOptions(
            DiscordRankRoleIds: new Dictionary<string, string>
            {
                ["Officer"] = "officer-id",
                ["Astral"] = "astral-id",
                ["General"] = "general-id"
            }));

        Assert.Contains(manifest.Operations, x => x.Kind == TransitionOperationKind.DiscordRoleRename && x.TargetKey == "officer-id");
        Assert.Contains(manifest.Operations, x => x.Kind == TransitionOperationKind.DiscordRoleRename && x.TargetKey == "general-id");
        Assert.DoesNotContain(manifest.Operations, x => x.Kind == TransitionOperationKind.DiscordRoleRename && x.TargetKey == "astral-id");
        Assert.Contains(manifest.Warnings, x => x.Code == "DISCORD_ROLE_TARGET_COLLISION" && x.BlocksApply);
    }

    [Fact]
    public async Task Planner_accepts_all_configured_roles_already_at_frozen_targets_without_writes()
    {
        await using var db = CreateDb();
        DiscordRoleObservation[] roles =
        [
            new DiscordRoleObservation("officer-id", "Brassican", null, "1", 1, 1, false, null),
            new DiscordRoleObservation("astral-id", "General", null, "2", 2, 2, false, null),
            new DiscordRoleObservation("general-id", "Colonel", null, "3", 3, 3, false, null)
        ];
        var planner = new RankTransitionPlanner(
            db,
            new FakeWom(new Dictionary<string, string>()),
            new FakeDiscordGateway(roles),
            environment: new TransitionEnvironmentIdentity("test-db", 7173, "guild"));

        var manifest = await planner.PlanAsync(new RankTransitionPlannerOptions(
            DiscordRankRoleIds: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Officer"] = "officer-id",
                ["Astral"] = "astral-id",
                ["General"] = "general-id"
            }));

        Assert.DoesNotContain(manifest.Operations, x => x.Kind == TransitionOperationKind.DiscordRoleRename);
        Assert.DoesNotContain(manifest.Warnings, x => x.Code is "DISCORD_ROLE_NAME_DRIFT" or "DISCORD_ROLE_TARGET_COLLISION");
        Assert.Equal("Brassican", manifest.DiscordRoles.Single(x => x.RoleId == "officer-id").NameTarget);
        Assert.Equal("General", manifest.DiscordRoles.Single(x => x.RoleId == "astral-id").NameTarget);
        Assert.Equal("Colonel", manifest.DiscordRoles.Single(x => x.RoleId == "general-id").NameTarget);

        var configuredIdKeys = manifest.ConfigKeyGuidance.Keys
            .Where(x => x.StartsWith("DiscordBot:RankRoleIds:", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "DiscordBot:RankRoleIds:Brassican",
                "DiscordBot:RankRoleIds:Colonel",
                "DiscordBot:RankRoleIds:General"
            },
            configuredIdKeys);
        Assert.DoesNotContain("DiscordBot:RankRoleIds:Officer", configuredIdKeys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("DiscordBot:RankRoleIds:Astral", configuredIdKeys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("officer-id", manifest.ConfigKeyGuidance["DiscordBot:RankRoleIds:Brassican"]);
        Assert.Equal("astral-id", manifest.ConfigKeyGuidance["DiscordBot:RankRoleIds:General"]);
        Assert.Equal("general-id", manifest.ConfigKeyGuidance["DiscordBot:RankRoleIds:Colonel"]);
        Assert.True(manifest.ConfigKeyGuidance["DiscordBot:Token"].Contains("plan", StringComparison.OrdinalIgnoreCase));
        Assert.True(manifest.ConfigKeyGuidance["DiscordBot:Token"].Contains("GET", StringComparison.Ordinal));
        Assert.True(manifest.ConfigKeyGuidance["DiscordBot:RankRoleIdsMigration"].Contains("remove retired", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Planner_handles_mixed_partial_discord_completion_by_configured_id_without_double_mapping_general()
    {
        await using var db = CreateDb();
        DiscordRoleObservation[] roles =
        [
            new DiscordRoleObservation("officer-id", "Brassican", null, "1", 1, 1, false, null),
            new DiscordRoleObservation("astral-id", "Astral", null, "2", 2, 2, false, null),
            new DiscordRoleObservation("general-id", "Colonel", null, "3", 3, 3, false, null)
        ];
        var planner = new RankTransitionPlanner(
            db,
            new FakeWom(new Dictionary<string, string>()),
            new FakeDiscordGateway(roles),
            environment: new TransitionEnvironmentIdentity("test-db", 7173, "guild"));

        var manifest = await planner.PlanAsync(new RankTransitionPlannerOptions(
            DiscordRankRoleIds: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Officer"] = "officer-id",
                ["Astral"] = "astral-id",
                ["General"] = "general-id"
            }));

        var rename = Assert.Single(manifest.Operations, x => x.Kind == TransitionOperationKind.DiscordRoleRename);
        Assert.Equal("astral-id", rename.TargetKey);
        Assert.Equal("Astral", rename.Before);
        Assert.Equal("General", rename.After);
        Assert.DoesNotContain(manifest.Operations, x => x.TargetKey == "general-id");
        Assert.DoesNotContain(manifest.Operations, x => x.After == "Colonel" && x.TargetKey == "astral-id");
    }

    [Fact]
    public async Task Planner_holds_promotion_when_mapped_wom_standing_exceeds_fresh_eligibility()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new PlayerSnapshot
        {
            Timestamp = now,
            Ehp = 1750
        };
        AddPlayer(db, 1, "WomAboveEligibility", "Brassican", "Commander", PlayerStatus.ACTIVE, snapshot);
        await db.SaveChangesAsync();

        var planner = new RankTransitionPlanner(
            db,
            new FakeWom(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["WomAboveEligibility"] = "General"
            }),
            discordRoleGateway: null,
            clock: new FixedClock(now),
            environment: new TransitionEnvironmentIdentity("test-db", 7173, "guild"));

        var manifest = await planner.PlanAsync(new RankTransitionPlannerOptions(
            IncludeDiscord: false,
            IncludeWom: true));

        var wom = Assert.Single(manifest.WomObservations);
        Assert.True(wom.Protected);
        Assert.Equal("Colonel", wom.RoleTarget);
        Assert.Contains(manifest.Warnings, x => x.Code == "WOM_STANDING_ABOVE_ELIGIBILITY" && !x.BlocksApply);
        Assert.Contains(manifest.Operations, x =>
            x.Kind == TransitionOperationKind.WiseOldManRole &&
            x.TargetKey == "WomAboveEligibility" &&
            x.After == "Colonel");
        Assert.DoesNotContain(manifest.Operations, x =>
            x.Kind == TransitionOperationKind.QueuePromotionCandidate &&
            x.TargetKey == "1");
    }

    private static Player AddPlayer(TrackerDbContext db, int id, string username, string current, string eligible, PlayerStatus status, PlayerSnapshot snapshot)
    {
        var player = new Player
        {
            Id = id,
            Username = username,
            CurrentRank = current,
            EligibleRank = eligible,
            Status = status,
            LastSeen = DateTimeOffset.UtcNow,
            LastSynced = DateTimeOffset.UtcNow
        };
        snapshot.PlayerId = id;
        snapshot.Player = player;
        player.Snapshots.Add(snapshot);
        db.Players.Add(player);
        return player;
    }

    private static PlayerSnapshot Snapshot(DateTimeOffset timestamp) => new()
    {
        Timestamp = timestamp,
        TotalLevel = 1,
        Ehb = 0,
        Ehp = 0,
        Collections = 900,
        PetCount = 15
    };

    private static TrackerDbContext CreateDb() => new(new DbContextOptionsBuilder<TrackerDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    private sealed class FixedClock(DateTimeOffset utcNow) : ITransitionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FakeWom(IReadOnlyDictionary<string, string> roles) : IWiseOldManClient
    {
        public Task<string?> GetMemberRoleAsync(string username, CancellationToken ct) =>
            Task.FromResult(roles.TryGetValue(username, out var role) ? role : null);

        public Task<string?> GetMemberRoleFreshAsync(string username, CancellationToken ct) =>
            GetMemberRoleAsync(username, ct);

        public Task<IReadOnlyDictionary<string, string>> GetMemberRolesAsync(CancellationToken ct) => Task.FromResult(roles);
        public Task<bool> IsImpAccountAsync(string username, CancellationToken ct) => Task.FromResult(false);
        public Task<WomRoleUpdateResult> UpdateMemberRoleAsync(string username, string role, CancellationToken ct, bool invalidateCache = true) =>
            Task.FromResult(new WomRoleUpdateResult(true, 200, "ok", role, null, null));
        public Task<(bool Success, string Details)> RemoveMemberAsync(string username, CancellationToken ct) => Task.FromResult((true, "ok"));
        public Task InvalidateCacheAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeDiscordGateway(IReadOnlyList<DiscordRoleObservation> roles) : IDiscordRoleGateway
    {
        public Task<DiscordGuildObservation?> GetGuildObservationAsync(CancellationToken ct) =>
            Task.FromResult<DiscordGuildObservation?>(new DiscordGuildObservation("guild", roles, true, null));

        public Task<DiscordRoleMutationResult> RenameRoleAsync(string roleId, string expectedName, string targetName, CancellationToken ct) =>
            Task.FromResult(new DiscordRoleMutationResult(true, 200, "ok", null));
    }
}
