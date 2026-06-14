using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;

namespace SwedesClanTracker.Core;

public interface IWiseOldManClient
{
    Task<string?> GetMemberRoleAsync(string username, CancellationToken ct);
    Task<IReadOnlyDictionary<string, string>> GetMemberRolesAsync(CancellationToken ct);
    Task<bool> IsImpAccountAsync(string username, CancellationToken ct);
    Task<WomRoleUpdateResult> UpdateMemberRoleAsync(string username, string role, CancellationToken ct, bool invalidateCache = true);
    Task<(bool Success, string Details)> RemoveMemberAsync(string username, CancellationToken ct);
    Task InvalidateCacheAsync(CancellationToken ct);
}

public sealed record WomRoleUpdateResult(
    bool Success,
    int HttpStatus,
    string Details,
    string? UpdatedRole,
    int? WomPlayerId,
    string? DisplayName);

public class WiseOldManClient(HttpClient httpClient, IConfiguration configuration) : IWiseOldManClient
{
    private static readonly SemaphoreSlim CacheLock = new(1, 1);
    private static DateTimeOffset _cacheValidUntilUtc = DateTimeOffset.MinValue;
    private static int _cachedGroupId = -1;
    private static ConcurrentDictionary<string, string> _roleCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public async Task<string?> GetMemberRoleAsync(string username, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        var roles = await GetRolesAsync(ct);
        var normalized = NormalizeUsername(username);
        return roles.TryGetValue(normalized, out var role) ? role : null;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetMemberRolesAsync(CancellationToken ct)
    {
        var roles = await GetRolesAsync(ct);
        return new Dictionary<string, string>(roles, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<bool> IsImpAccountAsync(string username, CancellationToken ct)
    {
        var role = await GetMemberRoleAsync(username, ct);
        return string.Equals(role, "imp", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<WomRoleUpdateResult> UpdateMemberRoleAsync(string username, string role, CancellationToken ct, bool invalidateCache = true)
    {
        var normalizedUsername = NormalizeUsername(username);
        var normalizedRole = RankRules.NormalizeRankName(role);
        if (string.IsNullOrWhiteSpace(normalizedUsername))
        {
            return new WomRoleUpdateResult(false, 0, "Username is empty.", null, null, null);
        }
        if (string.IsNullOrWhiteSpace(normalizedRole))
        {
            return new WomRoleUpdateResult(false, 0, "Role is empty.", null, null, null);
        }

        var groupId = configuration.GetValue<int?>("WiseOldMan:GroupId") ?? 0;
        var verificationCode = configuration["WiseOldMan:VerificationCode"] ?? "";
        if (groupId <= 0 || string.IsNullOrWhiteSpace(verificationCode))
        {
            return new WomRoleUpdateResult(false, 0, "WiseOldMan settings missing.", null, null, null);
        }

        var updateBody = System.Text.Json.JsonSerializer.Serialize(new
        {
            verificationCode,
            username = normalizedUsername,
            role = normalizedRole.ToLowerInvariant()
        });
        var request = new HttpRequestMessage(HttpMethod.Put, $"https://api.wiseoldman.net/v2/groups/{groupId}/role")
        {
            Content = new StringContent(updateBody, System.Text.Encoding.UTF8, "application/json")
        };

        try
        {
            var response = await httpClient.SendAsync(request, ct);
            var responseText = await response.Content.ReadAsStringAsync(ct);
            if (invalidateCache)
            {
                await InvalidateCacheAsync(ct);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new WomRoleUpdateResult(
                    false,
                    (int)response.StatusCode,
                    $"WOM role update failed ({(int)response.StatusCode}): {Truncate(responseText, 180)}",
                    null,
                    null,
                    null);
            }

            var updatedRole = normalizedRole;
            int? womPlayerId = null;
            string? displayName = null;
            if (TryReadWomRoleUpdateResponse(responseText, out var parsedUpdatedRole, out womPlayerId, out displayName))
            {
                updatedRole = parsedUpdatedRole;
            }

            return new WomRoleUpdateResult(
                true,
                (int)response.StatusCode,
                "Role update accepted by WiseOldMan.",
                updatedRole,
                womPlayerId,
                displayName);
        }
        catch (Exception ex)
        {
            return new WomRoleUpdateResult(false, 0, $"WOM role update exception: {ex.GetType().Name}", null, null, null);
        }
    }

    public async Task<(bool Success, string Details)> RemoveMemberAsync(string username, CancellationToken ct)
    {
        var normalizedUsername = NormalizeUsername(username);
        if (string.IsNullOrWhiteSpace(normalizedUsername)) return (false, "Username is empty.");

        var groupId = configuration.GetValue<int?>("WiseOldMan:GroupId") ?? 0;
        var verificationCode = configuration["WiseOldMan:VerificationCode"] ?? "";
        if (groupId <= 0 || string.IsNullOrWhiteSpace(verificationCode))
        {
            return (false, "WiseOldMan settings missing.");
        }

        var removeBody = System.Text.Json.JsonSerializer.Serialize(new
        {
            verificationCode,
            members = new[] { normalizedUsername }
        });
        var request = new HttpRequestMessage(HttpMethod.Delete, $"https://api.wiseoldman.net/v2/groups/{groupId}/members")
        {
            Content = new StringContent(removeBody, System.Text.Encoding.UTF8, "application/json")
        };

        try
        {
            var response = await httpClient.SendAsync(request, ct);
            var responseText = await response.Content.ReadAsStringAsync(ct);
            await InvalidateCacheAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                return (true, "Removed from WOM.");
            }

            var roleAfter = await GetMemberRoleAsync(normalizedUsername, ct);
            if (string.IsNullOrWhiteSpace(roleAfter))
            {
                return (true, $"WOM remove returned {(int)response.StatusCode}, but member is not present.");
            }
            return (false, $"WOM remove failed ({(int)response.StatusCode}): {Truncate(responseText, 180)}");
        }
        catch (Exception ex)
        {
            return (false, $"WOM remove exception: {ex.GetType().Name}");
        }
    }

    public async Task InvalidateCacheAsync(CancellationToken ct)
    {
        await CacheLock.WaitAsync(ct);
        try
        {
            _cachedGroupId = -1;
            _roleCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _cacheValidUntilUtc = DateTimeOffset.MinValue;
        }
        finally
        {
            CacheLock.Release();
        }
    }

    private async Task<ConcurrentDictionary<string, string>> GetRolesAsync(CancellationToken ct)
    {
        var groupId = configuration.GetValue<int?>("WiseOldMan:GroupId") ?? 0;
        if (groupId <= 0) return new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var now = DateTimeOffset.UtcNow;
        if (_cachedGroupId == groupId && now <= _cacheValidUntilUtc && _roleCache.Count > 0)
        {
            return _roleCache;
        }

        await CacheLock.WaitAsync(ct);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_cachedGroupId == groupId && now <= _cacheValidUntilUtc && _roleCache.Count > 0)
            {
                return _roleCache;
            }

            var csv = await httpClient.GetStringAsync($"https://api.wiseoldman.net/v2/groups/{groupId}/csv", ct);
            var newCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = 1; i < lines.Length; i++)
            {
                var fields = ParseCsvLine(lines[i]);
                if (fields.Count < 2) continue;
                var username = NormalizeUsername(fields[0]);
                var role = fields[1].Trim();
                if (string.IsNullOrWhiteSpace(username)) continue;
                newCache[username] = role;
            }

            _cachedGroupId = groupId;
            _roleCache = newCache;
            _cacheValidUntilUtc = DateTimeOffset.UtcNow.Add(CacheTtl);
            return _roleCache;
        }
        finally
        {
            CacheLock.Release();
        }
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                fields.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(ch);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    private static string NormalizeUsername(string input) =>
        UsernameRules.NormalizeUsername(input);

    private static bool TryReadWomRoleUpdateResponse(string json, out string updatedRole, out int? womPlayerId, out string? displayName)
    {
        updatedRole = "";
        womPlayerId = null;
        displayName = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("role", out var roleProp))
            {
                updatedRole = roleProp.GetString() ?? "";
            }
            if (doc.RootElement.TryGetProperty("playerId", out var playerIdProp) && playerIdProp.TryGetInt32(out var parsedPlayerId))
            {
                womPlayerId = parsedPlayerId;
            }
            if (doc.RootElement.TryGetProperty("displayName", out var displayNameProp))
            {
                displayName = displayNameProp.GetString();
            }
            return !string.IsNullOrWhiteSpace(updatedRole) || womPlayerId.HasValue || !string.IsNullOrWhiteSpace(displayName);
        }
        catch
        {
            return false;
        }
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return value.Length <= max ? value : value[..max];
    }
}
