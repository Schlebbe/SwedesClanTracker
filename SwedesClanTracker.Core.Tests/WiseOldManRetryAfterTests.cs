using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.Core.Tests;

[Collection("WiseOldManClient")]
public sealed class WiseOldManRetryAfterTests
{
    [Fact]
    public async Task DeltaRetryAfter_AnchorsDeadlineAtResponseReceipt()
    {
        var handler = new RateLimitHandler(
            TimeSpan.FromSeconds(3),
            retryAfterDelta: TimeSpan.FromSeconds(60));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient, 901);

        var first = await Assert.ThrowsAsync<WiseOldManRateLimitException>(
            () => client.GetMemberRolesAsync(CancellationToken.None));
        var requestCompletedAt = DateTimeOffset.UtcNow;

        Assert.True(handler.ResponseReturnedAt.HasValue);
        var responseReturnedAt = handler.ResponseReturnedAt!.Value;
        Assert.InRange(
            first.RetryAfterUtc,
            responseReturnedAt.AddSeconds(60),
            requestCompletedAt.AddSeconds(60));

        var second = await Assert.ThrowsAsync<WiseOldManRateLimitException>(
            () => client.GetMemberRolesAsync(CancellationToken.None));

        Assert.Equal(first.RetryAfterUtc, second.RetryAfterUtc);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task AbsoluteRetryAfter_UsesAdvertisedUtcDeadline()
    {
        var retryAt = DateTimeOffset.UtcNow.AddMinutes(2);
        var handler = new RateLimitHandler(
            TimeSpan.Zero,
            retryAfterDate: retryAt);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient, 902);

        var exception = await Assert.ThrowsAsync<WiseOldManRateLimitException>(
            () => client.GetMemberRolesAsync(CancellationToken.None));

        Assert.InRange(
            exception.RetryAfterUtc,
            retryAt.AddMilliseconds(-1),
            retryAt.AddMilliseconds(1));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task MissingRetryAfter_UsesFallbackAndSuppressesFollowUpRequest()
    {
        var handler = new RateLimitHandler(TimeSpan.Zero);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient, 903);

        var first = await Assert.ThrowsAsync<WiseOldManRateLimitException>(
            () => client.GetMemberRolesAsync(CancellationToken.None));
        var requestCompletedAt = DateTimeOffset.UtcNow;
        Assert.True(handler.ResponseReturnedAt.HasValue);
        var responseReturnedAt = handler.ResponseReturnedAt!.Value;

        Assert.InRange(
            first.RetryAfterUtc,
            responseReturnedAt.AddSeconds(30),
            requestCompletedAt.AddSeconds(30));

        var second = await Assert.ThrowsAsync<WiseOldManRateLimitException>(
            () => client.GetMemberRolesAsync(CancellationToken.None));

        Assert.Equal(first.RetryAfterUtc, second.RetryAfterUtc);
        Assert.Equal(1, handler.RequestCount);
    }

    private static WiseOldManClient CreateClient(HttpClient httpClient, int groupId)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WiseOldMan:GroupId"] = groupId.ToString()
            })
            .Build();

        return new WiseOldManClient(httpClient, configuration);
    }

    private sealed class RateLimitHandler(
        TimeSpan responseDelay,
        TimeSpan? retryAfterDelta = null,
        DateTimeOffset? retryAfterDate = null) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public DateTimeOffset? ResponseReturnedAt { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            RequestCount++;
            await Task.Delay(responseDelay, cancellationToken);

            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            if (retryAfterDelta is { } delta)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(delta);
            }
            else if (retryAfterDate is { } date)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(date);
            }

            ResponseReturnedAt = DateTimeOffset.UtcNow;
            return response;
        }
    }
}
