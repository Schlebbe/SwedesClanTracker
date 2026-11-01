using Microsoft.EntityFrameworkCore;
using SwedesClanTracker.Core;
using SwedesClanTracker.RankTransition;

namespace SwedesClanTracker.RankTransition.Tests;

public sealed class ApprovedCutoverPlannerTests
{
    [Fact]
    public async Task Approved_cutover_uses_one_final_commander_target_without_transient_brassican_queue()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        AddPlayer(db, 1, "Arvld", "Officer", "Officer", Snapshot(now, totalLevel: 2200));
        await db.SaveChangesAsync();

        var roles = Roles("user-1", "officer-id");
        var planner = new RankTransitionPlanner(
            db,
            new ApprovedPlannerWom(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Arvld"] = "Officer"
            }),
            new ApprovedPlannerDiscord(roles, [Member("user-1", "Arvld", "officer-id")]),
            new ApprovedPlannerClock(now),
            new TransitionEnvironmentIdentity("test-db", 7173, "guild"));

        var manifest = await planner.PlanAsync(new RankTransitionPlannerOptions(
            DiscordRankRoleIds: PostCutoverRoleIds(),
            ApproveQualifyingPromotions: true));

        var decision = Assert.Single(manifest.PromotionDecisions);
        Assert.True(decision.ApprovedForCutover);
        Assert.False(decision.QueuedThroughApprovalFlow);
        Assert.Equal("Commander", manifest.Players.Single().DatabaseCurrentRankTarget);
        Assert.DoesNotContain(manifest.Operations, x => x.Kind == TransitionOperationKind.QueuePromotionCandidate);
        Assert.DoesNotContain(manifest.Operations, x =>
            x.Kind == TransitionOperationKind.DatabaseCurrentRank && x.After == "Brassican");
        Assert.Contains(manifest.Operations, x =>
            x.Kind == TransitionOperationKind.FinalizeApprovedPromotion &&
            x.Before == "Officer" && x.After == "Commander");
        Assert.Contains(manifest.Operations, x =>
            x.Kind == TransitionOperationKind.WiseOldManRole &&
            x.TargetKey == "Arvld" && x.After == "Commander");
        Assert.Equal("Commander", Assert.Single(manifest.DiscordMemberRankChanges).TargetRanks.Single());
    }

    [Fact]
    public async Task Shared_reviewed_discord_account_emits_one_highest_floor_member_plan()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        AddPlayer(db, 1, "LowerAccount", "Officer", "Officer", Snapshot(now, collections: 900));
        AddPlayer(db, 2, "HigherAccount", "Officer", "Officer", Snapshot(now, totalLevel: 2200));
        await db.SaveChangesAsync();

        var member = Member("shared-user", "SharedDiscordName", "officer-id");
        var planner = new RankTransitionPlanner(
            db,
            new ApprovedPlannerWom(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["LowerAccount"] = "Officer",
                ["HigherAccount"] = "Officer"
            }),
            new ApprovedPlannerDiscord(Roles("shared-user", "officer-id"), [member]),
            new ApprovedPlannerClock(now),
            new TransitionEnvironmentIdentity("test-db", 7173, "guild"));

        var manifest = await planner.PlanAsync(new RankTransitionPlannerOptions(
            DiscordRankRoleIds: RoleIds(),
            ApproveQualifyingPromotions: true,
            DiscordMemberIdentityOverrides: new Dictionary<int, string>
            {
                [1] = "shared-user",
                [2] = "shared-user"
            }));

        var plan = Assert.Single(manifest.DiscordMemberRankChanges);
        Assert.Equal("shared-user", plan.DiscordUserId);
        Assert.Equal(new[] { 1, 2 }, plan.PlayerIds);
        // Core's frozen clan order places General above Commander; the
        // shared-account plan must therefore choose the General role ID even
        // though one linked player qualifies for Commander.
        Assert.Equal("astral-id", plan.TargetRoleId);
        Assert.Contains("General", plan.TargetRanks);
        Assert.Contains("Commander", plan.TargetRanks);
        Assert.Single(manifest.Operations, x => x.Kind == TransitionOperationKind.DiscordMemberRankRole);
    }

    [Fact]
    public async Task Manual_cutover_freezes_full_discord_roster_without_member_mutation_operations()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        AddPlayer(db, 1, "ManualArvld", "Officer", "Officer", Snapshot(now, totalLevel: 2200));
        await db.SaveChangesAsync();
        var roles = Roles("manual-user", "officer-id");
        var planner = new RankTransitionPlanner(
            db,
            new ApprovedPlannerWom(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ManualArvld"] = "Officer"
            }),
            new ApprovedPlannerDiscord(roles, [Member("manual-user", "ManualArvld", "officer-id")]),
            new ApprovedPlannerClock(now),
            new TransitionEnvironmentIdentity("test-db", 7173, "guild"));

        var manifest = await planner.PlanAsync(new RankTransitionPlannerOptions(
            DiscordRankRoleIds: PostCutoverRoleIds(),
            ApproveQualifyingPromotions: true,
            DiscordMemberRanksManual: true));

        Assert.True(manifest.DiscordMemberRanksManual);
        Assert.Single(manifest.DiscordMembers);
        Assert.DoesNotContain(manifest.Operations, x => x.Kind == TransitionOperationKind.DiscordMemberRankRole);
        Assert.Contains(manifest.Operations, x => x.Kind == TransitionOperationKind.FinalizeApprovedPromotion);
        Assert.DoesNotContain(manifest.Warnings, x => x.BlocksApply && x.Code.StartsWith("CUTOVER_MEMBER", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Post_cutover_role_ids_keep_general_canonical_and_do_not_collide_with_colonel()
    {
        await using var db = CreateDb();
        var planner = new RankTransitionPlanner(
            db,
            new ApprovedPlannerWom(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            new ApprovedPlannerDiscord(PostCutoverRoles(), []),
            new ApprovedPlannerClock(DateTimeOffset.UtcNow),
            new TransitionEnvironmentIdentity("test-db", 7173, "guild"));

        var manifest = await planner.PlanAsync(new RankTransitionPlannerOptions(
            DiscordRankRoleIds: PostCutoverRoleIds()));

        Assert.Equal("officer-id", manifest.FrozenDiscordRankRoleIds["Brassican"]);
        Assert.Equal("astral-id", manifest.FrozenDiscordRankRoleIds["General"]);
        Assert.Equal("general-id", manifest.FrozenDiscordRankRoleIds["Colonel"]);
        Assert.DoesNotContain(manifest.Warnings, x => x.Code == "DISCORD_RANK_ROLE_ID_COLLISION");
    }

    private static Dictionary<string, string> RoleIds() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Officer"] = "officer-id",
        ["Astral"] = "astral-id",
        ["General"] = "general-id",
        ["Commander"] = "commander-id"
    };

    private static Dictionary<string, string> PostCutoverRoleIds() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Brassican"] = "officer-id",
        ["General"] = "astral-id",
        ["Colonel"] = "general-id",
        ["Commander"] = "commander-id"
    };

    private static IReadOnlyList<DiscordRoleObservation> Roles(string memberId, string memberRoleId) =>
    [
        Role("guild", "officer-id", "Officer", 1, memberId, memberRoleId),
        Role("guild", "astral-id", "Astral", 2),
        Role("guild", "general-id", "General", 3),
        Role("guild", "commander-id", "Commander", 4)
    ];

    private static IReadOnlyList<DiscordRoleObservation> PostCutoverRoles() =>
    [
        Role("guild", "officer-id", "Brassican", 1),
        Role("guild", "astral-id", "General", 2),
        Role("guild", "general-id", "Colonel", 3),
        Role("guild", "commander-id", "Commander", 4)
    ];

    private static DiscordRoleObservation Role(
        string guildId,
        string roleId,
        string name,
        int position,
        string? memberId = null,
        string? memberRoleId = null)
    {
        var members = memberId is null || memberRoleId != roleId ? [] : new[] { memberId };
        return new DiscordRoleObservation(
            roleId,
            name,
            null,
            "1",
            position,
            members.Length,
            Protected: false,
            Notes: null,
            MemberIds: members,
            MembershipHash: DiscordMemberTransitionHelpers.ComputeRoleHash(members));
    }

    private static DiscordMemberObservation Member(string userId, string username, string roleId) =>
        new(userId, username, null, null, [roleId]);

    private static Player AddPlayer(
        TrackerDbContext db,
        int id,
        string username,
        string current,
        string eligible,
        PlayerSnapshot snapshot)
    {
        var player = new Player
        {
            Id = id,
            Username = username,
            CurrentRank = current,
            EligibleRank = eligible,
            Status = PlayerStatus.ACTIVE,
            LastSeen = snapshot.Timestamp,
            LastSynced = snapshot.Timestamp,
            Snapshots = [snapshot]
        };
        snapshot.PlayerId = id;
        snapshot.Player = player;
        db.Players.Add(player);
        return player;
    }

    private static PlayerSnapshot Snapshot(
        DateTimeOffset timestamp,
        int totalLevel = 1,
        int collections = 0) => new()
    {
        Timestamp = timestamp,
        TotalLevel = totalLevel,
        Collections = collections,
        Ehb = 0,
        Ehp = 0,
        PetCount = 0
    };

    private static TrackerDbContext CreateDb() => new(new DbContextOptionsBuilder<TrackerDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    private sealed class ApprovedPlannerClock(DateTimeOffset now) : ITransitionClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class ApprovedPlannerWom(IReadOnlyDictionary<string, string> roles) : IWiseOldManClient
    {
        public Task<string?> GetMemberRoleAsync(string username, CancellationToken ct) =>
            Task.FromResult(roles.TryGetValue(username, out var role) ? role : null);
        public Task<string?> GetMemberRoleFreshAsync(string username, CancellationToken ct) => GetMemberRoleAsync(username, ct);
        public Task<IReadOnlyDictionary<string, string>> GetMemberRolesAsync(CancellationToken ct) => Task.FromResult(roles);
        public Task<bool> IsImpAccountAsync(string username, CancellationToken ct) => Task.FromResult(false);
        public Task<WomRoleUpdateResult> UpdateMemberRoleAsync(string username, string role, CancellationToken ct, bool invalidateCache = true) =>
            Task.FromResult(new WomRoleUpdateResult(true, 200, "ok", role, null, null));
        public Task<(bool Success, string Details)> RemoveMemberAsync(string username, CancellationToken ct) => Task.FromResult((true, "ok"));
        public Task InvalidateCacheAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ApprovedPlannerDiscord(
        IReadOnlyList<DiscordRoleObservation> roles,
        IReadOnlyList<DiscordMemberObservation> members) : IDiscordRoleGateway
    {
        public Task<DiscordGuildObservation?> GetGuildObservationAsync(CancellationToken ct) =>
            Task.FromResult<DiscordGuildObservation?>(new DiscordGuildObservation("guild", roles, true, null, members));
        public Task<DiscordRoleMutationResult> RenameRoleAsync(string roleId, string expectedName, string targetName, CancellationToken ct) =>
            Task.FromResult(new DiscordRoleMutationResult(true, 200, "ok", null));
        public Task<DiscordMemberObservation?> GetMemberObservationAsync(string userId, CancellationToken ct) =>
            Task.FromResult(members.FirstOrDefault(x => x.UserId == userId));
    }
}
