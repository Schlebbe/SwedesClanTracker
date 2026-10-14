using SwedesClanTracker.Core;

namespace SwedesClanTracker.Worker;

public static class DiscordDeleteScheduleMatcher
{
    public static bool MatchesOpenOrCompletedSchedule(LifecycleEvent lifecycleEvent, ulong messageId)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);

        return IsMessageDeleteScheduleEvent(lifecycleEvent.EventType) &&
               lifecycleEvent.Status is "OPEN" or "DONE" &&
               LifecycleMetadataMatcher.HasUlongProperty(
                   lifecycleEvent.MetadataJson,
                   "DiscordMessageId",
                   messageId);
    }

    private static bool IsMessageDeleteScheduleEvent(string eventType) =>
        eventType is
            "PROMOTION_DISCORD_DELETE_SCHEDULED" or
            "TEMPLE_MISSING_DISCORD_DELETE_SCHEDULED" or
            "WOM_MISSING_DISCORD_DELETE_SCHEDULED" or
            "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED";
}
