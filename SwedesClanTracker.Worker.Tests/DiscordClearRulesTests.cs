using SwedesClanTracker.Worker;

namespace SwedesClanTracker.Worker.Tests;

public class DiscordClearRulesTests
{
    [Fact]
    public void Clear_can_only_run_in_the_exact_configured_channel()
    {
        const ulong configuredChannelId = 123;

        Assert.True(DiscordClearRules.CanRunInConfiguredChannel(configuredChannelId, configuredChannelId));
        Assert.False(DiscordClearRules.CanRunInConfiguredChannel(null, configuredChannelId));
        Assert.False(DiscordClearRules.CanRunInConfiguredChannel(456, configuredChannelId));
        Assert.False(DiscordClearRules.CanRunInConfiguredChannel(configuredChannelId, 0));
    }

    [Fact]
    public void Clear_keeps_the_oldest_user_message_and_ignores_bot_messages()
    {
        var messages = new[]
        {
            new DiscordClearMessage(50, DateTimeOffset.Parse("2026-10-16T12:02:00Z"), IsUserPosted: true),
            new DiscordClearMessage(10, DateTimeOffset.Parse("2026-10-16T12:00:00Z"), IsUserPosted: false),
            new DiscordClearMessage(30, DateTimeOffset.Parse("2026-10-16T12:01:00Z"), IsUserPosted: true),
            new DiscordClearMessage(20, DateTimeOffset.Parse("2026-10-16T12:00:30Z"), IsUserPosted: true),
        };

        var toDelete = DiscordClearRules.SelectUserMessagesToDelete(messages);

        Assert.Equal([30UL, 50UL], toDelete.Select(message => message.Id));
    }

    [Fact]
    public void Clear_deletes_no_messages_when_there_is_only_one_user_message()
    {
        var messages = new[]
        {
            new DiscordClearMessage(10, DateTimeOffset.UtcNow, IsUserPosted: false),
            new DiscordClearMessage(20, DateTimeOffset.UtcNow, IsUserPosted: true),
        };

        Assert.Empty(DiscordClearRules.SelectUserMessagesToDelete(messages));
    }
}
