using SwedesClanTracker.Worker;

namespace SwedesClanTracker.Worker.Tests;

public sealed class TempleRosterCacheTests
{
    [Fact]
    public async Task Successful_snapshot_is_reused_until_ttl_expires()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = new TempleRosterCache(
            successTtl: TimeSpan.FromMinutes(1),
            failureBackoff: TimeSpan.FromSeconds(5),
            timeProvider: clock);
        var calls = 0;

        async Task<TempleRosterLookupResult> Fetch(CancellationToken _)
        {
            calls++;
            await Task.Yield();
            return new TempleRosterLookupResult(
                true,
                new HashSet<string>(["Lindal"], StringComparer.OrdinalIgnoreCase));
        }

        var first = await cache.GetAsync(449, Fetch, CancellationToken.None);
        var second = await cache.GetAsync(449, Fetch, CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(second.Usernames.Contains("Lindal"));
        Assert.Equal(1, calls);

        clock.Advance(TimeSpan.FromMinutes(1));
        await cache.GetAsync(449, Fetch, CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Failed_snapshot_is_unknown_and_does_not_become_empty_success()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = new TempleRosterCache(
            successTtl: TimeSpan.FromMinutes(1),
            failureBackoff: TimeSpan.FromSeconds(5),
            timeProvider: clock);
        var calls = 0;

        Task<TempleRosterLookupResult> Fetch(CancellationToken _)
        {
            calls++;
            return Task.FromResult(calls == 1
                ? new TempleRosterLookupResult(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                : new TempleRosterLookupResult(true, new HashSet<string>(["Lindal"], StringComparer.OrdinalIgnoreCase)));
        }

        var failed = await cache.GetAsync(449, Fetch, CancellationToken.None);
        Assert.False(failed.Succeeded);
        Assert.Empty(failed.Usernames);

        var duringBackoff = await cache.GetAsync(449, Fetch, CancellationToken.None);
        Assert.False(duringBackoff.Succeeded);
        Assert.Equal(1, calls);

        clock.Advance(TimeSpan.FromSeconds(5));
        var recovered = await cache.GetAsync(449, Fetch, CancellationToken.None);
        Assert.True(recovered.Succeeded);
        Assert.True(recovered.Usernames.Contains("Lindal"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Concurrent_reads_share_one_in_flight_fetch()
    {
        var cache = new TempleRosterCache();
        var calls = 0;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<TempleRosterLookupResult> Fetch(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await gate.Task.WaitAsync(ct);
            return new TempleRosterLookupResult(
                true,
                new HashSet<string>(["Lindal"], StringComparer.OrdinalIgnoreCase));
        }

        var first = cache.GetAsync(449, Fetch, CancellationToken.None);
        var second = cache.GetAsync(449, Fetch, CancellationToken.None);
        await Task.Delay(10);
        gate.SetResult(true);

        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Invalidation_after_add_makes_the_next_check_fetch_fresh_membership()
    {
        var cache = new TempleRosterCache();
        var calls = 0;

        Task<TempleRosterLookupResult> Fetch(CancellationToken _)
        {
            calls++;
            var usernames = calls == 1
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(["Lindal"], StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(new TempleRosterLookupResult(true, usernames));
        }

        var beforeAdd = await cache.GetAsync(449, Fetch, CancellationToken.None);
        await cache.InvalidateAsync(449, CancellationToken.None);
        var afterAdd = await cache.GetAsync(449, Fetch, CancellationToken.None, forceRefresh: true);

        Assert.True(beforeAdd.Succeeded);
        Assert.Empty(beforeAdd.Usernames);
        Assert.True(afterAdd.Succeeded);
        Assert.Contains("Lindal", afterAdd.Usernames);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Invalidation_waits_for_pre_mutation_fetch_before_allowing_fresh_state()
    {
        var cache = new TempleRosterCache();
        var oldFetchStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldFetch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task<TempleRosterLookupResult> Fetch(CancellationToken ct)
        {
            calls++;
            if (calls == 1)
            {
                oldFetchStarted.SetResult(true);
                await releaseOldFetch.Task.WaitAsync(ct);
                return new TempleRosterLookupResult(
                    true,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }

            return new TempleRosterLookupResult(
                true,
                new HashSet<string>(["Lindal"], StringComparer.OrdinalIgnoreCase));
        }

        var preMutationRead = cache.GetAsync(449, Fetch, CancellationToken.None);
        await oldFetchStarted.Task;
        var invalidation = cache.InvalidateAsync(449, CancellationToken.None);
        releaseOldFetch.SetResult(true);
        await preMutationRead;
        await invalidation;

        var postMutationRead = await cache.GetAsync(449, Fetch, CancellationToken.None, forceRefresh: true);

        Assert.True(postMutationRead.Succeeded);
        Assert.Contains("Lindal", postMutationRead.Usernames);
        Assert.Equal(2, calls);
    }

    private sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset utcNow = initial;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan amount) => utcNow += amount;
    }
}
