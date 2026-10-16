namespace SwedesClanTracker.Worker;

public static class DiscordClearRules
{
    public static bool CanRunInConfiguredChannel(ulong? commandChannelId, ulong configuredChannelId) =>
        configuredChannelId > 0 && commandChannelId == configuredChannelId;

    public static IReadOnlyList<DiscordClearMessage> SelectUserMessagesToDelete(
        IEnumerable<DiscordClearMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return messages
            .Where(message => message.IsUserPosted)
            .OrderBy(message => message.Timestamp)
            .ThenBy(message => message.Id)
            .Skip(1)
            .ToArray();
    }
}

public sealed record DiscordClearMessage(ulong Id, DateTimeOffset Timestamp, bool IsUserPosted);
