using SwedesClanTracker.Core;

namespace SwedesClanTracker.Worker;

public static class DiscordCommandErrorMessages
{
    public const string GenericCommandFailure = "Command failed.";

    public static string Format(
        Exception exception,
        string fallback = GenericCommandFailure,
        DateTimeOffset? now = null)
    {
        if (exception is not WiseOldManRateLimitException rateLimit)
        {
            return fallback;
        }

        var retryAt = rateLimit.RetryAfterUtc;
        var retryTimestamp = retryAt.ToUnixTimeSeconds();
        var relativeTimestamp = $"<t:{retryTimestamp}:R>";
        var absoluteTimestamp = $"<t:{retryTimestamp}:f>";
        var currentTime = now ?? DateTimeOffset.UtcNow;
        var retryInstruction = retryAt > currentTime
            ? $"Retry {relativeTimestamp} ({absoluteTimestamp})."
            : $"The cooldown deadline has passed; retry now (deadline {relativeTimestamp}, {absoluteTimestamp}).";

        return "Wise Old Man (WOM) roster requests are temporarily on cooldown. " +
            retryInstruction;
    }
}
