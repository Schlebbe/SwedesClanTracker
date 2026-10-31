using SwedesClanTracker.Core;
using SwedesClanTracker.Worker;

namespace SwedesClanTracker.Worker.Tests;

public sealed class DiscordCommandErrorMessagesTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Cooldown_message_names_wom_and_includes_relative_and_absolute_retry_times()
    {
        var retryAt = Now.AddMinutes(5);
        var exception = new WiseOldManRateLimitException(retryAt, "internal response details");

        var message = DiscordCommandErrorMessages.Format(exception, now: Now);

        Assert.Contains("Wise Old Man", message, StringComparison.Ordinal);
        Assert.Contains("WOM", message, StringComparison.Ordinal);
        Assert.Contains("cooldown", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"<t:{retryAt.ToUnixTimeSeconds()}:R>", message, StringComparison.Ordinal);
        Assert.Contains($"<t:{retryAt.ToUnixTimeSeconds()}:f>", message, StringComparison.Ordinal);
        Assert.DoesNotContain("internal response details", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Past_deadline_message_is_still_timestamped_and_tells_user_to_retry_now()
    {
        var retryAt = Now.AddSeconds(-1);
        var message = DiscordCommandErrorMessages.Format(
            new WiseOldManRateLimitException(retryAt, "internal"),
            now: Now);

        Assert.Contains("deadline has passed", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retry now", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"<t:{retryAt.ToUnixTimeSeconds()}:R>", message, StringComparison.Ordinal);
        Assert.Contains($"<t:{retryAt.ToUnixTimeSeconds()}:f>", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unrelated_exception_keeps_generic_fallback_without_leaking_details()
    {
        const string fallback = "Command failed.";
        var message = DiscordCommandErrorMessages.Format(
            new InvalidOperationException("database connection secret"),
            fallback,
            Now);

        Assert.Equal(fallback, message);
        Assert.DoesNotContain("database connection secret", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Wise Old Man", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cache_refresh_can_supply_its_existing_safe_fallback()
    {
        const string fallback = "Failed to refresh the Wise Old Man roster cache. Check worker logs for details.";

        var message = DiscordCommandErrorMessages.Format(
            new TimeoutException("internal timeout"),
            fallback,
            Now);

        Assert.Equal(fallback, message);
    }
}
