using Microsoft.EntityFrameworkCore;
using SwedesClanTracker.Core;
using SwedesClanTracker.RankTransition;

namespace SwedesClanTracker.RankTransition.Tests;

public sealed class RankTransitionSafetyTests
{
    [Fact]
    public async Task Ignored_wom_mismatch_preserves_ignore_and_suppresses_promotion()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var player = AddPlayer(db, 1, "Ignored", "Brassican", "Brassican", Snapshot(now));
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = player.Id,
            EventType = "WOM_RANK_MISMATCH_IGNORED",
            MetadataJson = "{\"Username\":\"Ignored\"}",
            Status = "OPEN",
            CreatedAt = now
        });
        await db.SaveChangesAsync();

        var manifest = await PlanAsync(
            db,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Ignored"] = "Officer"
            },
            now);

        var wom = manifest.WomObservations.Single(x => x.Username == "Ignored");
        Assert.True(wom.IgnoredMismatch);
        Assert.Equal("Brassican", wom.RoleTarget);
        Assert.Contains(manifest.Operations, x =>
            x.Kind == TransitionOperationKind.WiseOldManRole && x.TargetKey == "Ignored" && x.After == "Brassican");
        Assert.DoesNotContain(manifest.Operations, x =>
            x.Kind == TransitionOperationKind.QueuePromotionCandidate && x.TargetKey == "1");
    }

    [Fact]
    public async Task Special_wom_role_does_not_queue_promotion_or_create_external_alignment()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        AddPlayer(db, 1, "Special", "Brassican", "Brassican", Snapshot(now, totalLevel: 2200));
        await db.SaveChangesAsync();

        var manifest = await PlanAsync(
            db,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Special"] = "Recruit"
            },
            now);

        var wom = manifest.WomObservations.Single(x => x.Username == "Special");
        Assert.True(wom.Protected);
        Assert.DoesNotContain(manifest.PromotionDecisions, x => x.PlayerId == 1);
        Assert.DoesNotContain(manifest.Operations, x =>
            x.Kind == TransitionOperationKind.QueuePromotionCandidate && x.TargetKey == "1");
        Assert.DoesNotContain(manifest.Operations, x =>
            x.Kind == TransitionOperationKind.WiseOldManRole && x.TargetKey == "Special");
    }

    [Fact]
    public async Task Stale_snapshot_does_not_queue_promotion()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        AddPlayer(db, 1, "Stale", "Brassican", "Brassican", Snapshot(
            now.Subtract(TimeSpan.FromDays(2)),
            totalLevel: 2200));
        await db.SaveChangesAsync();

        var manifest = await PlanAsync(
            db,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Stale"] = "Officer"
            },
            now);

        Assert.Contains(manifest.Warnings, x => x.Code == "STALE_PLAYER_SNAPSHOT" && x.Subject == "Stale");
        Assert.DoesNotContain(manifest.PromotionDecisions, x => x.PlayerId == 1);
        Assert.DoesNotContain(manifest.Operations, x =>
            x.Kind == TransitionOperationKind.QueuePromotionCandidate && x.TargetKey == "1");
    }

    [Fact]
    public async Task Missing_wom_identity_is_flagged_and_holds_external_and_promotion_work()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        AddPlayer(db, 1, "MissingFromWom", "Brassican", "Brassican", Snapshot(now, collections: 900));
        await db.SaveChangesAsync();

        var manifest = await PlanAsync(db, new Dictionary<string, string>(), now);

        Assert.Contains(manifest.Warnings, x => x.Code == "WOM_IDENTITY_MISSING" && x.Subject == "MissingFromWom");
        Assert.DoesNotContain(manifest.Operations, x =>
            x.TargetKey == "MissingFromWom" ||
            x.Kind == TransitionOperationKind.QueuePromotionCandidate && x.TargetKey == "1");
    }

    [Fact]
    public async Task Ambiguous_database_username_is_reviewed_without_guessing_external_owner()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        AddPlayer(db, 1, "SameName", "Officer", "Officer", Snapshot(now));
        AddPlayer(db, 2, "samename", "Officer", "Officer", Snapshot(now));
        await db.SaveChangesAsync();

        var manifest = await PlanAsync(
            db,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SameName"] = "Officer"
            },
            now);

        Assert.Contains(manifest.Warnings, x => x.Code == "AMBIGUOUS_PLAYER_IDENTITY" && x.BlocksApply);
        Assert.DoesNotContain(manifest.Operations, x => x.IsExternal);
        Assert.DoesNotContain(manifest.Operations, x => x.Kind == TransitionOperationKind.QueuePromotionCandidate);
    }

    [Fact]
    public async Task Missing_snapshot_holds_eligibility_instead_of_legacy_remapping_it()
    {
        await using var db = CreateDb();
        var player = new Player
        {
            Id = 1,
            Username = "NoSnapshot",
            CurrentRank = "Officer",
            EligibleRank = "Astral",
            Status = PlayerStatus.ACTIVE,
            LastSeen = DateTimeOffset.UtcNow
        };
        db.Players.Add(player);
        await db.SaveChangesAsync();

        var manifest = await PlanAsync(
            db,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["NoSnapshot"] = "Officer"
            },
            DateTimeOffset.UtcNow);

        var observation = Assert.Single(manifest.Players);
        Assert.Equal("Brassican", observation.DatabaseCurrentRankTarget);
        Assert.Equal("Astral", observation.DatabaseEligibleRankTarget);
        Assert.Contains(manifest.Warnings, x => x.Code == "NO_PLAYER_SNAPSHOT");
        Assert.DoesNotContain(manifest.Operations, x => x.Kind == TransitionOperationKind.DatabaseEligibleRank);
        Assert.DoesNotContain(manifest.Operations, x => x.Kind == TransitionOperationKind.QueuePromotionCandidate);
    }

    [Fact]
    public async Task Canonical_general_is_not_double_mapped_to_colonel()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        AddPlayer(db, 1, "CanonicalGeneral", "General", "General", Snapshot(now, ehp: 1750));
        await db.SaveChangesAsync();

        var manifest = await PlanAsync(
            db,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["CanonicalGeneral"] = "General"
            },
            now);

        var player = manifest.Players.Single(x => x.PlayerId == 1);
        // The standing database value is still the frozen pre-cutover General,
        // so it must move once to Colonel.  The freshly evaluated canonical
        // rank is already General and must not be mapped a second time.
        Assert.Equal("Colonel", player.DatabaseCurrentRankTarget);
        Assert.Equal("General", player.DatabaseEligibleRankTarget);
        Assert.Equal("General", player.EvaluatedEligibleRank);
        Assert.Contains(manifest.Operations, x =>
            x.Kind == TransitionOperationKind.DatabaseCurrentRank &&
            x.TargetKey == "1" && x.Before == "General" && x.After == "Colonel");
        Assert.Contains(manifest.Operations, x =>
            x.Kind == TransitionOperationKind.WiseOldManRole &&
            x.TargetKey == "CanonicalGeneral" && x.Before == "General" && x.After == "Colonel");
    }

    [Fact]
    public async Task Every_pre_cutover_pending_candidate_is_superseded_and_only_current_upgrades_are_requeued()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var legacy = AddPlayer(db, 1, "Legacy", "Officer", "Officer", Snapshot(now, ehp: 1750));
        var canonical = AddPlayer(db, 2, "Canonical", "Brassican", "Brassican", Snapshot(now, ehp: 1750));
        var unchangedName = AddPlayer(db, 3, "CaptainAdmiral", "Captain", "Admiral", Snapshot(now, petCount: 40));
        var oldCandidate = new PromotionCandidate
        {
            PlayerId = legacy.Id,
            Player = legacy,
            OldRank = "Officer",
            NewRank = "Astral",
            Reason = "legacy policy",
            Status = PromotionStatus.PENDING,
            CreatedAt = now
        };
        var canonicalCandidate = new PromotionCandidate
        {
            PlayerId = canonical.Id,
            Player = canonical,
            OldRank = "Brassican",
            NewRank = "General",
            Reason = "current policy",
            Status = PromotionStatus.PENDING,
            CreatedAt = now
        };
        var unchangedNameCandidate = new PromotionCandidate
        {
            PlayerId = unchangedName.Id,
            Player = unchangedName,
            OldRank = "Captain",
            NewRank = "Admiral",
            Reason = "old 40-pet Admiral policy",
            Status = PromotionStatus.PENDING,
            CreatedAt = now
        };
        db.PromotionCandidates.AddRange(oldCandidate, canonicalCandidate, unchangedNameCandidate);
        await db.SaveChangesAsync();

        var manifest = await PlanAsync(
            db,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Legacy"] = "Officer",
                ["Canonical"] = "Brassican",
                ["CaptainAdmiral"] = "Captain"
            },
            now);

        var supersede = Assert.Single(manifest.Operations, x =>
            x.Kind == TransitionOperationKind.SupersedePromotionCandidate &&
            x.TargetKey == oldCandidate.Id.ToString());
        Assert.Equal(PromotionStatus.DISMISSED.ToString(), supersede.After);
        var canonicalSupersede = Assert.Single(manifest.Operations, x =>
            x.Kind == TransitionOperationKind.SupersedePromotionCandidate &&
            x.TargetKey == canonicalCandidate.Id.ToString());
        Assert.Equal(PromotionStatus.DISMISSED.ToString(), canonicalSupersede.After);
        var queue = Assert.Single(manifest.Operations, x =>
            x.Kind == TransitionOperationKind.QueuePromotionCandidate &&
            x.TargetKey == legacy.Id.ToString());
        Assert.Equal("General", queue.After);
        Assert.Contains(manifest.Operations, x =>
            x.Kind == TransitionOperationKind.QueuePromotionCandidate &&
            x.TargetKey == canonical.Id.ToString() &&
            x.After == "General");

        var unchangedNameSupersede = Assert.Single(manifest.Operations, x =>
            x.Kind == TransitionOperationKind.SupersedePromotionCandidate &&
            x.TargetKey == unchangedNameCandidate.Id.ToString());
        Assert.Equal(PromotionStatus.DISMISSED.ToString(), unchangedNameSupersede.After);
        var unchangedNameObservation = manifest.Players.Single(x => x.PlayerId == unchangedName.Id);
        Assert.Equal("Brigadier", unchangedNameObservation.EvaluatedEligibleRank);
        Assert.DoesNotContain(manifest.Operations, x =>
            x.Kind == TransitionOperationKind.QueuePromotionCandidate &&
            x.TargetKey == unchangedName.Id.ToString() &&
            x.After == "Admiral");
        Assert.Contains(manifest.Operations, x =>
            x.Kind == TransitionOperationKind.QueuePromotionCandidate &&
            x.TargetKey == unchangedName.Id.ToString() &&
            x.After == "Brigadier");

        var ordered = manifest.Operations.ToArray();
        var lastSupersedeIndex = Array.FindLastIndex(
            ordered,
            x => x.Kind == TransitionOperationKind.SupersedePromotionCandidate);
        var firstQueueIndex = Array.FindIndex(
            ordered,
            x => x.Kind == TransitionOperationKind.QueuePromotionCandidate);
        Assert.True(lastSupersedeIndex >= 0 && firstQueueIndex > lastSupersedeIndex);
    }

    private static async Task<RankTransitionManifest> PlanAsync(
        TrackerDbContext db,
        IReadOnlyDictionary<string, string> womRoles,
        DateTimeOffset now)
    {
        var planner = new RankTransitionPlanner(
            db,
            new FakeWom(womRoles),
            discordRoleGateway: null,
            clock: new FixedClock(now));

        return await planner.PlanAsync(new RankTransitionPlannerOptions(
            IncludeDiscord: false,
            IncludeWom: true));
    }

    private static Player AddPlayer(
        TrackerDbContext db,
        int id,
        string username,
        string currentRank,
        string eligibleRank,
        PlayerSnapshot snapshot)
    {
        var player = new Player
        {
            Id = id,
            Username = username,
            CurrentRank = currentRank,
            EligibleRank = eligibleRank,
            Status = PlayerStatus.ACTIVE,
            LastSeen = snapshot.Timestamp,
            LastSynced = snapshot.Timestamp
        };
        snapshot.PlayerId = id;
        snapshot.Player = player;
        player.Snapshots.Add(snapshot);
        db.Players.Add(player);
        return player;
    }

    private static PlayerSnapshot Snapshot(
        DateTimeOffset timestamp,
        int totalLevel = 0,
        double ehb = 0,
        double ehp = 0,
        int collections = 0,
        int petCount = 0) => new()
    {
        Timestamp = timestamp,
        TotalLevel = totalLevel,
        Ehb = ehb,
        Ehp = ehp,
        Collections = collections,
        PetCount = petCount
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

        public Task<IReadOnlyDictionary<string, string>> GetMemberRolesAsync(CancellationToken ct) =>
            Task.FromResult(roles);

        public Task<bool> IsImpAccountAsync(string username, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<WomRoleUpdateResult> UpdateMemberRoleAsync(
            string username,
            string role,
            CancellationToken ct,
            bool invalidateCache = true) =>
            Task.FromResult(new WomRoleUpdateResult(true, 200, "ok", role, null, null));

        public Task<(bool Success, string Details)> RemoveMemberAsync(string username, CancellationToken ct) =>
            Task.FromResult((true, "ok"));

        public Task InvalidateCacheAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
