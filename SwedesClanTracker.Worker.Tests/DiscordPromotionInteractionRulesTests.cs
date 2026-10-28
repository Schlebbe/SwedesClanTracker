using SwedesClanTracker.Worker;

namespace SwedesClanTracker.Worker.Tests;

public sealed class DiscordPromotionInteractionRulesTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void ShouldDefer_only_unacknowledged_non_manual_interactions(
        bool hasResponded,
        bool isMergeManual,
        bool expected)
    {
        Assert.Equal(expected, DiscordPromotionInteractionRules.ShouldDefer(hasResponded, isMergeManual));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ShouldApplyWomRankMismatchAction_only_when_matching_event_is_open(
        bool hasMatchingOpenMismatch,
        bool expected)
    {
        Assert.Equal(expected, DiscordPromotionInteractionRules.ShouldApplyWomRankMismatchAction(hasMatchingOpenMismatch));
    }
}
