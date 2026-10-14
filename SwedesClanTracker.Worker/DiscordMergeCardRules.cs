namespace SwedesClanTracker.Worker;

public static class DiscordMergeCardRules
{
    public const string SupersededCardHandledEventType = "MERGE_SUPERSEDED_DISCORD_CARD_HANDLED";

    public static bool IsSupersededMissingCard(string eventType) =>
        eventType is
            "TEMPLE_MISSING_DISCORD_POSTED" or
            "WOM_MISSING_DISCORD_POSTED";
}
