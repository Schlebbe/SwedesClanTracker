using System.Net;
using Microsoft.Extensions.Configuration;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.Core.Tests;

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
}
