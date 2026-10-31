using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.Core.Tests;

[Collection("WiseOldManClient")]
public sealed class WiseOldManClientTests
{
    [Fact]
    public async Task Successful_role_put_cannot_be_overwritten_by_an_inflight_stale_csv_fetch()
    {
        var handler = new CacheContendingHandler();
        using var httpClient = new HttpClient(handler);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WiseOldMan:GroupId"] = "123",
                ["WiseOldMan:VerificationCode"] = "test-code"
            })
            .Build();
        var client = new WiseOldManClient(httpClient, configuration);

        await client.InvalidateCacheAsync(CancellationToken.None);
        using var csvCts = new CancellationTokenSource();
        var csvFetch = client.GetMemberRolesAsync(csvCts.Token);
        await handler.CsvFetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var putCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var result = await client.UpdateMemberRoleAsync("Gynofobi", "Officer", putCts.Token);

        Assert.True(result.Success);
        Assert.Equal(200, result.HttpStatus);
        Assert.Equal(1, handler.PutCalls);

        handler.ReleaseCsv.TrySetResult(true);
        await csvFetch;

        var roleAfterMutation = await client.GetMemberRoleAsync("Gynofobi", CancellationToken.None);

        Assert.Equal("commander", roleAfterMutation);
        Assert.Equal(2, handler.CsvCalls);
    }

    [Fact]
    public async Task Rate_limited_refresh_uses_stale_cache_and_does_not_retry_during_cooldown()
    {
        var handler = new RateLimitAfterInitialFetchHandler();
        using var httpClient = new HttpClient(handler);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WiseOldMan:GroupId"] = "124"
            })
            .Build();
        var client = new WiseOldManClient(httpClient, configuration);

        await client.GetMemberRolesAsync(CancellationToken.None);
        await client.InvalidateCacheAsync(CancellationToken.None);

        var staleRole = await client.GetMemberRoleAsync("Gynofobi", CancellationToken.None);
        var staleRoleDuringCooldown = await client.GetMemberRoleAsync("Gynofobi", CancellationToken.None);

        Assert.Equal("officer", staleRole);
        Assert.Equal("officer", staleRoleDuringCooldown);
        Assert.Equal(2, handler.CsvCalls);
    }

    [Fact]
    public async Task Rate_limited_fetch_without_cache_throws_and_suppresses_follow_up_request()
    {
        var handler = new AlwaysRateLimitedHandler();
        using var httpClient = new HttpClient(handler);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WiseOldMan:GroupId"] = "125"
            })
            .Build();
        var client = new WiseOldManClient(httpClient, configuration);

        var first = await Assert.ThrowsAsync<WiseOldManRateLimitException>(
            () => client.GetMemberRolesAsync(CancellationToken.None));
        var second = await Assert.ThrowsAsync<WiseOldManRateLimitException>(
            () => client.GetMemberRolesAsync(CancellationToken.None));

        Assert.True(first.RetryAfterUtc > DateTimeOffset.UtcNow.AddSeconds(20));
        Assert.Equal(first.RetryAfterUtc, second.RetryAfterUtc);
        Assert.Equal(1, handler.CsvCalls);
    }

    [Fact]
    public async Task Failed_remove_does_not_confirm_stale_absence_when_fresh_refresh_is_rate_limited()
    {
        var handler = new ScriptedMutationHandler(
            HttpStatusCode.TooManyRequests,
            () => CreateCsvResponse("username,role\nAnotherPlayer,member\n"),
            CreateRateLimitedResponse);
        using var httpClient = new HttpClient(handler);
        var configuration = CreateConfiguration("126", includeVerificationCode: true);
        var client = new WiseOldManClient(httpClient, configuration);

        // Prime a valid snapshot which is already missing the member. This is the
        // stale absence that must not confirm a failed DELETE.
        await client.GetMemberRolesAsync(CancellationToken.None);

        var result = await client.RemoveMemberAsync("Gynofobi", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(nameof(WiseOldManRateLimitException), result.Details, StringComparison.Ordinal);
        Assert.Equal(1, handler.DeleteCalls);
        Assert.Equal(2, handler.CsvCalls);
    }

    [Fact]
    public async Task Failed_remove_confirms_absence_only_after_successful_fresh_refresh()
    {
        var handler = new ScriptedMutationHandler(
            HttpStatusCode.TooManyRequests,
            () => CreateCsvResponse("username,role\nGynofobi,officer\n"),
            () => CreateCsvResponse("username,role\nAnotherPlayer,member\n"));
        using var httpClient = new HttpClient(handler);
        var configuration = CreateConfiguration("127", includeVerificationCode: true);
        var client = new WiseOldManClient(httpClient, configuration);

        await client.GetMemberRolesAsync(CancellationToken.None);

        var result = await client.RemoveMemberAsync("Gynofobi", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("not present", result.Details, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.DeleteCalls);
        Assert.Equal(2, handler.CsvCalls);
    }

    [Fact]
    public async Task Fresh_role_lookup_bypasses_valid_cache_and_observes_current_absence()
    {
        var handler = new ScriptedMutationHandler(
            HttpStatusCode.OK,
            () => CreateCsvResponse("username,role\nGynofobi,officer\n"),
            () => CreateCsvResponse("username,role\nAnotherPlayer,member\n"));
        using var httpClient = new HttpClient(handler);
        var configuration = CreateConfiguration("128", includeVerificationCode: false);
        var client = new WiseOldManClient(httpClient, configuration);

        await client.GetMemberRolesAsync(CancellationToken.None);
        var role = await client.GetMemberRoleFreshAsync("Gynofobi", CancellationToken.None);

        Assert.Null(role);
        Assert.Equal(2, handler.CsvCalls);
    }

    [Fact]
    public async Task Fresh_role_lookup_fails_closed_and_suppresses_requests_during_cooldown()
    {
        var handler = new ScriptedMutationHandler(
            HttpStatusCode.OK,
            () => CreateCsvResponse("username,role\nGynofobi,officer\n"),
            CreateRateLimitedResponse);
        using var httpClient = new HttpClient(handler);
        var configuration = CreateConfiguration("129", includeVerificationCode: false);
        var client = new WiseOldManClient(httpClient, configuration);

        await client.GetMemberRolesAsync(CancellationToken.None);
        var first = await Assert.ThrowsAsync<WiseOldManRateLimitException>(
            () => client.GetMemberRoleFreshAsync("Gynofobi", CancellationToken.None));
        var second = await Assert.ThrowsAsync<WiseOldManRateLimitException>(
            () => client.GetMemberRoleFreshAsync("Gynofobi", CancellationToken.None));

        Assert.Equal(first.RetryAfterUtc, second.RetryAfterUtc);
        Assert.Equal(2, handler.CsvCalls);
    }

    [Fact]
    public async Task Fresh_role_lookup_without_group_configuration_fails_closed()
    {
        using var httpClient = new HttpClient(new ScriptedMutationHandler(HttpStatusCode.OK));
        var configuration = new ConfigurationBuilder().Build();
        var client = new WiseOldManClient(httpClient, configuration);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetMemberRoleFreshAsync("Gynofobi", CancellationToken.None));
    }

    private sealed class CacheContendingHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> CsvFetchStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseCsv { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PutCalls { get; private set; }
        public int CsvCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                CsvCalls++;
                if (CsvCalls == 1)
                {
                    CsvFetchStarted.TrySetResult(true);
                    await ReleaseCsv.Task.WaitAsync(cancellationToken);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("username,role\nGynofobi,officer\n")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("username,role\nGynofobi,commander\n")
                };
            }

            if (request.Method == HttpMethod.Put)
            {
                PutCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"role\":\"officer\"}")
                };
            }

            throw new InvalidOperationException($"Unexpected WOM request: {request.Method} {request.RequestUri}");
        }
    }

    private sealed class RateLimitAfterInitialFetchHandler : HttpMessageHandler
    {
        public int CsvCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get)
            {
                throw new InvalidOperationException($"Unexpected WOM request: {request.Method} {request.RequestUri}");
            }

            CsvCalls++;
            if (CsvCalls == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("username,role\nGynofobi,officer\n")
                });
            }

            return Task.FromResult(CreateRateLimitedResponse());
        }
    }

    private sealed class AlwaysRateLimitedHandler : HttpMessageHandler
    {
        public int CsvCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get)
            {
                throw new InvalidOperationException($"Unexpected WOM request: {request.Method} {request.RequestUri}");
            }

            CsvCalls++;
            return Task.FromResult(CreateRateLimitedResponse());
        }
    }

    private sealed class ScriptedMutationHandler(
        HttpStatusCode deleteStatus,
        params Func<HttpResponseMessage>[] csvResponses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _csvResponses = new(csvResponses);

        public int CsvCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                CsvCalls++;
                if (!_csvResponses.TryDequeue(out var responseFactory))
                {
                    throw new InvalidOperationException("No scripted CSV response remains.");
                }

                return Task.FromResult(responseFactory());
            }

            if (request.Method == HttpMethod.Delete)
            {
                DeleteCalls++;
                return Task.FromResult(new HttpResponseMessage(deleteStatus)
                {
                    Content = new StringContent("delete response")
                });
            }

            throw new InvalidOperationException($"Unexpected WOM request: {request.Method} {request.RequestUri}");
        }
    }

    private static IConfiguration CreateConfiguration(string groupId, bool includeVerificationCode)
    {
        var values = new Dictionary<string, string?>
        {
            ["WiseOldMan:GroupId"] = groupId
        };
        if (includeVerificationCode)
        {
            values["WiseOldMan:VerificationCode"] = "test-code";
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static HttpResponseMessage CreateCsvResponse(string csv) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(csv)
        };

    private static HttpResponseMessage CreateRateLimitedResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
        return response;
    }
}
