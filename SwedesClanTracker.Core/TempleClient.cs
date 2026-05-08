using System.Text.Json;
using System.Net.Sockets;

namespace SwedesClanTracker.Core;

public record PlayerStatsDto(int TotalLevel, double Ehb, double Ehp, int Collections);

public interface ITempleClient
{
    Task<IReadOnlyList<string>> GetRosterAsync(CancellationToken ct);
    Task<PlayerStatsDto?> GetPlayerStatsAsync(string username, CancellationToken ct);
    Task<int?> GetPetsAsync(string username, CancellationToken ct);
}

public class TempleClient(HttpClient httpClient) : ITempleClient
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3)
    ];

    public async Task<IReadOnlyList<string>> GetRosterAsync(CancellationToken ct)
    {
        var raw = await GetStringWithRetryAsync("https://templeosrs.com/api/groupmembers.php?id=449", ct);
        return JsonSerializer.Deserialize<List<string>>(raw) ?? [];
    }

    public async Task<PlayerStatsDto?> GetPlayerStatsAsync(string username, CancellationToken ct)
    {
        var raw = await GetStringWithRetryAsync($"https://templeosrs.com/api/player_stats.php?player={Uri.EscapeDataString(username)}&bosses=1", ct);
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
        int lvl = data.GetProperty("Overall_level").GetInt32();
        double ehb = data.GetProperty("Ehb").GetDouble();
        double ehp = data.GetProperty("Overall_ehp").GetDouble();
        int collections = data.TryGetProperty("Collections", out var c) ? c.GetInt32() : 0;
        return new PlayerStatsDto(lvl, ehb, ehp, collections);
    }

    public async Task<int?> GetPetsAsync(string username, CancellationToken ct)
    {
        var url = $"https://templeosrs.com/api/collection-log/player_collection_log.php?player={Uri.EscapeDataString(username)}&categories=all_pets";
        using var response = await SendWithRetryAsync(url, ct);
        if ((int)response.StatusCode == 402) return null;
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("error", out _)) return null;
        if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
        if (data.TryGetProperty("items", out var items) &&
            items.TryGetProperty("all_pets", out var pets))
        {
            return pets.ValueKind switch
            {
                JsonValueKind.Array => pets.GetArrayLength(),
                JsonValueKind.Object => pets.EnumerateObject().Count(),
                _ => null
            };
        }

        if (data.TryGetProperty("total_collections_in_response", out var total) &&
            total.ValueKind == JsonValueKind.Number &&
            total.TryGetInt32(out var count))
        {
            return count;
        }

        return null;
    }

    private async Task<string> GetStringWithRetryAsync(string url, CancellationToken ct)
    {
        Exception? lastException = null;

        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            try
            {
                return await httpClient.GetStringAsync(url, ct);
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < RetryDelays.Length && !ct.IsCancellationRequested)
            {
                lastException = ex;
                await Task.Delay(RetryDelays[attempt], ct);
            }
        }

        throw lastException ?? new HttpRequestException("Temple API request failed.");
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(string url, CancellationToken ct)
    {
        Exception? lastException = null;

        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            try
            {
                return await httpClient.GetAsync(url, ct);
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < RetryDelays.Length && !ct.IsCancellationRequested)
            {
                lastException = ex;
                await Task.Delay(RetryDelays[attempt], ct);
            }
        }

        throw lastException ?? new HttpRequestException("Temple API request failed.");
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is HttpRequestException) return true;
        if (ex is TimeoutException) return true;
        if (ex is TaskCanceledException) return true;
        if (ex is SocketException) return true;
        if (ex.InnerException is SocketException) return true;
        return false;
    }
}
