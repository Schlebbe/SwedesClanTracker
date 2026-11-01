using Microsoft.EntityFrameworkCore;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.Core.Tests;

public class TrackerSyncServiceTests
{
    [Theory]
    [InlineData("rate-limit")]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("http-timeout")]
    public async Task SyncRosterAndQueueAsync_persists_temple_discovery_before_wom_failure(
        string failureKind)
    {
        await using var db = CreateDbContext();
        var originalLastSeen = DateTimeOffset.UtcNow.AddHours(-1);
        var existingPlayer = new Player
        {
            Id = 217,
            Username = "Muzmatch",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = PlayerStatus.ACTIVE,
            LastSeen = originalLastSeen
        };
        db.Players.Add(existingPlayer);
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = existingPlayer.Id,
            EventType = "WOM_RANK_MISMATCH_REQUIRED",
            MetadataJson = "{\"Username\":\"Muzmatch\",\"ExpectedRank\":\"Brassican\"}",
            Status = "OPEN",
            CreatedAt = originalLastSeen
        });
        await db.SaveChangesAsync();

        var womFailure = CreateWomFetchFailure(failureKind);
        var wom = new FakeWiseOldManClient(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            womFailure);
        var service = new TrackerSyncService(
            db,
            new FakeTempleClient(["Jay Hood"]),
            wom);

        var thrown = await Record.ExceptionAsync(
            () => service.SyncRosterAndQueueAsync(CancellationToken.None));

        Assert.NotNull(thrown);
        Assert.Equal(womFailure.GetType(), thrown!.GetType());
        Assert.Equal(1, wom.GetMemberRolesCalls);

        // The failed sync leaves the DbContext with tracked entities. Clear it
        // and requery so this assertion proves both rows were durably written.
        db.ChangeTracker.Clear();
        var discovered = await db.Players.SingleAsync(x => x.Username == "Jay Hood");
        Assert.Equal(PlayerStatus.NEW_PENDING_REVIEW, discovered.Status);
        var newPlayerEvents = await db.LifecycleEvents
            .Where(x => x.PlayerId == discovered.Id && x.EventType == "NEW_PLAYER")
            .ToListAsync();
        var newPlayerEvent = Assert.Single(newPlayerEvents);
        Assert.Equal("OPEN", newPlayerEvent.Status);

        var unchangedExisting = await db.Players.SingleAsync(x => x.Id == existingPlayer.Id);
        Assert.Equal(PlayerStatus.ACTIVE, unchangedExisting.Status);
        Assert.Equal("Brassican", unchangedExisting.CurrentRank);
        Assert.Equal("Brassican", unchangedExisting.EligibleRank);
        Assert.Equal(originalLastSeen, unchangedExisting.LastSeen);
        var unchangedReview = await db.LifecycleEvents.SingleAsync(x =>
            x.PlayerId == existingPlayer.Id && x.EventType == "WOM_RANK_MISMATCH_REQUIRED");
        Assert.Equal("OPEN", unchangedReview.Status);
        Assert.DoesNotContain(
            await db.LifecycleEvents.Select(x => x.EventType).ToListAsync(),
            x => x is "WOM_MISSING_ACTION_REQUIRED" or "WOM_ONLY_ACTION_REQUIRED");
    }

    [Fact]
    public async Task SyncRosterAndQueueAsync_success_queues_existing_members_before_new_discoveries()
    {
        await using var db = CreateDbContext();
        db.Players.Add(new Player
        {
            Username = "Zed",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = PlayerStatus.ACTIVE,
            LastSeen = DateTimeOffset.UtcNow.AddHours(-1),
            LastSynced = DateTimeOffset.UtcNow.AddHours(-1)
        });
        await db.SaveChangesAsync();

        var service = new TrackerSyncService(
            db,
            new FakeTempleClient(["Zed", "Jay_Hood", " Aaron "]),
            new FakeWiseOldManClient(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Zed"] = "brassican",
                ["Jay Hood"] = "member",
                ["Aaron"] = "member"
            }));

        var queue = await service.SyncRosterAndQueueAsync(CancellationToken.None);

        var queuedNames = queue
            .Select(id => db.Players.Single(x => x.Id == id).Username)
            .ToArray();
        Assert.Equal(["Zed", "Aaron", "Jay Hood"], queuedNames);
        Assert.Equal(2, await db.LifecycleEvents.CountAsync(x => x.EventType == "NEW_PLAYER"));
    }

    [Fact]
    public async Task SyncRosterAndQueueAsync_recovery_queues_persisted_temple_discoveries_without_duplicates()
    {
        await using var db = CreateDbContext();
        var wom = new MutableWiseOldManClient
        {
            GetMemberRolesException = new WiseOldManRateLimitException(
                DateTimeOffset.UtcNow.AddMinutes(1),
                "Wise Old Man is cooling down.")
        };
        var temple = new FakeTempleClient([" Muzmatch ", "Jay_Hood", "Jay Hood", "muzmatch"]);
        var service = new TrackerSyncService(db, temple, wom);

        await Assert.ThrowsAsync<WiseOldManRateLimitException>(
            () => service.SyncRosterAndQueueAsync(CancellationToken.None));

        db.ChangeTracker.Clear();
        var persistedPlayers = await db.Players.OrderBy(x => x.Username).ToListAsync();
        Assert.Equal(["Jay Hood", "Muzmatch"], persistedPlayers.Select(x => x.Username));
        Assert.Equal(2, await db.LifecycleEvents.CountAsync(x => x.EventType == "NEW_PLAYER"));

        wom.GetMemberRolesException = null;
        wom.Roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Jay Hood"] = "member",
            ["Muzmatch"] = "brassican"
        };

        var queue = await service.SyncRosterAndQueueAsync(CancellationToken.None);

        db.ChangeTracker.Clear();
        var queuedNames = queue
            .Select(id => db.Players.Single(x => x.Id == id).Username)
            .ToArray();
        Assert.Equal(["Jay Hood", "Muzmatch"], queuedNames);
        Assert.Equal(2, await db.Players.CountAsync());
        Assert.Equal(2, await db.LifecycleEvents.CountAsync(x => x.EventType == "NEW_PLAYER"));
        Assert.All(
            await db.LifecycleEvents.Where(x => x.EventType == "NEW_PLAYER").ToListAsync(),
            x => Assert.Equal("OPEN", x.Status));
        Assert.All(
            await db.Players.ToListAsync(),
            x => Assert.Equal(PlayerStatus.NEW_PENDING_REVIEW, x.Status));
    }

    [Fact]
    public async Task SyncRosterAndQueueAsync_propagates_wom_cancellation_after_temple_discovery_is_saved()
    {
        await using var db = CreateDbContext();
        using var cancellation = new CancellationTokenSource();
        var wom = new CancelingWiseOldManClient(cancellation);
        var service = new TrackerSyncService(
            db,
            new FakeTempleClient(["Jay Hood"]),
            wom);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SyncRosterAndQueueAsync(cancellation.Token));

        db.ChangeTracker.Clear();
        Assert.True(await db.Players.AnyAsync(x => x.Username == "Jay Hood"));
        Assert.True(await db.LifecycleEvents.AnyAsync(x =>
            x.EventType == "NEW_PLAYER" && x.Status == "OPEN"));
        Assert.Equal(1, wom.GetMemberRolesCalls);
    }

    [Fact]
    public async Task SyncRosterAndQueueAsync_keeps_merge_review_open_when_new_name_is_missing_from_wom()
    {
        await using var db = CreateDbContext();
        var player = new Player
        {
            Username = "Wwarden",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = PlayerStatus.MERGE_SUGGESTED,
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-10)
        };
        db.Players.Add(player);
        await db.SaveChangesAsync();
        db.LifecycleEvents.AddRange(
            new LifecycleEvent
            {
                PlayerId = player.Id,
                EventType = "MERGE_SUGGESTED",
                MetadataJson = JsonUtil.Serialize(new { NewPlayer = "Wwarden", SuggestedPrevious = "Lsk Warden" }),
                Status = "OPEN",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            },
            new LifecycleEvent
            {
                PlayerId = player.Id,
                EventType = "MERGE_ACTION_REQUIRED",
                MetadataJson = JsonUtil.Serialize(new { NewPlayer = "Wwarden", SuggestedPrevious = "Lsk Warden" }),
                Status = "OPEN",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
        await db.SaveChangesAsync();

        var service = new TrackerSyncService(
            db,
            new FakeTempleClient(["Wwarden"]),
            new FakeWiseOldManClient(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));

        await service.SyncRosterAndQueueAsync(CancellationToken.None);

        var updatedPlayer = await db.Players.SingleAsync();
        var openEvents = await db.LifecycleEvents
            .Where(x => x.Status == "OPEN")
            .Select(x => x.EventType)
            .OrderBy(x => x)
            .ToListAsync();

        Assert.Equal(PlayerStatus.MERGE_SUGGESTED, updatedPlayer.Status);
        Assert.Contains("MERGE_SUGGESTED", openEvents);
        Assert.Contains("MERGE_ACTION_REQUIRED", openEvents);
        Assert.DoesNotContain("WOM_MISSING_ACTION_REQUIRED", openEvents);
    }

    [Fact]
    public async Task SyncRosterAndQueueAsync_ignores_unknown_wom_roles_for_wom_only_and_rank_mismatch_reviews()
    {
        await using var db = CreateDbContext();
        var player = new Player
        {
            Username = "Wwarden",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = PlayerStatus.ACTIVE,
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-10)
        };
        db.Players.Add(player);
        await db.SaveChangesAsync();
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = player.Id,
            EventType = "WOM_RANK_MISMATCH_REQUIRED",
            MetadataJson = JsonUtil.Serialize(new { player.Username, ExpectedRank = player.CurrentRank, ActualWomRole = "goblin" }),
            Status = "OPEN",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        await db.SaveChangesAsync();

        var service = new TrackerSyncService(
            db,
            new FakeTempleClient(["Wwarden"]),
            new FakeWiseOldManClient(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Wwarden"] = "goblin",
                ["Untracked"] = "future wom role"
            }));

        await service.SyncRosterAndQueueAsync(CancellationToken.None);

        var events = await db.LifecycleEvents
            .Select(x => new { x.EventType, x.Status })
            .ToListAsync();

        Assert.Contains(events, x => x.EventType == "WOM_RANK_MISMATCH_REQUIRED" && x.Status == "DONE");
        Assert.DoesNotContain(events, x => x.EventType == "WOM_ONLY_ACTION_REQUIRED" && x.Status == "OPEN");
    }

    [Fact]
    public async Task ProcessPlayerAsync_does_not_create_promotion_for_unknown_wom_role()
    {
        await using var db = CreateDbContext();
        var player = new Player
        {
            Username = "Wwarden",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = PlayerStatus.ACTIVE,
            LastSeen = DateTimeOffset.UtcNow
        };
        db.Players.Add(player);
        await db.SaveChangesAsync();

        var service = new TrackerSyncService(
            db,
            new FakeTempleClient([], new PlayerStatsDto(2100, 0, 0, 0)),
            new FakeWiseOldManClient(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Wwarden"] = "goblin"
            }));

        await service.ProcessPlayerAsync(player.Id, CancellationToken.None);

        Assert.Empty(await db.PromotionCandidates.ToListAsync());
    }

    [Fact]
    public async Task ProcessPlayerAsync_persists_database_only_promotion_classification()
    {
        await using var db = CreateDbContext();
        var player = new Player
        {
            Username = "Gynofobi",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = PlayerStatus.ACTIVE,
            LastSeen = DateTimeOffset.UtcNow
        };
        db.Players.Add(player);
        await db.SaveChangesAsync();

        var service = new TrackerSyncService(
            db,
            new FakeTempleClient([], new PlayerStatsDto(2200, 0, 0, 0)),
            new FakeWiseOldManClient(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Gynofobi"] = "Commander"
            }));

        await service.ProcessPlayerAsync(player.Id, CancellationToken.None);

        var candidate = await db.PromotionCandidates.SingleAsync();
        Assert.Equal(PromotionCandidateType.wom_already_at_new_rank, candidate.CandidateType);
        Assert.Equal("Commander", candidate.WomRoleAtEvaluation);
    }

    [Fact]
    public async Task SyncRosterAndQueueAsync_creates_rank_mismatch_after_database_only_rank_is_applied()
    {
        await using var db = CreateDbContext();
        var player = new Player
        {
            Username = "Gynofobi",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = PlayerStatus.ACTIVE,
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-10)
        };
        db.Players.Add(player);
        await db.SaveChangesAsync();

        var service = new TrackerSyncService(
            db,
            new FakeTempleClient(["Gynofobi"]),
            new FakeWiseOldManClient(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Gynofobi"] = "Member"
            }));

        await service.SyncRosterAndQueueAsync(CancellationToken.None);

        Assert.Contains(
            await db.LifecycleEvents.ToListAsync(),
            x => x.EventType == "WOM_RANK_MISMATCH_REQUIRED" && x.Status == "OPEN");
    }

    [Fact]
    public async Task SyncRosterAndQueueAsync_keeps_legacy_recruit_protected_from_member_mismatch()
    {
        await using var db = CreateDbContext();
        var player = new Player
        {
            Username = "LegacyRecruit",
            CurrentRank = "Recruit",
            EligibleRank = "Brassican",
            Status = PlayerStatus.ACTIVE,
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-10)
        };
        db.Players.Add(player);
        await db.SaveChangesAsync();

        var service = new TrackerSyncService(
            db,
            new FakeTempleClient(["LegacyRecruit"]),
            new FakeWiseOldManClient(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["LegacyRecruit"] = "Member"
            }));

        await service.SyncRosterAndQueueAsync(CancellationToken.None);

        Assert.DoesNotContain(
            await db.LifecycleEvents.ToListAsync(),
            x => x.EventType == "WOM_RANK_MISMATCH_REQUIRED" && x.Status == "OPEN");
    }

    [Fact]
    public async Task ProcessPlayerAsync_keeps_current_colonel_when_sync_eligibility_is_general()
    {
        await using var db = CreateDbContext();
        var player = new Player
        {
            Username = "ColonelMember",
            CurrentRank = "Colonel",
            EligibleRank = "General",
            Status = PlayerStatus.ACTIVE,
            LastSeen = DateTimeOffset.UtcNow
        };
        db.Players.Add(player);
        await db.SaveChangesAsync();

        var service = new TrackerSyncService(
            db,
            new FakeTempleClient([], new PlayerStatsDto(0, 1250, 0, 0), 0),
            new FakeWiseOldManClient(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ColonelMember"] = "Colonel"
            }));

        await service.ProcessPlayerAsync(player.Id, CancellationToken.None);

        var persisted = await db.Players.SingleAsync();
        Assert.Equal("Colonel", persisted.CurrentRank);
        Assert.Equal("General", persisted.EligibleRank);
        Assert.Empty(await db.PromotionCandidates.ToListAsync());
    }

    [Fact]
    public async Task ProcessPlayerAsync_rechecks_active_player_rename_and_creates_merge_suggestion()
    {
        await using var db = CreateDbContext();
        var previousPlayer = new Player
        {
            Username = "Iron A3x",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = PlayerStatus.MISSING_PENDING_REVIEW,
            LastSeen = DateTimeOffset.UtcNow.AddHours(-1)
        };
        var currentPlayer = new Player
        {
            Username = "Gynofobi",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = PlayerStatus.ACTIVE,
            LastSeen = DateTimeOffset.UtcNow
        };
        db.Players.AddRange(previousPlayer, currentPlayer);
        await db.SaveChangesAsync();
        db.PlayerSnapshots.Add(new PlayerSnapshot
        {
            PlayerId = previousPlayer.Id,
            Timestamp = DateTimeOffset.UtcNow.AddHours(-1),
            TotalLevel = 2088,
            Ehb = 299.0733,
            Ehp = 627.7743,
            Collections = 325,
            PetCount = 4
        });
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = currentPlayer.Id,
            EventType = RenameReviewEventTypes.RecheckRequested,
            MetadataJson = JsonUtil.Serialize(new { currentPlayer.Username, Source = "discord-slash-update" }),
            Status = "OPEN",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var service = new TrackerSyncService(
            db,
            new FakeTempleClient([], new PlayerStatsDto(2088, 287.8573, 627.7743, 325), 4),
            new FakeWiseOldManClient(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));

        await service.ProcessPlayerAsync(currentPlayer.Id, CancellationToken.None);

        var updatedPlayer = await db.Players.SingleAsync(x => x.Id == currentPlayer.Id);
        var mergeEvent = await db.LifecycleEvents.SingleAsync(x =>
            x.PlayerId == currentPlayer.Id && x.EventType == "MERGE_ACTION_REQUIRED");
        var recheckEvent = await db.LifecycleEvents.SingleAsync(x =>
            x.PlayerId == currentPlayer.Id && x.EventType == RenameReviewEventTypes.RecheckRequested);

        Assert.Equal(PlayerStatus.MERGE_SUGGESTED, updatedPlayer.Status);
        Assert.Equal("OPEN", mergeEvent.Status);
        Assert.Contains("Iron A3x", mergeEvent.MetadataJson);
        Assert.Equal("DONE", recheckEvent.Status);
    }

    private static Exception CreateWomFetchFailure(string failureKind) =>
        failureKind switch
        {
            "rate-limit" => new WiseOldManRateLimitException(
                DateTimeOffset.UtcNow.AddMinutes(1),
                "Wise Old Man is cooling down."),
            "http" => new HttpRequestException("Wise Old Man is unavailable."),
            "timeout" => new TimeoutException("Wise Old Man timed out."),
            "http-timeout" => new TaskCanceledException(
                "Wise Old Man HTTP request timed out.",
                new TimeoutException("Wise Old Man request timed out.")),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind), failureKind, "Unknown WOM failure kind.")
        };

    private static TrackerDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<TrackerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new TrackerDbContext(options);
    }

    private sealed class FakeTempleClient(IReadOnlyList<string> roster, PlayerStatsDto? stats = null, int? pets = null) : ITempleClient
    {
        public Task<IReadOnlyList<string>> GetRosterAsync(CancellationToken ct) => Task.FromResult(roster);

        public Task<PlayerStatsDto?> GetPlayerStatsAsync(string username, CancellationToken ct) =>
            Task.FromResult(stats);

        public Task<int?> GetPetsAsync(string username, CancellationToken ct) =>
            Task.FromResult(pets);
    }

    private sealed class FakeWiseOldManClient(
        IReadOnlyDictionary<string, string> roles,
        Exception? getMemberRolesException = null) : IWiseOldManClient
    {
        public int GetMemberRolesCalls { get; private set; }

        public Task<string?> GetMemberRoleAsync(string username, CancellationToken ct) =>
            Task.FromResult(roles.TryGetValue(username, out var role) ? role : null);

        public Task<IReadOnlyDictionary<string, string>> GetMemberRolesAsync(CancellationToken ct)
        {
            GetMemberRolesCalls++;
            return getMemberRolesException is null
                ? Task.FromResult(roles)
                : Task.FromException<IReadOnlyDictionary<string, string>>(getMemberRolesException);
        }

        public Task<bool> IsImpAccountAsync(string username, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<WomRoleUpdateResult> UpdateMemberRoleAsync(string username, string role, CancellationToken ct, bool invalidateCache = true) =>
            Task.FromResult(new WomRoleUpdateResult(true, 200, "ok", role, null, username));

        public Task<(bool Success, string Details)> RemoveMemberAsync(string username, CancellationToken ct) =>
            Task.FromResult((true, "ok"));

        public Task InvalidateCacheAsync(CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class MutableWiseOldManClient : IWiseOldManClient
    {
        public IReadOnlyDictionary<string, string> Roles { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Exception? GetMemberRolesException { get; set; }
        public int GetMemberRolesCalls { get; private set; }

        public Task<string?> GetMemberRoleAsync(string username, CancellationToken ct) =>
            Task.FromResult(Roles.TryGetValue(username, out var role) ? role : null);

        public Task<IReadOnlyDictionary<string, string>> GetMemberRolesAsync(CancellationToken ct)
        {
            GetMemberRolesCalls++;
            return GetMemberRolesException is null
                ? Task.FromResult(Roles)
                : Task.FromException<IReadOnlyDictionary<string, string>>(GetMemberRolesException);
        }

        public Task<bool> IsImpAccountAsync(string username, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<WomRoleUpdateResult> UpdateMemberRoleAsync(
            string username,
            string role,
            CancellationToken ct,
            bool invalidateCache = true) =>
            Task.FromResult(new WomRoleUpdateResult(true, 200, "ok", role, null, username));

        public Task<(bool Success, string Details)> RemoveMemberAsync(string username, CancellationToken ct) =>
            Task.FromResult((true, "ok"));

        public Task InvalidateCacheAsync(CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class CancelingWiseOldManClient(CancellationTokenSource cancellation) : IWiseOldManClient
    {
        public int GetMemberRolesCalls { get; private set; }

        public Task<string?> GetMemberRoleAsync(string username, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyDictionary<string, string>> GetMemberRolesAsync(CancellationToken ct)
        {
            GetMemberRolesCalls++;
            cancellation.Cancel();
            return Task.FromCanceled<IReadOnlyDictionary<string, string>>(cancellation.Token);
        }

        public Task<bool> IsImpAccountAsync(string username, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<WomRoleUpdateResult> UpdateMemberRoleAsync(
            string username,
            string role,
            CancellationToken ct,
            bool invalidateCache = true) =>
            Task.FromResult(new WomRoleUpdateResult(true, 200, "ok", role, null, username));

        public Task<(bool Success, string Details)> RemoveMemberAsync(string username, CancellationToken ct) =>
            Task.FromResult((true, "ok"));

        public Task InvalidateCacheAsync(CancellationToken ct) =>
            Task.CompletedTask;
    }
}
