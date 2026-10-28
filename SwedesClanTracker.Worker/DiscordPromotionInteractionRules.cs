namespace SwedesClanTracker.Worker;

public static class DiscordPromotionInteractionRules
{
    public static bool ShouldDefer(bool hasResponded, bool isMergeManual) =>
        !hasResponded && !isMergeManual;
}
