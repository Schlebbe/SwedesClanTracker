namespace SwedesClanTracker.Worker;

/// <summary>
/// Narrow test seam for Discord side effects exercised by the missing-review
/// behavioral tests. Production construction leaves these hooks null.
/// </summary>
public sealed class DiscordPromotionBotWorkerTestHooks
{
    public Func<ulong, Task<object?>>? ResolveMessageChannelAsync { get; init; }

    public Func<object, string, bool, Task>? RespondToComponentAsync { get; init; }

    public Func<object, object, Task>? UpdateComponentMessageAsync { get; init; }
}
