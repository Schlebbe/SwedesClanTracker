using SwedesClanTracker.Core;
using SwedesClanTracker.Worker;

namespace SwedesClanTracker.Worker.Tests;

public class DiscordDeleteScheduleMatcherTests
{
    private const ulong MessageId = 1234567890123456789;

    [Fact]
    public void Done_schedule_for_same_message_matches()
    {
        var lifecycleEvent = CreateEvent("PROMOTION_DISCORD_DELETE_SCHEDULED", "DONE", MessageId);

        Assert.True(DiscordDeleteScheduleMatcher.MatchesOpenOrCompletedSchedule(lifecycleEvent, MessageId));
    }

    [Fact]
    public void Open_schedule_for_same_message_matches()
    {
        var lifecycleEvent = CreateEvent("TEMPLE_MISSING_DISCORD_DELETE_SCHEDULED", "OPEN", MessageId);

        Assert.True(DiscordDeleteScheduleMatcher.MatchesOpenOrCompletedSchedule(lifecycleEvent, MessageId));
    }

    [Fact]
    public void Done_schedule_for_different_message_does_not_match()
    {
        var lifecycleEvent = CreateEvent("WOM_MISSING_DISCORD_DELETE_SCHEDULED", "DONE", MessageId);

        Assert.False(DiscordDeleteScheduleMatcher.MatchesOpenOrCompletedSchedule(lifecycleEvent, MessageId + 1));
    }

    [Theory]
    [InlineData("UNRELATED_EVENT", "OPEN")]
    [InlineData("PROMOTION_DISCORD_DELETE_SCHEDULED", "CLOSED")]
    [InlineData("PROMOTION_DISCORD_DELETE_SCHEDULED", "FAILED")]
    public void Unrelated_event_type_or_non_terminal_status_does_not_match(string eventType, string status)
    {
        var lifecycleEvent = CreateEvent(eventType, status, MessageId);

        Assert.False(DiscordDeleteScheduleMatcher.MatchesOpenOrCompletedSchedule(lifecycleEvent, MessageId));
    }

    private static LifecycleEvent CreateEvent(string eventType, string status, ulong messageId) => new()
    {
        EventType = eventType,
        Status = status,
        MetadataJson = $"{{\"DiscordMessageId\":{messageId}}}"
    };
}
