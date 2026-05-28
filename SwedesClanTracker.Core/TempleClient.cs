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
    private static readonly string[] ModeEhbKeys = ["Ehb", "Im_ehb", "Uim_ehb", "1def_ehb"];
    private static readonly string[] ModeEhpKeys = ["Ehp", "Im_ehp", "Uim_ehp", "1def_ehp"];

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
        var ehb = ResolvePrimaryEhb(data);
        if (!ehb.HasValue) return null;
        var ehp = ResolvePrimaryEhp(data);
        if (!ehp.HasValue) return null;
        int collections = data.TryGetProperty("Collections", out var c) ? c.GetInt32() : 0;
        return new PlayerStatsDto(lvl, ehb.Value, ehp.Value, collections);
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

    private static double? ResolvePrimaryEhb(JsonElement data)
    {
        if (data.TryGetProperty("info", out var info) &&
            info.ValueKind == JsonValueKind.Object &&
            info.TryGetProperty("Primary_ehb", out var primaryEhbProperty) &&
            primaryEhbProperty.ValueKind == JsonValueKind.String)
        {
            var primaryEhbKey = primaryEhbProperty.GetString();
            if (!string.IsNullOrWhiteSpace(primaryEhbKey) &&
                TryReadNumericProperty(data, primaryEhbKey, out var primaryEhb))
            {
                return primaryEhb;
            }
        }

        return TryReadHighestModeEhb(data, out var fallbackEhb) ? fallbackEhb : null;
    }

    private static double? ResolvePrimaryEhp(JsonElement data)
    {
        if (data.TryGetProperty("info", out var info) &&
            info.ValueKind == JsonValueKind.Object)
        {
            if (info.TryGetProperty("Primary_ehp", out var primaryEhpProperty) &&
                primaryEhpProperty.ValueKind == JsonValueKind.String)
            {
                var primaryEhpKey = primaryEhpProperty.GetString();
                if (!string.IsNullOrWhiteSpace(primaryEhpKey) &&
                    TryReadNumericProperty(data, primaryEhpKey, out var primaryEhp))
                {
                    return primaryEhp;
                }
            }

            // Backward-compatible fallback if Temple only provides Primary_ehb:
            // derive the corresponding EHP key for the same build.
            if (info.TryGetProperty("Primary_ehb", out var primaryEhbProperty) &&
                primaryEhbProperty.ValueKind == JsonValueKind.String)
            {
                var primaryEhbKey = primaryEhbProperty.GetString();
                if (!string.IsNullOrWhiteSpace(primaryEhbKey))
                {
                    var derivedEhpKey = primaryEhbKey.EndsWith("_ehb", StringComparison.OrdinalIgnoreCase)
                        ? primaryEhbKey[..^4] + "_ehp"
                        : primaryEhbKey == "Ehb" ? "Ehp" : "";
                    if (!string.IsNullOrWhiteSpace(derivedEhpKey) &&
                        TryReadNumericProperty(data, derivedEhpKey, out var derivedEhp))
                    {
                        return derivedEhp;
                    }
                }
            }
        }

        if (TryReadHighestModeEhp(data, out var fallbackEhp)) return fallbackEhp;
        return TryReadNumericProperty(data, "Overall_ehp", out var overallEhp) ? overallEhp : null;
    }

    private static bool TryReadHighestModeEhb(JsonElement data, out double ehb)
    {
        ehb = 0;
        var foundValue = false;

        foreach (var key in ModeEhbKeys)
        {
            if (!TryReadNumericProperty(data, key, out var value)) continue;
            if (!foundValue || value > ehb)
            {
                ehb = value;
                foundValue = true;
            }
        }

        return foundValue;
    }

    private static bool TryReadHighestModeEhp(JsonElement data, out double ehp)
    {
        ehp = 0;
        var foundValue = false;

        foreach (var key in ModeEhpKeys)
        {
            if (!TryReadNumericProperty(data, key, out var value)) continue;
            if (!foundValue || value > ehp)
            {
                ehp = value;
                foundValue = true;
            }
        }

        return foundValue;
    }

    private static bool TryReadNumericProperty(JsonElement parent, string propertyName, out double value)
    {
        value = 0;
        if (!parent.TryGetProperty(propertyName, out var property)) return false;
        if (property.ValueKind != JsonValueKind.Number) return false;
        return property.TryGetDouble(out value);
    }
}
