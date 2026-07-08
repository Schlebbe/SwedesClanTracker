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

    private static TrackerDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<TrackerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new TrackerDbContext(options);
    }

    private sealed class FakeTempleClient(IReadOnlyList<string> roster) : ITempleClient
    {
        public Task<IReadOnlyList<string>> GetRosterAsync(CancellationToken ct) => Task.FromResult(roster);

        public Task<PlayerStatsDto?> GetPlayerStatsAsync(string username, CancellationToken ct) =>
            Task.FromResult<PlayerStatsDto?>(null);

        public Task<int?> GetPetsAsync(string username, CancellationToken ct) =>
            Task.FromResult<int?>(null);
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
