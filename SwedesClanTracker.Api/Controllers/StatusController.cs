using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/status")]
public class StatusController(TrackerDbContext db) : ControllerBase
{
    private static readonly TimeSpan RecentEventWindow = TimeSpan.FromMinutes(30);
    private static readonly string[] RelevantEventTypes =
    [
        "NEW_PLAYER",
        "MISSING_IN_ROSTER",
        "STATUS_UPDATED",
        "MERGE_SUGGESTED",
        "PRIORITY_UPDATE_REQUEST",
        "TEMPLE_MISSING_ACTION_REQUIRED",
        "WOM_MISSING_ACTION_REQUIRED",
        "WOM_ONLY_ACTION_REQUIRED",
        "WOM_ONLY_DISCORD_POSTED",
        "WOM_ONLY_ACTION_APPLIED",
        "WOM_ONLY_IGNORED",
        "TEMPLE_MISSING_ACTION_APPLIED",
        "TEMPLE_MISSING_DISCORD_POSTED",
        "WOM_MISSING_DISCORD_POSTED",
        "DISCORD_POSTED_MESSAGE_MISSING",
        "DISCORD_REVIEW_REQUEUE_REQUESTED",
        "WOM_RANK_MISMATCH_REQUIRED",
        "WOM_RANK_MISMATCH_DISCORD_POSTED",
        "WOM_RANK_MISMATCH_ACTION_APPLIED",
        "WOM_RANK_MISMATCH_IGNORED",
        "WOM_ROLE_UPDATE_APPLIED",
        "PROMOTION_CANDIDATE_CREATED",
        "PROMOTION_DISCORD_POSTED",
        "PROMOTION_DISCORD_ACTION_APPLIED",
        "DISCORD_MARK_RENAME_SUSPECT",
        "DISCORD_SLASH_COMMAND_USED",
        "PET_HISCORES_DISCORD_POSTED",
        "PET_HISCORES_BANNER_POSTED"
    ];

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await db.LifecycleEvents
            .Where(x => x.EventType == AppStatusConstants.EventType && x.Status == "OPEN")
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new
            {
                x.Id,
                x.MetadataJson,
                x.CreatedAt
            })
            .ToListAsync(ct);

        var components = rows
            .Select(x => ToStatus(x.Id, x.MetadataJson, x.CreatedAt, now))
            .GroupBy(x => x.Component, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.OrderByDescending(s => s.HeartbeatAt ?? s.CreatedAt).First())
            .OrderBy(x => x.Component)
            .ToList();

        var latestPlayerSync = await db.Players
            .Where(x => x.LastSynced.HasValue)
            .OrderByDescending(x => x.LastSynced)
            .Select(x => new
            {
                x.Username,
                x.LastSynced
            })
            .FirstOrDefaultAsync(ct);

        if (latestPlayerSync is not null)
        {
            components.Add(ToLatestPlayerSyncStatus(latestPlayerSync.Username, latestPlayerSync.LastSynced!.Value, now));
        }

        var recentCutoff = now.Subtract(RecentEventWindow);
        var latestActivity = await db.LifecycleEvents
            .Where(x =>
                x.EventType != AppStatusConstants.EventType &&
                RelevantEventTypes.Contains(x.EventType) &&
                x.CreatedAt >= recentCutoff)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Select(x => new
            {
                x.EventType,
                x.MetadataJson,
                x.CreatedAt,
                Player = db.Players
                    .Where(p => p.Id == x.PlayerId)
                    .Select(p => p.Username)
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct);

        if (latestActivity is not null)
        {
            components.Add(ToRecentActivityStatus(latestActivity.EventType, latestActivity.MetadataJson, latestActivity.Player, latestActivity.CreatedAt, now));
        }

        components.Insert(0, new LiveStatusDto(
            "API",
            "Online",
            "Frontend is connected to the API.",
            null,
            now,
            now,
            0,
            false,
            false,
            new Dictionary<string, string>()));

        return Ok(new LiveStatusResponse(now, components));
    }

    private static LiveStatusDto ToRecentActivityStatus(
        string eventType,
        string metadataJson,
        string? player,
        DateTimeOffset createdAt,
        DateTimeOffset now)
    {
        var metadata = ReadMetadata(metadataJson);
        var currentPlayer = player ?? Pick(metadata, "Username", "Player", "NewPlayer");
        var age = Math.Max(0, (int)(now - createdAt).TotalSeconds);
        return new LiveStatusDto(
            "Recent Event",
            HumanizeKey(eventType),
            $"Recent event: {HumanizeKey(eventType)}.",
            currentPlayer,
            createdAt,
            createdAt,
            age,
            age > 120,
            age > 600,
            new Dictionary<string, string>
            {
                ["Last Event"] = HumanizeKey(eventType)
            });
    }

    private static LiveStatusDto ToLatestPlayerSyncStatus(string username, DateTimeOffset lastSynced, DateTimeOffset now)
    {
        var age = Math.Max(0, (int)(now - lastSynced).TotalSeconds);
        return new LiveStatusDto(
            "Latest Sync",
            "Player synced",
            "Most recently synced player.",
            username,
            lastSynced,
            lastSynced,
            age,
            age > 120,
            age > 600,
            new Dictionary<string, string>
            {
                ["Last Sync"] = lastSynced.ToString("O")
            });
    }

    private static LiveStatusDto ToStatus(int id, string metadataJson, DateTimeOffset createdAt, DateTimeOffset now)
    {
        var metadata = ReadMetadata(metadataJson);
        var component = Pick(metadata, "Component") ?? $"Status #{id}";
        var state = Pick(metadata, "State") ?? "Unknown";
        var message = Pick(metadata, "Message") ?? "No status message recorded.";
        var currentPlayer = Pick(metadata, "Details.Username", "Username", "Player");
        var heartbeatAt = TryParseDate(Pick(metadata, "HeartbeatAt"));
        var age = heartbeatAt.HasValue ? Math.Max(0, (int)(now - heartbeatAt.Value).TotalSeconds) : (int?)null;
        var isStale = age.HasValue && age.Value > 120;
        var isOffline = age.HasValue && age.Value > 600;

        var details = metadata
            .Where(x => !IsReservedKey(x.Key) && !IsSensitiveKey(x.Key) && !string.IsNullOrWhiteSpace(x.Value))
            .ToDictionary(x => HumanizeKey(x.Key), x => x.Value);

        return new LiveStatusDto(component, state, message, currentPlayer, heartbeatAt, createdAt, age, isStale, isOffline, details);
    }

    private static Dictionary<string, string> ReadMetadata(string metadataJson)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return values;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                AddMetadataValue(values, prop.Name, prop.Value);
            }
        }
        catch
        {
            values["Message"] = "Status metadata could not be read.";
        }
        return values;
    }

    private static void AddMetadataValue(Dictionary<string, string> values, string name, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in value.EnumerateObject())
            {
                values[$"{name}.{prop.Name}"] = JsonValueToString(prop.Value);
            }
            return;
        }

        values[name] = JsonValueToString(value);
    }

    private static string JsonValueToString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "",
            _ => value.GetRawText()
        };
    }

    private static string? Pick(Dictionary<string, string> metadata, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
    }

    private static DateTimeOffset? TryParseDate(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static bool IsReservedKey(string key)
    {
        return key.Equals("Component", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("State", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Message", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("HeartbeatAt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSensitiveKey(string key)
    {
        return key.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("VerificationCode", StringComparison.OrdinalIgnoreCase);
    }

    private static string HumanizeKey(string key)
    {
        var spaced = key.Replace(".", " ").Replace("_", " ");
        return string.Join(" ", spaced.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => char.ToUpperInvariant(x[0]) + x[1..]));
    }
}

public record LiveStatusResponse(DateTimeOffset ServerTime, IReadOnlyList<LiveStatusDto> Components);

public record LiveStatusDto(
    string Component,
    string State,
    string Message,
    string? CurrentPlayer,
    DateTimeOffset? HeartbeatAt,
    DateTimeOffset CreatedAt,
    int? AgeSeconds,
    bool IsStale,
    bool IsOffline,
    IReadOnlyDictionary<string, string> Details);
