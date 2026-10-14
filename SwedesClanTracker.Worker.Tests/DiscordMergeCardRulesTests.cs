using SwedesClanTracker.Worker;

namespace SwedesClanTracker.Worker.Tests;

public class DiscordMergeCardRulesTests
{
    [Theory]
    [InlineData("TEMPLE_MISSING_DISCORD_POSTED")]
    [InlineData("WOM_MISSING_DISCORD_POSTED")]
    public void Missing_card_post_events_are_superseded_cards(string eventType)
    {
        Assert.True(DiscordMergeCardRules.IsSupersededMissingCard(eventType));
    }

    [Theory]
    [InlineData("MERGE_DISCORD_POSTED")]
    [InlineData("PROMOTION_DISCORD_POSTED")]
    [InlineData("TEMPLE_NAME_CHANGE_NEEDED_DISCORD_POSTED")]
    [InlineData("UNRELATED_EVENT")]
    public void Unrelated_and_other_posting_events_are_not_superseded_cards(string eventType)
    {
        Assert.False(DiscordMergeCardRules.IsSupersededMissingCard(eventType));
    }
}
