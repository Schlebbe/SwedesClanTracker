using Microsoft.EntityFrameworkCore;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.Core.Tests;

public sealed class MergeReviewPolicyTests
{
    [Fact]
    public void Controlled_previous_identity_is_suppressed_but_unrelated_identity_is_not()
    {
        var references = new[]
        {
            new OpenMergeReviewReference(10, 42, "Ironhold")
        };

        Assert.True(MergeReviewPolicy.ControlsPreviousIdentity(references, 42, "Ironhold"));
        Assert.True(MergeReviewPolicy.ControlsPreviousIdentity(references, 999, "Ironhold"));
        Assert.False(MergeReviewPolicy.ControlsPreviousIdentity(references, 999, "Unrelated"));
    }

    [Fact]
    public async Task Open_merge_policy_resolves_previous_identity_from_posted_card_fallback()
    {
        await using var db = new TrackerDbContext(new DbContextOptionsBuilder<TrackerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var current = new Player { Username = "Stonehall", Status = PlayerStatus.MERGE_SUGGESTED };
        var previous = new Player { Username = "Ironhold", Status = PlayerStatus.MISSING_PENDING_REVIEW };
        db.Players.AddRange(current, previous);
        await db.SaveChangesAsync();
        db.LifecycleEvents.AddRange(
            new LifecycleEvent
            {
                PlayerId = current.Id,
                EventType = "MERGE_ACTION_REQUIRED",
                MetadataJson = JsonUtil.Serialize(new { NewPlayer = current.Username }),
                Status = "OPEN",
                CreatedAt = DateTimeOffset.UtcNow
            },
            new LifecycleEvent
            {
                PlayerId = current.Id,
                EventType = "MERGE_DISCORD_POSTED",
                MetadataJson = JsonUtil.Serialize(new
                {
                    NewPlayer = current.Username,
                    SuggestedPrevious = previous.Username,
                    SuggestedPreviousPlayerId = previous.Id
                }),
                Status = "DONE",
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-1)
            });
        await db.SaveChangesAsync();

        var references = await MergeReviewPolicy.GetOpenReferencesAsync(db, CancellationToken.None);

        var reference = Assert.Single(references);
        Assert.Equal(previous.Id, reference.SuggestedPreviousPlayerId);
        Assert.True(MergeReviewPolicy.ControlsPreviousIdentity(references, previous.Id, previous.Username));
    }
}
