using System.Collections.Concurrent;

namespace SwedesClanTracker.Worker;

public sealed record TempleRosterLookupResult(
    bool Succeeded,
    IReadOnlySet<string> Usernames);

/// <summary>
/// Coalesces Temple roster reads and keeps a successful snapshot briefly.
/// Failed reads are never represented as an empty successful roster.
/// </summary>
public sealed class TempleRosterCache
{
    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public TempleRosterLookupResult? LastResult { get; set; }
        public DateTimeOffset LastAttemptAt { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset SuccessValidUntil { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset RetryAfter { get; set; } = DateTimeOffset.MinValue;
    }

    private static readonly TempleRosterLookupResult Unknown =
        new(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private readonly ConcurrentDictionary<int, Entry> entries = new();
    private readonly TimeSpan successTtl;
    private readonly TimeSpan failureBackoff;
    private readonly TimeProvider timeProvider;

    public TempleRosterCache(
        TimeSpan? successTtl = null,
        TimeSpan? failureBackoff = null,
        TimeProvider? timeProvider = null)
    {
        this.successTtl = successTtl ?? TimeSpan.FromSeconds(45);
        this.failureBackoff = failureBackoff ?? TimeSpan.FromSeconds(10);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<TempleRosterLookupResult> GetAsync(
        int groupId,
        Func<CancellationToken, Task<TempleRosterLookupResult>> fetchAsync,
        CancellationToken ct,
        bool forceRefresh = false)
    {
        if (groupId <= 0) return Unknown;

        var entry = entries.GetOrAdd(groupId, static _ => new Entry());
        var requestedAt = timeProvider.GetUtcNow();
        if (CanReuse(entry, requestedAt, forceRefresh))
        {
            return entry.LastResult!;
        }

        await entry.Gate.WaitAsync(ct);
        try
        {
            var now = timeProvider.GetUtcNow();
            // A forced refresh that waited behind another caller can reuse the
            // result that caller just obtained instead of issuing a second call.
            if (entry.LastAttemptAt >= requestedAt && entry.LastResult is not null)
            {
                return entry.LastResult;
            }

            if (CanReuse(entry, now, forceRefresh))
            {
                return entry.LastResult!;
            }

            TempleRosterLookupResult result;
            try
            {
                result = await fetchAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                result = Unknown;
            }

            entry.LastAttemptAt = now;
            if (result.Succeeded)
            {
                entry.LastResult = result;
                entry.SuccessValidUntil = now.Add(successTtl);
                entry.RetryAfter = DateTimeOffset.MinValue;
            }
            else
            {
                // Preserve the last failure as Unknown. In particular, do not
                // turn an HTTP/API failure into an empty successful roster.
                entry.LastResult = Unknown;
                entry.SuccessValidUntil = DateTimeOffset.MinValue;
                entry.RetryAfter = now.Add(failureBackoff);
            }

            return entry.LastResult;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async Task InvalidateAsync(int groupId, CancellationToken ct)
    {
        if (groupId <= 0) return;

        var entry = entries.GetOrAdd(groupId, static _ => new Entry());
        await entry.Gate.WaitAsync(ct);
        try
        {
            entry.LastResult = null;
            entry.LastAttemptAt = DateTimeOffset.MinValue;
            entry.SuccessValidUntil = DateTimeOffset.MinValue;
            entry.RetryAfter = DateTimeOffset.MinValue;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private static bool CanReuse(Entry entry, DateTimeOffset now, bool forceRefresh) =>
        entry.LastResult is not null &&
        (entry.LastResult.Succeeded
            ? !forceRefresh && now < entry.SuccessValidUntil
            : now < entry.RetryAfter);
}
