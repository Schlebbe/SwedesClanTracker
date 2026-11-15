using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.Core.Tests;

public class MergeReviewServiceTests
{
    [Fact]
    public async Task AbortAsync_restores_one_missing_review_pair_for_suggested_previous_player()
    {
        await using var db = CreateDbContext();
        var previousPlayer = new Player
        {
            Username = "Yntg",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = PlayerStatus.MISSING_PENDING_REVIEW,
            LastSeen = DateTimeOffset.UtcNow.AddHours(-1)
        };
        var currentPlayer = new Player
        {
            Username = "Lindal",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = PlayerStatus.MERGE_SUGGESTED,
            LastSeen = DateTimeOffset.UtcNow
        };
        db.Players.AddRange(previousPlayer, currentPlayer);
        await db.SaveChangesAsync();
        db.LifecycleEvents.AddRange(
            CreateMergeEvent(currentPlayer.Id, "MERGE_SUGGESTED", previousPlayer.Username),
            CreateMergeEvent(currentPlayer.Id, "MERGE_ACTION_REQUIRED", previousPlayer.Username));
        await db.SaveChangesAsync();

        var service = new MergeReviewService(db, new FakeWiseOldManClient());

        var result = await service.AbortAsync(
            currentPlayer.Id,
            "tester",
            "test",
            CancellationToken.None);

        Assert.True(result.Success);
        db.ChangeTracker.Clear();

        var persistedPrevious = await db.Players.SingleAsync(x => x.Id == previousPlayer.Id);
        var persistedCurrent = await db.Players.SingleAsync(x => x.Id == currentPlayer.Id);
        Assert.Equal(PlayerStatus.MISSING_PENDING_REVIEW, persistedPrevious.Status);
        Assert.Equal(PlayerStatus.ACTIVE, persistedCurrent.Status);

        var restoredEvents = await db.LifecycleEvents
            .Where(x => x.PlayerId == previousPlayer.Id && x.Status == "OPEN")
            .OrderBy(x => x.EventType)
            .ToListAsync();
        Assert.Equal(["MISSING_IN_ROSTER", "TEMPLE_MISSING_ACTION_REQUIRED"], restoredEvents.Select(x => x.EventType));

        Assert.DoesNotContain(
            await db.LifecycleEvents.Where(x => x.PlayerId == currentPlayer.Id).ToListAsync(),
            x => x.Status == "OPEN" && x.EventType is "MERGE_SUGGESTED" or "MERGE_ACTION_REQUIRED");
        var applied = await db.LifecycleEvents.SingleAsync(x =>
            x.PlayerId == currentPlayer.Id && x.EventType == "MERGE_ACTION_APPLIED");
        Assert.Equal("OPEN", applied.Status);
        Assert.Contains("\"Action\":\"abort\"", applied.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReassignAsync_releases_original_suggested_previous_player_with_one_missing_review_pair()
    {
        await using var db = CreateDbContext();
        var suggestedPrevious = new Player
        {
            Username = "Yntg",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = PlayerStatus.MISSING_PENDING_REVIEW,
            LastSeen = DateTimeOffset.UtcNow.AddHours(-1)
        };
        var selectedPrevious = new Player
        {
            Username = "Other Old",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = PlayerStatus.MISSING_PENDING_REVIEW,
            LastSeen = DateTimeOffset.UtcNow.AddHours(-1)
        };
        var currentPlayer = new Player
        {
            Username = "Lindal",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = PlayerStatus.MERGE_SUGGESTED,
            LastSeen = DateTimeOffset.UtcNow
        };
        db.Players.AddRange(suggestedPrevious, selectedPrevious, currentPlayer);
        await db.SaveChangesAsync();
        db.LifecycleEvents.AddRange(
            CreateMergeEvent(currentPlayer.Id, "MERGE_SUGGESTED", suggestedPrevious.Username),
            CreateMergeEvent(currentPlayer.Id, "MERGE_ACTION_REQUIRED", suggestedPrevious.Username));
        await db.SaveChangesAsync();

        var service = new MergeReviewService(db, new FakeWiseOldManClient());

        var result = await service.ReassignAsync(
            currentPlayer.Id,
            selectedPrevious.Username,
            "tester",
            "test",
            CancellationToken.None);

        Assert.True(result.Success);
        db.ChangeTracker.Clear();

        Assert.DoesNotContain(await db.Players.ToListAsync(), x => x.Id == currentPlayer.Id);
        var canonicalPlayer = await db.Players.SingleAsync(x => x.Id == selectedPrevious.Id);
        Assert.Equal("Lindal", canonicalPlayer.Username);
        Assert.Equal(PlayerStatus.ACTIVE, canonicalPlayer.Status);

        var releasedPlayer = await db.Players.SingleAsync(x => x.Id == suggestedPrevious.Id);
        Assert.Equal(PlayerStatus.MISSING_PENDING_REVIEW, releasedPlayer.Status);
        var releasedEvents = await db.LifecycleEvents
            .Where(x => x.PlayerId == releasedPlayer.Id && x.Status == "OPEN")
            .OrderBy(x => x.EventType)
            .ToListAsync();
        Assert.Equal(["MISSING_IN_ROSTER", "TEMPLE_MISSING_ACTION_REQUIRED"], releasedEvents.Select(x => x.EventType));

        var transferredMergeEvents = await db.LifecycleEvents
            .Where(x => x.PlayerId == canonicalPlayer.Id &&
                        (x.EventType == "MERGE_SUGGESTED" || x.EventType == "MERGE_ACTION_REQUIRED"))
            .ToListAsync();
        Assert.NotEmpty(transferredMergeEvents);
        Assert.All(transferredMergeEvents, x => Assert.Equal("DONE", x.Status));
        var openReferences = await MergeReviewPolicy.GetOpenReferencesAsync(db, CancellationToken.None);
        Assert.DoesNotContain(openReferences, x =>
            x.SuggestedPreviousPlayerId == releasedPlayer.Id ||
            string.Equals(x.SuggestedPreviousUsername, releasedPlayer.Username, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConfirmSuggestedAsync_closes_transferred_requirements_and_preserves_existing_missing_event_ids()
    {
        await using var db = CreateDbContext();
        var previousPlayer = new Player
        {
            Username = "Yntg",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = PlayerStatus.MISSING_PENDING_REVIEW,
            LastSeen = DateTimeOffset.UtcNow.AddHours(-1)
        };
        var currentPlayer = new Player
        {
            Username = "Lindal",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = PlayerStatus.MERGE_SUGGESTED,
            LastSeen = DateTimeOffset.UtcNow
        };
        db.Players.AddRange(previousPlayer, currentPlayer);
        await db.SaveChangesAsync();
        db.LifecycleEvents.AddRange(
            new LifecycleEvent
            {
                PlayerId = previousPlayer.Id,
                EventType = "MISSING_IN_ROSTER",
                MetadataJson = JsonUtil.Serialize(new { previousPlayer.Username }),
                Status = "OPEN",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-3)
            },
            new LifecycleEvent
            {
                PlayerId = previousPlayer.Id,
                EventType = "TEMPLE_MISSING_ACTION_REQUIRED",
                MetadataJson = JsonUtil.Serialize(new { previousPlayer.Username }),
                Status = "OPEN",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-3)
            },
            CreateMergeEvent(currentPlayer.Id, "MERGE_SUGGESTED", previousPlayer.Username, previousPlayer.Id),
            CreateMergeEvent(currentPlayer.Id, "MERGE_ACTION_REQUIRED", previousPlayer.Username, previousPlayer.Id));
        await db.SaveChangesAsync();
        var expectedMissingIds = await db.LifecycleEvents
            .Where(x => x.PlayerId == previousPlayer.Id &&
                        (x.EventType == "MISSING_IN_ROSTER" || x.EventType == "TEMPLE_MISSING_ACTION_REQUIRED"))
            .OrderBy(x => x.EventType)
            .Select(x => new { x.EventType, x.Id })
            .ToListAsync();

        var result = await new MergeReviewService(db, new FakeWiseOldManClient())
            .ConfirmSuggestedAsync(currentPlayer.Id, "tester", "test", CancellationToken.None);

        Assert.True(result.Success);
        db.ChangeTracker.Clear();
        var canonicalPlayer = await db.Players.SingleAsync(x => x.Id == previousPlayer.Id);
        Assert.Equal("Lindal", canonicalPlayer.Username);
        Assert.Equal(PlayerStatus.ACTIVE, canonicalPlayer.Status);
        var actualMissingIds = await db.LifecycleEvents
            .Where(x => x.PlayerId == canonicalPlayer.Id &&
                        (x.EventType == "MISSING_IN_ROSTER" || x.EventType == "TEMPLE_MISSING_ACTION_REQUIRED"))
            .OrderBy(x => x.EventType)
            .Select(x => new { x.EventType, x.Id })
            .ToListAsync();
        Assert.Equal(expectedMissingIds, actualMissingIds);
        Assert.All(
            await db.LifecycleEvents
                .Where(x => x.PlayerId == canonicalPlayer.Id &&
                            (x.EventType == "MERGE_SUGGESTED" || x.EventType == "MERGE_ACTION_REQUIRED"))
                .ToListAsync(),
            x => Assert.Equal("DONE", x.Status));
        Assert.Empty(await MergeReviewPolicy.GetOpenReferencesAsync(db, CancellationToken.None));
    }

    [Fact]
    public async Task AbortAsync_keeps_an_active_previous_identity_active_without_missing_review()
    {
        await using var db = CreateDbContext();
        var previousPlayer = new Player { Username = "Yntg", Status = PlayerStatus.ACTIVE };
        var currentPlayer = new Player { Username = "Lindal", Status = PlayerStatus.MERGE_SUGGESTED };
        db.Players.AddRange(previousPlayer, currentPlayer);
        await db.SaveChangesAsync();
        db.LifecycleEvents.AddRange(
            CreateMergeEvent(currentPlayer.Id, "MERGE_SUGGESTED", previousPlayer.Username),
            CreateMergeEvent(currentPlayer.Id, "MERGE_ACTION_REQUIRED", previousPlayer.Username));
        await db.SaveChangesAsync();

        var result = await new MergeReviewService(db, new FakeWiseOldManClient())
            .AbortAsync(currentPlayer.Id, "tester", "test", CancellationToken.None);

        Assert.True(result.Success);
        db.ChangeTracker.Clear();
        Assert.Equal(PlayerStatus.ACTIVE, (await db.Players.SingleAsync(x => x.Id == previousPlayer.Id)).Status);
        Assert.Empty(await db.LifecycleEvents
            .Where(x => x.PlayerId == previousPlayer.Id && x.Status == "OPEN" &&
                        (x.EventType == "MISSING_IN_ROSTER" || x.EventType == "TEMPLE_MISSING_ACTION_REQUIRED"))
            .ToListAsync());
    }

    [Fact]
    public async Task AbortAsync_keeps_a_removed_previous_identity_removed_without_missing_review()
    {
        await using var db = CreateDbContext();
        var previousPlayer = new Player { Username = "Yntg", Status = PlayerStatus.REMOVED_CONFIRMED };
        var currentPlayer = new Player { Username = "Lindal", Status = PlayerStatus.MERGE_SUGGESTED };
        db.Players.AddRange(previousPlayer, currentPlayer);
        await db.SaveChangesAsync();
        db.LifecycleEvents.AddRange(
            CreateMergeEvent(currentPlayer.Id, "MERGE_SUGGESTED", previousPlayer.Username),
            CreateMergeEvent(currentPlayer.Id, "MERGE_ACTION_REQUIRED", previousPlayer.Username));
        await db.SaveChangesAsync();

        var result = await new MergeReviewService(db, new FakeWiseOldManClient())
            .AbortAsync(currentPlayer.Id, "tester", "test", CancellationToken.None);

        Assert.True(result.Success);
        db.ChangeTracker.Clear();
        Assert.Equal(PlayerStatus.REMOVED_CONFIRMED, (await db.Players.SingleAsync(x => x.Id == previousPlayer.Id)).Status);
        Assert.Empty(await db.LifecycleEvents
            .Where(x => x.PlayerId == previousPlayer.Id && x.Status == "OPEN" &&
                        (x.EventType == "MISSING_IN_ROSTER" || x.EventType == "TEMPLE_MISSING_ACTION_REQUIRED"))
            .ToListAsync());
    }

    [Fact]
    public async Task AbortAsync_does_not_restore_missing_review_when_another_merge_controls_the_previous_identity()
    {
        await using var db = CreateDbContext();
        var previousPlayer = new Player { Username = "Yntg", Status = PlayerStatus.MISSING_PENDING_REVIEW };
        var currentPlayer = new Player { Username = "Lindal", Status = PlayerStatus.MERGE_SUGGESTED };
        var otherMergePlayer = new Player { Username = "OtherNew", Status = PlayerStatus.MERGE_SUGGESTED };
        db.Players.AddRange(previousPlayer, currentPlayer, otherMergePlayer);
        await db.SaveChangesAsync();
        db.LifecycleEvents.AddRange(
            CreateMergeEvent(currentPlayer.Id, "MERGE_SUGGESTED", previousPlayer.Username, previousPlayer.Id),
            CreateMergeEvent(currentPlayer.Id, "MERGE_ACTION_REQUIRED", previousPlayer.Username, previousPlayer.Id),
            CreateMergeEvent(otherMergePlayer.Id, "MERGE_SUGGESTED", previousPlayer.Username, previousPlayer.Id),
            CreateMergeEvent(otherMergePlayer.Id, "MERGE_ACTION_REQUIRED", previousPlayer.Username, previousPlayer.Id));
        await db.SaveChangesAsync();

        var result = await new MergeReviewService(db, new FakeWiseOldManClient())
            .AbortAsync(currentPlayer.Id, "tester", "test", CancellationToken.None);

        Assert.True(result.Success);
        db.ChangeTracker.Clear();
        Assert.Equal(PlayerStatus.MISSING_PENDING_REVIEW, (await db.Players.SingleAsync(x => x.Id == previousPlayer.Id)).Status);
        Assert.Empty(await db.LifecycleEvents
            .Where(x => x.PlayerId == previousPlayer.Id && x.Status == "OPEN" &&
                        (x.EventType == "MISSING_IN_ROSTER" || x.EventType == "TEMPLE_MISSING_ACTION_REQUIRED"))
            .ToListAsync());
    }

    [Fact]
    public async Task EnsureOpenPairAsync_is_idempotent_for_unsaved_tracked_entities()
    {
        await using var db = CreateDbContext();
        var player = new Player { Username = "Yntg", Status = PlayerStatus.MISSING_PENDING_REVIEW };
        db.Players.Add(player);
        await db.SaveChangesAsync();

        await MissingReviewLifecycle.EnsureOpenPairAsync(db, player, "test", CancellationToken.None);
        await MissingReviewLifecycle.EnsureOpenPairAsync(db, player, "test", CancellationToken.None);

        var pending = db.ChangeTracker.Entries<LifecycleEvent>()
            .Where(x => x.Entity.PlayerId == player.Id && x.Entity.Status == "OPEN")
            .Select(x => x.Entity.EventType)
            .OrderBy(x => x)
            .ToArray();
        Assert.Equal(["MISSING_IN_ROSTER", "TEMPLE_MISSING_ACTION_REQUIRED"], pending);

        await db.SaveChangesAsync();
        Assert.Equal(2, await db.LifecycleEvents.CountAsync(x => x.PlayerId == player.Id && x.Status == "OPEN"));
    }

    private static LifecycleEvent CreateMergeEvent(int playerId, string eventType, string suggestedPrevious, int? suggestedPreviousPlayerId = null) => new()
    {
        PlayerId = playerId,
        EventType = eventType,
        MetadataJson = JsonUtil.Serialize(new { NewPlayer = "Lindal", SuggestedPrevious = suggestedPrevious, SuggestedPreviousPlayerId = suggestedPreviousPlayerId }),
        Status = "OPEN",
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
    };

    private static TrackerDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<TrackerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new TrackerDbContext(options);
    }

    private sealed class FakeWiseOldManClient : IWiseOldManClient
    {
        public Task<string?> GetMemberRoleAsync(string username, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyDictionary<string, string>> GetMemberRolesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

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
