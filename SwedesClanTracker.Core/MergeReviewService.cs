using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SwedesClanTracker.Core;

public interface IMergeReviewService
{
    Task<MergeActionResult> ConfirmSuggestedAsync(int newPlayerId, string handledBy, string source, CancellationToken ct);
    Task<MergeActionResult> ReassignAsync(int newPlayerId, string previousUsername, string handledBy, string source, CancellationToken ct);
    Task<MergeActionResult> AbortAsync(int newPlayerId, string handledBy, string source, CancellationToken ct);
}

public record MergeActionResult(bool Success, string Message);

public class MergeReviewService(TrackerDbContext db, IWiseOldManClient wiseOldManClient) : IMergeReviewService
{
    public async Task<MergeActionResult> ConfirmSuggestedAsync(int newPlayerId, string handledBy, string source, CancellationToken ct)
    {
        var newPlayer = await db.Players.FirstOrDefaultAsync(x => x.Id == newPlayerId, ct);
        if (newPlayer is null) return new(false, "New player not found.");
        if (newPlayer.Status != PlayerStatus.MERGE_SUGGESTED) return new(false, "Rename review is already handled.");

        var suggested = await GetSuggestedPreviousAsync(newPlayerId, ct);
        if (string.IsNullOrWhiteSpace(suggested)) return new(false, "No suggested previous player was found.");
        return await MergeIntoPreviousAsync(newPlayer, suggested, "confirm", handledBy, source, ct);
    }

    public async Task<MergeActionResult> ReassignAsync(int newPlayerId, string previousUsername, string handledBy, string source, CancellationToken ct)
    {
        var newPlayer = await db.Players.FirstOrDefaultAsync(x => x.Id == newPlayerId, ct);
        if (newPlayer is null) return new(false, "New player not found.");
        if (newPlayer.Status != PlayerStatus.MERGE_SUGGESTED) return new(false, "Rename review is already handled.");
        if (string.IsNullOrWhiteSpace(previousUsername)) return new(false, "Previous username is required.");

        return await MergeIntoPreviousAsync(newPlayer, previousUsername, "reassign", handledBy, source, ct);
    }

    public async Task<MergeActionResult> AbortAsync(int newPlayerId, string handledBy, string source, CancellationToken ct)
    {
        var newPlayer = await db.Players.FirstOrDefaultAsync(x => x.Id == newPlayerId, ct);
        if (newPlayer is null) return new(false, "New player not found.");
        if (newPlayer.Status != PlayerStatus.MERGE_SUGGESTED) return new(false, "Rename review is already handled.");

        newPlayer.Status = PlayerStatus.ACTIVE;

        var metadata = await GetLatestMergeMetadataAsync(newPlayer.Id, ct);
        var suggestedPrevious = Pick(metadata, "SuggestedPrevious");
        Player? oldPlayer = null;
        if (!string.IsNullOrWhiteSpace(suggestedPrevious))
        {
            oldPlayer = await db.Players.FirstOrDefaultAsync(x =>
                x.Id != newPlayer.Id &&
                x.Username.ToLower() == suggestedPrevious.ToLower(), ct);
        }
        if (oldPlayer is not null && oldPlayer.Status != PlayerStatus.REMOVED_CONFIRMED)
        {
            oldPlayer.Status = PlayerStatus.MISSING_PENDING_REVIEW;
            await EnsureOpenEventAsync(oldPlayer.Id, "MISSING_IN_ROSTER", new { Username = oldPlayer.Username, Source = source, MissingAt = DateTimeOffset.UtcNow }, ct);
            await EnsureOpenEventAsync(oldPlayer.Id, "TEMPLE_MISSING_ACTION_REQUIRED", new { Username = oldPlayer.Username, Source = source, MissingAt = DateTimeOffset.UtcNow }, ct);
        }

        await CloseOpenLifecycleEventsAsync(newPlayer.Id, ct, "NEW_PLAYER", "MERGE_SUGGESTED", "MERGE_ACTION_REQUIRED");
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = newPlayer.Id,
            EventType = "MERGE_ACTION_APPLIED",
            MetadataJson = JsonUtil.Serialize(new
            {
                Action = "abort",
                NewPlayer = newPlayer.Username,
                HandledBy = handledBy,
                Source = source,
                SuggestedPrevious = suggestedPrevious
            }),
            Status = "OPEN",
            CreatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync(ct);
        return new(true, "Rename review aborted. New player kept as new identity.");
    }

    private async Task<MergeActionResult> MergeIntoPreviousAsync(Player newPlayer, string previousUsername, string action, string handledBy, string source, CancellationToken ct)
    {
        var normalizedPrevious = UsernameRules.NormalizeUsername(previousUsername);
        var oldPlayer = await db.Players.FirstOrDefaultAsync(x =>
            x.Id != newPlayer.Id &&
            x.Username.ToLower() == normalizedPrevious.ToLower(), ct);
        if (oldPlayer is null) return new(false, $"Previous player '{previousUsername}' was not found.");

        var finalUsername = UsernameRules.NormalizeUsername(newPlayer.Username);
        var collided = await db.Players.AnyAsync(x => x.Id != oldPlayer.Id && x.Id != newPlayer.Id && x.Username.ToLower() == finalUsername.ToLower(), ct);
        if (collided) return new(false, $"Cannot merge because '{finalUsername}' already exists.");

        var womCleanupSummary = "";
        var oldWomRole = await wiseOldManClient.GetMemberRoleAsync(oldPlayer.Username, ct);
        if (string.IsNullOrWhiteSpace(oldWomRole) ||
            string.Equals(oldWomRole, "member", StringComparison.OrdinalIgnoreCase))
        {
            var womCleanup = await wiseOldManClient.RemoveMemberAsync(oldPlayer.Username, ct);
            womCleanupSummary = womCleanup.Success
                ? $" WOM cleanup: attempted old-name removal ({womCleanup.Details})"
                : $" WOM cleanup warning: attempted old-name removal but failed ({womCleanup.Details})";
        }
        else
        {
            womCleanupSummary = $" WOM cleanup skipped: old name currently has WOM role '{oldWomRole}'. Run /wom-remove {oldPlayer.Username} if this is the stale previous name.";
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        oldPlayer.Username = finalUsername;
        oldPlayer.Status = PlayerStatus.ACTIVE;
        oldPlayer.LastSeen = newPlayer.LastSeen > oldPlayer.LastSeen ? newPlayer.LastSeen : oldPlayer.LastSeen;
        if (newPlayer.LastSynced.HasValue && (!oldPlayer.LastSynced.HasValue || newPlayer.LastSynced > oldPlayer.LastSynced))
        {
            oldPlayer.LastSynced = newPlayer.LastSynced;
        }
        if (newPlayer.StoredPetCount > oldPlayer.StoredPetCount) oldPlayer.StoredPetCount = newPlayer.StoredPetCount;
        if (newPlayer.ManualPetOverride.HasValue) oldPlayer.ManualPetOverride = newPlayer.ManualPetOverride;
        oldPlayer.EligibleRank = RankRules.RankOrder(newPlayer.EligibleRank) > RankRules.RankOrder(oldPlayer.EligibleRank)
            ? newPlayer.EligibleRank
            : oldPlayer.EligibleRank;

        var snapshots = await db.PlayerSnapshots.Where(x => x.PlayerId == newPlayer.Id).ToListAsync(ct);
        foreach (var snapshot in snapshots) snapshot.PlayerId = oldPlayer.Id;

        var promotions = await db.PromotionCandidates.Where(x => x.PlayerId == newPlayer.Id).ToListAsync(ct);
        foreach (var promotion in promotions) promotion.PlayerId = oldPlayer.Id;

        var lifecycle = await db.LifecycleEvents.Where(x => x.PlayerId == newPlayer.Id).ToListAsync(ct);
        foreach (var ev in lifecycle) ev.PlayerId = oldPlayer.Id;

        db.Players.Remove(newPlayer);
        await CloseOpenLifecycleEventsAsync(oldPlayer.Id, ct,
            "NEW_PLAYER",
            "MERGE_SUGGESTED",
            "MERGE_ACTION_REQUIRED",
            "MISSING_IN_ROSTER",
            "TEMPLE_MISSING_ACTION_REQUIRED");
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = oldPlayer.Id,
            EventType = "MERGE_ACTION_APPLIED",
            MetadataJson = JsonUtil.Serialize(new
            {
                Action = action,
                PreviousPlayer = previousUsername,
                NewPlayer = finalUsername,
                CanonicalPlayer = oldPlayer.Username,
                HandledBy = handledBy,
                Source = source
            }),
            Status = "OPEN",
            CreatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(true, $"Rename confirmed: {previousUsername} -> {finalUsername}.{womCleanupSummary}");
    }

    private async Task<string?> GetSuggestedPreviousAsync(int newPlayerId, CancellationToken ct)
    {
        var metadata = await GetLatestMergeMetadataAsync(newPlayerId, ct);
        return Pick(metadata, "SuggestedPrevious");
    }

    private async Task<Dictionary<string, string>> GetLatestMergeMetadataAsync(int newPlayerId, CancellationToken ct)
    {
        var ev = await db.LifecycleEvents
            .Where(x => x.PlayerId == newPlayerId && x.EventType == "MERGE_SUGGESTED")
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct);
        return ev is null ? [] : ReadMetadata(ev.MetadataJson);
    }

    private static Dictionary<string, string> ReadMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                dict[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString() ?? "",
                    JsonValueKind.Number => p.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => p.Value.GetRawText()
                };
            }
            return dict;
        }
        catch { return []; }
    }

    private static string? Pick(Dictionary<string, string> metadata, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private async Task EnsureOpenEventAsync(int playerId, string eventType, object metadata, CancellationToken ct)
    {
        var exists = await db.LifecycleEvents.AnyAsync(x => x.PlayerId == playerId && x.EventType == eventType && x.Status == "OPEN", ct);
        if (!exists)
        {
            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = playerId,
                EventType = eventType,
                MetadataJson = JsonUtil.Serialize(metadata),
                Status = "OPEN",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private async Task CloseOpenLifecycleEventsAsync(int playerId, CancellationToken ct, params string[] eventTypes)
    {
        var events = await db.LifecycleEvents
            .Where(x => x.PlayerId == playerId && x.Status == "OPEN" && eventTypes.Contains(x.EventType))
            .ToListAsync(ct);
        foreach (var ev in events) ev.Status = "DONE";
    }
}
