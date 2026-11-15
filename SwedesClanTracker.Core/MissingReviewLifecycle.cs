using Microsoft.EntityFrameworkCore;

namespace SwedesClanTracker.Core;

public static class MissingReviewLifecycle
{
    private static readonly string[] PairEventTypes =
    [
        "MISSING_IN_ROSTER",
        "TEMPLE_MISSING_ACTION_REQUIRED"
    ];

    public static async Task EnsureOpenPairAsync(
        TrackerDbContext db,
        Player player,
        string source,
        CancellationToken ct)
    {
        var existing = await db.LifecycleEvents
            .Where(x => x.PlayerId == player.Id &&
                        x.Status == "OPEN" &&
                        PairEventTypes.Contains(x.EventType))
            .ToListAsync(ct);

        var trackedOpenTypes = db.ChangeTracker
            .Entries<LifecycleEvent>()
            .Where(x => x.State != EntityState.Deleted &&
                        x.Entity.PlayerId == player.Id &&
                        x.Entity.Status == "OPEN" &&
                        PairEventTypes.Contains(x.Entity.EventType))
            .Select(x => x.Entity.EventType)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var openTypes = existing
            .Select(x => x.EventType)
            .Concat(trackedOpenTypes)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;

        if (!openTypes.Contains("MISSING_IN_ROSTER"))
        {
            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = player.Id,
                EventType = "MISSING_IN_ROSTER",
                MetadataJson = JsonUtil.Serialize(new { player.Username, MissingAt = now, Source = source }),
                Status = "OPEN",
                CreatedAt = now
            });
        }

        if (!openTypes.Contains("TEMPLE_MISSING_ACTION_REQUIRED"))
        {
            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = player.Id,
                EventType = "TEMPLE_MISSING_ACTION_REQUIRED",
                MetadataJson = JsonUtil.Serialize(new { player.Username, MissingAt = now, Source = source }),
                Status = "OPEN",
                CreatedAt = now
            });
        }
    }
}
