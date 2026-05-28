using System.Net;
using System.Net.Http;
using System.Text;

namespace SwedesClanTracker.Core.Tests;

public class TempleClientTests
{
    [Fact]
    public async Task GetPlayerStatsAsync_UsesPrimaryEhbFieldWhenPresent()
    {
        var client = CreateClient("""
            {
              "data": {
                "info": {
                  "Primary_ehb": "Im_ehb",
                  "Primary_ehp": "Im_ehp"
                },
                "Overall_level": 2100,
                "Overall_ehp": 456.7,
                "Collections": 99,
                "Ehb": 10.0,
                "Im_ehb": 45.5,
                "Uim_ehb": 2.2,
                "1def_ehb": 1.1,
                "Ehp": 400.0,
                "Im_ehp": 567.8,
                "Uim_ehp": 320.0,
                "1def_ehp": 250.0
              }
            }
            """);

        var stats = await client.GetPlayerStatsAsync("Example", CancellationToken.None);

        Assert.NotNull(stats);
        Assert.Equal(45.5, stats!.Ehb, 6);
        Assert.Equal(567.8, stats.Ehp, 6);
    }

    [Fact]
    public async Task GetPlayerStatsAsync_FallsBackToHighestModeEhbWhenPrimaryIsInvalid()
    {
        var client = CreateClient("""
            {
              "data": {
                "info": {
                  "Primary_ehb": "NotARealMode",
                  "Primary_ehp": "AlsoNotReal"
                },
                "Overall_level": 2000,
                "Overall_ehp": 300.0,
                "Collections": 50,
                "Ehb": 100.0,
                "Im_ehb": 250.0,
                "Uim_ehb": 150.0,
                "1def_ehb": 125.0,
                "Ehp": 200.0,
                "Im_ehp": 350.0,
                "Uim_ehp": 320.0,
                "1def_ehp": 290.0
              }
            }
            """);

        var stats = await client.GetPlayerStatsAsync("Example", CancellationToken.None);

        Assert.NotNull(stats);
        Assert.Equal(250.0, stats!.Ehb, 6);
        Assert.Equal(350.0, stats.Ehp, 6);
    }

    [Fact]
    public async Task GetPlayerStatsAsync_IgnoresBossEhbFieldsDuringFallback()
    {
        var client = CreateClient("""
            {
              "data": {
                "info": {},
                "Overall_level": 1900,
                "Overall_ehp": 222.2,
                "Ehb": 3.0,
                "Im_ehb": 5.0,
                "Uim_ehb": 4.0,
                "1def_ehb": 2.0,
                "TzTok-Jad_ehb": 9000.0,
                "Ehp": 120.0,
                "Im_ehp": 140.0,
                "Uim_ehp": 130.0,
                "1def_ehp": 110.0,
                "TzTok-Jad_ehp": 9000.0
              }
            }
            """);

        var stats = await client.GetPlayerStatsAsync("Example", CancellationToken.None);

        Assert.NotNull(stats);
        Assert.Equal(5.0, stats!.Ehb, 6);
        Assert.Equal(140.0, stats.Ehp, 6);
    }

    [Fact]
    public async Task GetPlayerStatsAsync_ReturnsNullWhenNoNumericModeEhbExists()
    {
        var client = CreateClient("""
            {
              "data": {
                "info": {
                  "Primary_ehb": "Im_ehb"
                },
                "Overall_level": 1900,
                "Overall_ehp": "bad",
                "Ehb": "bad",
                "Im_ehb": null,
                "Uim_ehb": {},
                "1def_ehb": [],
                "Ehp": null,
                "Im_ehp": {},
                "Uim_ehp": [],
                "1def_ehp": "bad"
              }
            }
            """);

        var stats = await client.GetPlayerStatsAsync("Example", CancellationToken.None);

        Assert.Null(stats);
    }

    [Fact]
    public async Task GetPlayerStatsAsync_UsesPrimaryEhpFieldWhenPresent()
    {
        var client = CreateClient("""
            {
              "data": {
                "info": {
                  "Primary_ehb": "Ehb",
                  "Primary_ehp": "Uim_ehp"
                },
                "Overall_level": 2200,
                "Overall_ehp": 777.123,
                "Ehp": 9999.0,
                "Im_ehp": 8888.0,
                "Uim_ehp": 4444.0,
                "Collections": 42,
                "Ehb": 11.0,
                "Im_ehb": 22.0,
                "Uim_ehb": 33.0,
                "1def_ehb": 44.0
              }
            }
            """);

        var stats = await client.GetPlayerStatsAsync("Example", CancellationToken.None);

        Assert.NotNull(stats);
        Assert.Equal(4444.0, stats!.Ehp, 6);
    }

    [Fact]
    public async Task GetPlayerStatsAsync_DerivesPrimaryEhpFromPrimaryEhbWhenPrimaryEhpMissing()
    {
        var client = CreateClient("""
            {
              "data": {
                "info": {
                  "Primary_ehb": "1def_ehb"
                },
                "Overall_level": 2200,
                "Overall_ehp": 777.123,
                "Ehp": 9999.0,
                "Im_ehp": 8888.0,
                "Uim_ehp": 4444.0,
                "1def_ehp": 1234.5,
                "Collections": 42,
                "Ehb": 11.0,
                "Im_ehb": 22.0,
                "Uim_ehb": 33.0,
                "1def_ehb": 44.0
              }
            }
            """);

        var stats = await client.GetPlayerStatsAsync("Example", CancellationToken.None);

        Assert.NotNull(stats);
        Assert.Equal(1234.5, stats!.Ehp, 6);
    }

    private static TempleClient CreateClient(string json)
    {
        var handler = new StubHttpMessageHandler(json);
        var httpClient = new HttpClient(handler);
        return new TempleClient(httpClient);
    }

    private sealed class StubHttpMessageHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }
}
