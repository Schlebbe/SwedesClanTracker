using SwedesClanTracker.Core;

namespace SwedesClanTracker.Worker;

public static class DiscordMergeCardRules
{
    public const string SupersededCardHandledEventType = "MERGE_SUPERSEDED_DISCORD_CARD_HANDLED";

    public static bool IsSupersededMissingCard(string eventType) =>
        eventType is
            "TEMPLE_MISSING_DISCORD_POSTED" or
            "WOM_MISSING_DISCORD_POSTED";

    public static bool IsMissingCardDeleteSchedule(string eventType) =>
        eventType is
            "TEMPLE_MISSING_DISCORD_DELETE_SCHEDULED" or
            "WOM_MISSING_DISCORD_DELETE_SCHEDULED";

    public static bool ShouldRestoreMissingCard(
        PlayerStatus playerStatus,
        bool hasOpenRequirement,
        bool mergeSuppressed) =>
        playerStatus == PlayerStatus.MISSING_PENDING_REVIEW &&
        hasOpenRequirement &&
        !mergeSuppressed;
}
