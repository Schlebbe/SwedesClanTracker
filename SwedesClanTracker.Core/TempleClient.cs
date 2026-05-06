using System.Text.Json;

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
    public async Task<IReadOnlyList<string>> GetRosterAsync(CancellationToken ct)
    {
        var raw = await httpClient.GetStringAsync("https://templeosrs.com/api/groupmembers.php?id=449", ct);
        return JsonSerializer.Deserialize<List<string>>(raw) ?? [];
    }

    public async Task<PlayerStatsDto?> GetPlayerStatsAsync(string username, CancellationToken ct)
    {
        var raw = await httpClient.GetStringAsync($"https://templeosrs.com/api/player_stats.php?player={Uri.EscapeDataString(username)}&bosses=1", ct);
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
        using var response = await httpClient.GetAsync(url, ct);
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
}
