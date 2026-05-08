using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;

namespace SwedesClanTracker.Core;

public interface IWiseOldManClient
{
    Task<string?> GetMemberRoleAsync(string username, CancellationToken ct);
    Task<IReadOnlyDictionary<string, string>> GetMemberRolesAsync(CancellationToken ct);
    Task<bool> IsImpAccountAsync(string username, CancellationToken ct);
    Task InvalidateCacheAsync(CancellationToken ct);
}

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
        input.Replace('_', ' ').Trim();
}
