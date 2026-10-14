using Microsoft.EntityFrameworkCore;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.Core.Tests;

public class TrackerSyncServiceTests
{
    [Fact]
    public async Task SyncRosterAndQueueAsync_keeps_merge_review_open_when_new_name_is_missing_from_wom()
    {
        await using var db = CreateDbContext();
        var player = new Player
        {
            Username = "Wwarden",
            CurrentRank = "Officer",
            EligibleRank = "Officer",
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
            CurrentRank = "Officer",
            EligibleRank = "Officer",
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
            CurrentRank = "Recruit",
            EligibleRank = "Recruit",
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
    public async Task ProcessPlayerAsync_rechecks_active_player_rename_and_creates_merge_suggestion()
    {
        await using var db = CreateDbContext();
        var previousPlayer = new Player
        {
            Username = "Iron A3x",
            CurrentRank = "Officer",
            EligibleRank = "Officer",
            Status = PlayerStatus.MISSING_PENDING_REVIEW,
            LastSeen = DateTimeOffset.UtcNow.AddHours(-1)
        };
        var currentPlayer = new Player
        {
            Username = "Gynofobi",
            CurrentRank = "Officer",
            EligibleRank = "Officer",
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

    private sealed class FakeWiseOldManClient(IReadOnlyDictionary<string, string> roles) : IWiseOldManClient
    {
        public Task<string?> GetMemberRoleAsync(string username, CancellationToken ct) =>
            Task.FromResult(roles.TryGetValue(username, out var role) ? role : null);

        public Task<IReadOnlyDictionary<string, string>> GetMemberRolesAsync(CancellationToken ct) =>
            Task.FromResult(roles);

        public Task<bool> IsImpAccountAsync(string username, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<WomRoleUpdateResult> UpdateMemberRoleAsync(string username, string role, CancellationToken ct, bool invalidateCache = true) =>
            Task.FromResult(new WomRoleUpdateResult(true, 200, "ok", role, null, username));

        public Task<(bool Success, string Details)> RemoveMemberAsync(string username, CancellationToken ct) =>
            Task.FromResult((true, "ok"));

        public Task InvalidateCacheAsync(CancellationToken ct) =>
            Task.CompletedTask;
    }
}
