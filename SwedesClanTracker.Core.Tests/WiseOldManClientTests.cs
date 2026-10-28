using System.Net;
using Microsoft.Extensions.Configuration;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.Core.Tests;

public sealed class WiseOldManClientTests
{
    [Fact]
    public async Task Successful_role_put_is_not_failed_when_cache_invalidation_times_out()
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

        using var putCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var result = await client.UpdateMemberRoleAsync("Gynofobi", "Officer", putCts.Token);

        Assert.True(result.Success);
        Assert.Equal(200, result.HttpStatus);
        Assert.Equal(1, handler.PutCalls);

        csvCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await csvFetch);
    }

    private sealed class CacheContendingHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> CsvFetchStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PutCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                CsvFetchStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (request.Method == HttpMethod.Put)
            {
                PutCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"role\":\"officer\"}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("username,role\nGynofobi,officer\n")
            };
        }
    }
}
