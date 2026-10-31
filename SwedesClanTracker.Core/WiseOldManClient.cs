using System.Net;
using Microsoft.Extensions.Configuration;

namespace SwedesClanTracker.Core;

public interface IWiseOldManClient
{
    Task<string?> GetMemberRoleAsync(string username, CancellationToken ct);
    /// <summary>
    /// Reads a member role from a successful, current Wise Old Man roster fetch.
    /// Implementations must not use a stale or cached snapshot to answer this call.
    /// Implementations that do not support authoritative observations fail closed
    /// instead of forwarding a cached role lookup.
    /// </summary>
    Task<string?> GetMemberRoleFreshAsync(string username, CancellationToken ct) =>
        throw new NotSupportedException("Fresh Wise Old Man role observations are not supported by this client.");
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

public sealed class WiseOldManRateLimitException : Exception
{
    public WiseOldManRateLimitException(DateTimeOffset retryAfterUtc, string message)
        : base(message)
    {
        RetryAfterUtc = retryAfterUtc;
    }

    public DateTimeOffset RetryAfterUtc { get; }
}

public class WiseOldManClient(HttpClient httpClient, IConfiguration configuration) : IWiseOldManClient
{
    private sealed record RoleCacheSnapshot(
        int GroupId,
        DateTimeOffset ValidUntilUtc,
        IReadOnlyDictionary<string, string> Roles,
        long Generation);

    private static readonly SemaphoreSlim CacheLock = new(1, 1);
    private static readonly IReadOnlyDictionary<string, string> EmptyRoles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static RoleCacheSnapshot _cache = new(-1, DateTimeOffset.MinValue, EmptyRoles, 0);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinimumFetchBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumFetchBackoff = TimeSpan.FromMinutes(10);
    private static DateTimeOffset _retryNotBeforeUtc = DateTimeOffset.MinValue;
    private static int _consecutiveFetchFailures;
    private static int _retryGroupId = -1;

    public async Task<string?> GetMemberRoleAsync(string username, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        var roles = await GetRolesAsync(ct);
        var normalized = NormalizeUsername(username);
        return roles.TryGetValue(normalized, out var role) ? role : null;
    }

    public async Task<string?> GetMemberRoleFreshAsync(string username, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        var roles = await GetRolesAsync(ct, requireFresh: true);
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

            if (!response.IsSuccessStatusCode)
            {
                var failedResult = new WomRoleUpdateResult(
                    false,
                    (int)response.StatusCode,
                    $"WOM role update failed ({(int)response.StatusCode}): {Truncate(responseText, 180)}",
                    null,
                    null,
                    null);

                if (invalidateCache)
                {
                    await TryInvalidateCacheAsync(CancellationToken.None);
                }

                return failedResult;
            }

            var updatedRole = normalizedRole;
            int? womPlayerId = null;
            string? displayName = null;
            if (TryReadWomRoleUpdateResponse(responseText, out var parsedUpdatedRole, out womPlayerId, out displayName))
            {
                updatedRole = parsedUpdatedRole;
            }

            var successfulResult = new WomRoleUpdateResult(
                true,
                (int)response.StatusCode,
                "Role update accepted by WiseOldMan.",
                updatedRole,
                womPlayerId,
                displayName);

            if (invalidateCache)
            {
                // A successful PUT is authoritative. Cache invalidation is maintenance and
                // must not turn an acknowledged role update into a failed approval.
                await TryInvalidateCacheAsync(CancellationToken.None);
            }

            return successfulResult;
        }
        catch (Exception ex)
        {
            return new WomRoleUpdateResult(false, 0, $"WOM role update exception: {ex.GetType().Name}", null, null, null);
        }
    }

    private async Task TryInvalidateCacheAsync(CancellationToken ct)
    {
        try
        {
            await InvalidateCacheAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // The role PUT already completed. Cache invalidation can be retried by the
            // next sync or an explicit cache refresh.
        }
        catch
        {
            // Cache maintenance must not change the result of the WOM mutation.
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

            // A failed DELETE can only be treated as idempotent success after a
            // successful fresh roster observation. A cached/stale absence is not
            // enough because the snapshot may predate the failed mutation.
            var roleAfter = await GetMemberRoleFreshAsync(normalizedUsername, ct);
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

    public Task InvalidateCacheAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequestCacheInvalidation();
        return Task.CompletedTask;
    }

    private async Task<IReadOnlyDictionary<string, string>> GetRolesAsync(
        CancellationToken ct,
        bool requireFresh = false)
    {
        var groupId = configuration.GetValue<int?>("WiseOldMan:GroupId") ?? 0;
        if (groupId <= 0)
        {
            if (requireFresh)
            {
                throw new InvalidOperationException("Wise Old Man group settings are missing for a fresh roster observation.");
            }

            return EmptyRoles;
        }

        var now = DateTimeOffset.UtcNow;
        var cache = Volatile.Read(ref _cache);
        if (!requireFresh && cache.GroupId == groupId && now <= cache.ValidUntilUtc)
        {
            return cache.Roles;
        }

        await CacheLock.WaitAsync(ct);
        try
        {
            now = DateTimeOffset.UtcNow;
            cache = Volatile.Read(ref _cache);
            if (!requireFresh && cache.GroupId == groupId && now <= cache.ValidUntilUtc)
            {
                return cache.Roles;
            }

            if (_retryGroupId == groupId && now < _retryNotBeforeUtc)
            {
                if (!requireFresh && HasUsableCache(cache, groupId))
                {
                    return cache.Roles;
                }

                throw new WiseOldManRateLimitException(
                    _retryNotBeforeUtc,
                    $"Wise Old Man fetch is cooling down until {_retryNotBeforeUtc:O}.");
            }

            var fetchGeneration = cache.Generation;
            try
            {
                using var response = await httpClient.GetAsync(
                    $"https://api.wiseoldman.net/v2/groups/{groupId}/csv",
                    ct);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var responseReceivedAt = DateTimeOffset.UtcNow;
                    var retryAfter = GetRetryAfter(response, responseReceivedAt);
                    var retryAfterUtc = RegisterFetchFailure(groupId, responseReceivedAt, retryAfter);
                    if (!requireFresh && HasUsableCache(cache, groupId))
                    {
                        return cache.Roles;
                    }

                    throw new WiseOldManRateLimitException(
                        retryAfterUtc,
                        $"Wise Old Man rate limited the roster fetch until {retryAfterUtc:O}.");
                }

                response.EnsureSuccessStatusCode();
                var csv = await response.Content.ReadAsStringAsync(ct);
                var newCache = ParseRoles(csv);

                var fetchedCache = new RoleCacheSnapshot(
                    groupId,
                    DateTimeOffset.UtcNow.Add(CacheTtl),
                    newCache,
                    fetchGeneration);
                Interlocked.CompareExchange(ref _cache, fetchedCache, cache);
                ResetFetchFailures(groupId);
                return newCache;
            }
            catch (WiseOldManRateLimitException)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                RegisterFetchFailure(groupId, DateTimeOffset.UtcNow, null);
                if (!requireFresh && HasUsableCache(cache, groupId))
                {
                    return cache.Roles;
                }

                throw;
            }
        }
        finally
        {
            CacheLock.Release();
        }
    }

    private static IReadOnlyDictionary<string, string> ParseRoles(string csv)
    {
        var newCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

        return newCache;
    }

    private static bool HasUsableCache(RoleCacheSnapshot cache, int groupId) =>
        cache.GroupId == groupId;

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response, DateTimeOffset responseReceivedAt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var untilDate = date - responseReceivedAt;
            if (untilDate > TimeSpan.Zero) return untilDate;
        }

        return null;
    }

    private static DateTimeOffset RegisterFetchFailure(int groupId, DateTimeOffset responseReceivedAt, TimeSpan? requestedDelay)
    {
        if (_retryGroupId != groupId)
        {
            _retryGroupId = groupId;
            _consecutiveFetchFailures = 0;
            _retryNotBeforeUtc = DateTimeOffset.MinValue;
        }

        _consecutiveFetchFailures = Math.Min(_consecutiveFetchFailures + 1, 6);
        var exponentialSeconds = MinimumFetchBackoff.TotalSeconds * Math.Pow(2, _consecutiveFetchFailures - 1);
        var delaySeconds = requestedDelay is { } serverDelay
            ? Math.Max(MinimumFetchBackoff.TotalSeconds, serverDelay.TotalSeconds)
            : Math.Min(MaximumFetchBackoff.TotalSeconds, Math.Max(MinimumFetchBackoff.TotalSeconds, exponentialSeconds));
        _retryNotBeforeUtc = responseReceivedAt.AddSeconds(delaySeconds);
        return _retryNotBeforeUtc;
    }

    private static void ResetFetchFailures(int groupId)
    {
        _retryGroupId = groupId;
        _consecutiveFetchFailures = 0;
        _retryNotBeforeUtc = DateTimeOffset.MinValue;
    }

    private static void RequestCacheInvalidation()
    {
        while (true)
        {
            var current = Volatile.Read(ref _cache);
            var invalidatedCache = current with
            {
                ValidUntilUtc = DateTimeOffset.MinValue,
                Generation = current.Generation + 1
            };
            if (ReferenceEquals(Interlocked.CompareExchange(ref _cache, invalidatedCache, current), current))
            {
                return;
            }
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
