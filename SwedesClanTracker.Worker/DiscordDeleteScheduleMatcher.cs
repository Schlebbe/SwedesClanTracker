using System.Text.Json.Nodes;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.Worker;

public static class DiscordDeleteScheduleMatcher
{
    private const string DeletionOutcomeProperty = "DeletionOutcome";
    private const string CancelledOutcome = "CANCELLED";

    public static bool MatchesOpenOrCompletedSchedule(LifecycleEvent lifecycleEvent, ulong messageId)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);

        return IsMessageDeleteScheduleEvent(lifecycleEvent.EventType) &&
               lifecycleEvent.Status is "OPEN" or "DONE" &&
               !IsCancelled(lifecycleEvent) &&
               LifecycleMetadataMatcher.HasUlongProperty(
                   lifecycleEvent.MetadataJson,
                   "DiscordMessageId",
                   messageId);
    }

    public static bool IsCancelled(LifecycleEvent lifecycleEvent)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);

        return LifecycleMetadataMatcher.HasStringProperty(
            lifecycleEvent.MetadataJson,
            DeletionOutcomeProperty,
            CancelledOutcome);
    }

    public static void MarkCancelled(
        LifecycleEvent lifecycleEvent,
        string reason,
        DateTimeOffset cancelledAtUtc)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);

        JsonObject metadata;
        try
        {
            metadata = JsonNode.Parse(lifecycleEvent.MetadataJson) as JsonObject ?? new JsonObject();
        }
        catch
        {
            metadata = new JsonObject();
        }

        metadata[DeletionOutcomeProperty] = CancelledOutcome;
        metadata["CancellationReason"] = reason;
        metadata["CancelledAtUtc"] = cancelledAtUtc.ToUniversalTime().ToString("O");
        lifecycleEvent.MetadataJson = metadata.ToJsonString();
    }

    private static bool IsMessageDeleteScheduleEvent(string eventType) =>
        eventType is
            "PROMOTION_DISCORD_DELETE_SCHEDULED" or
            "TEMPLE_MISSING_DISCORD_DELETE_SCHEDULED" or
            "WOM_MISSING_DISCORD_DELETE_SCHEDULED" or
            "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED";
}
