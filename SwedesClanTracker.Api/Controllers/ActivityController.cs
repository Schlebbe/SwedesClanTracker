using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/activity")]
public class ActivityController(TrackerDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int? take, [FromQuery] bool? includeCleanup, CancellationToken ct)
    {
        var limit = Math.Clamp(take ?? 300, 1, 1000);
        var query = db.LifecycleEvents
            .Where(x => x.EventType != AppStatusConstants.EventType);

        if (includeCleanup != true)
        {
            query = query.Where(x =>
                x.EventType != "PROMOTION_DISCORD_DELETE_SCHEDULED" &&
                x.EventType != "TEMPLE_MISSING_DISCORD_DELETE_SCHEDULED" &&
                x.EventType != "WOM_MISSING_DISCORD_DELETE_SCHEDULED" &&
                x.EventType != "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED" &&
                x.EventType != "DISCORD_INTERACTION_RESPONSE_DELETE_SCHEDULED");
        }

        var rows = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(limit)
            .Select(x => new
            {
                x.Id,
                x.PlayerId,
                x.EventType,
                x.MetadataJson,
                x.Status,
                x.CreatedAt,
                Player = db.Players
                    .Where(p => p.Id == x.PlayerId)
                    .Select(p => p.Username)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        var metadataByEventId = rows.ToDictionary(x => x.Id, x => ReadMetadata(x.MetadataJson));
        var candidateIds = metadataByEventId.Values
            .Select(GetCandidateId)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .ToList();
        var candidates = candidateIds.Count == 0
            ? new Dictionary<int, PromotionActivityInfo>()
            : await db.PromotionCandidates
                .Where(x => candidateIds.Contains(x.Id))
                .Select(x => new PromotionActivityInfo(
                    x.Id,
                    x.PlayerId,
                    x.Player.Username,
                    x.OldRank,
                    x.NewRank,
                    x.Reason,
                    x.Status.ToString()))
                .ToDictionaryAsync(x => x.Id, ct);

        return Ok(rows.Select(x =>
        {
            var metadata = metadataByEventId[x.Id];
            var candidate = GetCandidateId(metadata) is { } candidateId && candidates.TryGetValue(candidateId, out var found)
                ? found
                : null;

            return ToActivityEvent(
                x.Id,
                x.PlayerId,
                x.Player,
                x.EventType,
                metadata,
                candidate,
                x.Status,
                x.CreatedAt);
        }));
    }

    private static ActivityEventDto ToActivityEvent(
        int id,
        int playerId,
        string? player,
        string eventType,
        Dictionary<string, string> metadata,
        PromotionActivityInfo? candidate,
        string status,
        DateTimeOffset createdAt)
    {
        var playerName = candidate?.Player ?? player ?? Pick(metadata, "Username", "Player", "NewPlayer") ?? "Player";
        var title = HumanizeEventType(eventType);
        var description = playerName == "Player"
            ? "Lifecycle event recorded by the app."
            : $"Related to {playerName}.";

        var action = Pick(metadata, "Action")?.ToLowerInvariant();
        var actor = Pick(metadata, "HandledBy", "RequestedBy", "User", "IgnoredBy");
        var candidateId = Pick(metadata, "CandidateId");
        var commandName = Pick(metadata, "Command", "CommandName");
        var page = Pick(metadata, "Page");
        var oldRank = candidate?.OldRank ?? Pick(metadata, "OldRank");
        var newRank = candidate?.NewRank ?? Pick(metadata, "NewRank");
        var rankChange = oldRank is null || newRank is null ? null : $"{oldRank} -> {newRank}";
        var expectedRank = Pick(metadata, "ExpectedRank");
        var actualWomRole = Pick(metadata, "ActualWomRole");
        var mismatchDirection = Pick(metadata, "Direction");
        if (string.IsNullOrWhiteSpace(mismatchDirection) && expectedRank is not null && actualWomRole is not null)
        {
            mismatchDirection = GetWomRankMismatchDirection(expectedRank, actualWomRole);
        }
        var requestedRole = Pick(metadata, "RequestedRole");
        var updatedRole = Pick(metadata, "UpdatedRole");
        var mismatchDescription = expectedRank is null || actualWomRole is null
            ? $"{playerName} has a Wise Old Man rank mismatch."
            : mismatchDirection switch
            {
                "higher" => $"{playerName} has Wise Old Man rank {actualWomRole}, which appears ahead of the database rank {expectedRank}.",
                "lower" => $"{playerName} has Wise Old Man rank {actualWomRole}, which appears behind the database rank {expectedRank}.",
                _ => $"{playerName} has Wise Old Man rank {actualWomRole}, but the database current rank is {expectedRank}."
            };
        var candidateReference = candidate is null
            ? candidateId is null ? "the promotion candidate" : $"promotion candidate #{candidateId}"
            : $"{candidate.Player} promotion from {candidate.OldRank} to {candidate.NewRank}";

        switch (eventType)
        {
            case "NEW_PLAYER":
                title = $"{playerName} added to tracker";
                description = "Found in the roster and opened for review.";
                break;
            case "MISSING_IN_ROSTER":
                title = $"{playerName} missing from roster";
                description = "Marked for review because the player was not found in the roster.";
                break;
            case "STATUS_UPDATED":
                title = $"{playerName} status updated";
                description = $"Status set to {FormatPlayerStatus(Pick(metadata, "Status")) ?? status}.";
                break;
            case "MERGE_SUGGESTED":
                title = "Possible rename detected";
                description = $"{Pick(metadata, "NewPlayer") ?? playerName} may be {Pick(metadata, "SuggestedPrevious") ?? "a previous player"}.";
                break;
            case "PRIORITY_UPDATE_REQUEST":
                title = "Priority update requested";
                description = $"{playerName} was queued for an immediate sync.";
                break;
            case "DISCORD_SLASH_COMMAND_USED":
                playerName = string.IsNullOrWhiteSpace(actor) ? "Player" : actor!;
                title = string.IsNullOrWhiteSpace(commandName)
                    ? "Discord slash command used"
                    : $"Discord slash command /{commandName}";
                var adminText = string.Equals(Pick(metadata, "AdminLocked"), "True", StringComparison.OrdinalIgnoreCase)
                    ? " Admin-locked command."
                    : "";
                var allowedText = string.Equals(Pick(metadata, "Allowed"), "False", StringComparison.OrdinalIgnoreCase)
                    ? " Request was denied."
                    : "";
                description = $"Slash command was used{ByActor(actor)}.{adminText}{allowedText}";
                break;
            case "TEMPLE_MISSING_ACTION_REQUIRED":
                title = "Temple review required";
                description = $"{playerName} needs a Temple add/remove decision.";
                break;
            case "WOM_MISSING_ACTION_REQUIRED":
                title = "Wise Old Man review required";
                description = $"{playerName} needs a Wise Old Man add/remove decision.";
                break;
            case "TEMPLE_MISSING_ACTION_APPLIED":
                title = action == "add" ? "Player added back to Temple" :
                    action == "remove" ? "Player removed from review" :
                    "Temple missing action applied";
                description = $"{playerName} was handled from the missing-player review queue{ByActor(actor)}.";
                break;
            case "TEMPLE_MISSING_DISCORD_POSTED":
                title = "Temple review posted to Discord";
                description = $"{playerName} missing-player review card was posted.";
                break;
            case "WOM_MISSING_DISCORD_POSTED":
                title = "Wise Old Man review posted to Discord";
                description = $"{playerName} missing-player review card was posted.";
                break;
            case "WOM_RANK_MISMATCH_REQUIRED":
                title = "Wise Old Man rank mismatch";
                description = $"{mismatchDescription} Officers need to update the rank in game/WiseOldMan or explicitly allow it.";
                break;
            case "WOM_RANK_MISMATCH_DISCORD_POSTED":
                title = "Wise Old Man rank alert posted";
                description = $"{mismatchDescription} A Discord alert was posted for officers.";
                break;
            case "WOM_RANK_MISMATCH_ACTION_APPLIED":
                title = action switch
                {
                    "ignore" => "Wise Old Man rank mismatch ignored",
                    "sync_wom_to_db" => "Database rank synced from Wise Old Man",
                    "sync_db_to_wom" => "Wise Old Man rank synced from database",
                    _ => "Wise Old Man rank mismatch dismissed"
                };
                description = action switch
                {
                    "ignore" => $"{playerName} was explicitly allowed to keep the mismatched Wise Old Man rank{ByActor(actor)}.",
                    "sync_wom_to_db" => $"{playerName}'s database rank was updated from Wise Old Man to keep tracker data aligned with in-game/WOM rank{ByActor(actor)}.",
                    "sync_db_to_wom" => $"{playerName}'s Wise Old Man rank was updated from database rank to restore parity across both systems{ByActor(actor)}.",
                    _ => $"{mismatchDescription} The alert was dismissed after officer acknowledgement{ByActor(actor)}."
                };
                break;
            case "WOM_RANK_MISMATCH_IGNORED":
                title = "Wise Old Man rank mismatch allowed";
                description = $"{playerName} is ignored for Wise Old Man rank mismatch alerts{ByActor(actor)}.";
                break;
            case "PROMOTION_CANDIDATE_CREATED":
                title = "Promotion candidate created";
                description = rankChange is null
                    ? $"{playerName} became eligible for promotion."
                    : $"{playerName} became eligible for promotion: {rankChange}.";
                break;
            case "PROMOTION_DISCORD_POSTED":
                title = "Promotion posted to Discord";
                description = $"{candidateReference} was posted to Discord for review.";
                break;
            case "PROMOTION_DISCORD_ACTION_APPLIED":
                title = action switch
                {
                    "approve" => "Promotion approved",
                    "dismiss" => "Promotion dismissed",
                    "rename" => "Promotion marked as rename suspect",
                    _ => "Promotion action applied"
                };
                description = $"{candidateReference} was updated from {Pick(metadata, "Source") ?? "the app"}{ByActor(actor)}.";
                break;
            case "PROMOTION_DISCORD_DELETE_SCHEDULED":
                title = "Promotion Discord cleanup scheduled";
                description = $"{candidateReference} Discord message was queued for deletion.";
                break;
            case "DISCORD_MARK_RENAME_SUSPECT":
                title = "Discord marked rename suspect";
                description = $"{candidateReference} was marked as a possible rename{ByActor(actor)}.";
                break;
            case "WOM_MISSING_ACTION_APPLIED":
                title = action == "reinstate" ? "Player reinstated in Wise Old Man" :
                    action == "remove" ? "Player removed from Wise Old Man review" :
                    "Wise Old Man missing action applied";
                description = $"{playerName} was handled from the Wise Old Man missing-player review queue{ByActor(actor)}.";
                break;
            case "WOM_ROLE_UPDATE_APPLIED":
                var roleText = updatedRole ?? requestedRole ?? "a new role";
                var success = string.Equals(Pick(metadata, "Success"), "True", StringComparison.OrdinalIgnoreCase);
                title = success ? "Wise Old Man role updated" : "Wise Old Man role update failed";
                description = success
                    ? $"{playerName} was updated to Wise Old Man role {FormatRoleName(roleText)}{ByActor(actor)}."
                    : $"{playerName} Wise Old Man role update to {FormatRoleName(roleText)} failed{ByActor(actor)}.";
                break;
            case "PET_HISCORES_DISCORD_POSTED":
                title = "Pet hiscores page posted";
                description = page is null
                    ? "A pet hiscores message was posted."
                    : $"Pet hiscores page {page} was posted.";
                break;
            case "PET_HISCORES_BANNER_POSTED":
                title = "Pet hiscores banner posted";
                description = "The pet hiscores banner image was posted.";
                break;
            case "TEMPLE_MISSING_DISCORD_DELETE_SCHEDULED":
            case "WOM_MISSING_DISCORD_DELETE_SCHEDULED":
                title = "Discord cleanup scheduled";
                description = "A Discord message was queued for deletion.";
                break;
            case "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED":
            case "DISCORD_INTERACTION_RESPONSE_DELETE_SCHEDULED":
                title = "Discord cleanup scheduled";
                var messageDescription = Pick(metadata, "MessageDescription", "Extra.MessageDescription");
                description = string.IsNullOrWhiteSpace(messageDescription)
                    ? "A Discord message was queued for deletion."
                    : $"Queued for deletion: {messageDescription}.";
                break;
        }

        return new ActivityEventDto(
            id,
            playerId,
            playerName == "Player" ? null : playerName,
            eventType,
            title,
            description,
            BuildGroups(eventType),
            PrimaryCategoryLabel(eventType),
            status,
            createdAt,
            actor,
            BuildDetails(metadata, candidate));
    }

    private static string ByActor(string? actor) => string.IsNullOrWhiteSpace(actor) ? "" : $" by {actor}";

    private static IReadOnlyList<string> BuildGroups(string eventType)
    {
        if (IsDiscordCleanupEvent(eventType))
        {
            return ["discord"];
        }

        var groups = new List<string>();
        if (eventType is "NEW_PLAYER" or "MISSING_IN_ROSTER" or "STATUS_UPDATED" or "MERGE_SUGGESTED" ||
            eventType.Contains("MISSING", StringComparison.OrdinalIgnoreCase) ||
            eventType.Contains("RANK_MISMATCH", StringComparison.OrdinalIgnoreCase) ||
            eventType.Contains("RENAME", StringComparison.OrdinalIgnoreCase))
        {
            groups.Add("players");
        }
        if (eventType.Contains("PROMOTION", StringComparison.OrdinalIgnoreCase))
        {
            groups.Add("promotions");
        }
        if (eventType.StartsWith("WOM_", StringComparison.OrdinalIgnoreCase))
        {
            groups.Add("players");
            groups.Add("discord");
        }
        if (eventType.Contains("DISCORD", StringComparison.OrdinalIgnoreCase) ||
            eventType.Contains("PET_HISCORES", StringComparison.OrdinalIgnoreCase))
        {
            groups.Add("discord");
        }
        if (eventType.Contains("COMMAND", StringComparison.OrdinalIgnoreCase))
        {
            groups.Add("commands");
        }
        if (eventType.Contains("ACTION", StringComparison.OrdinalIgnoreCase) ||
            eventType.Contains("REVIEW", StringComparison.OrdinalIgnoreCase) ||
            eventType.Contains("RANK_MISMATCH", StringComparison.OrdinalIgnoreCase) ||
            eventType is "STATUS_UPDATED" or "MERGE_SUGGESTED")
        {
            groups.Add("review");
        }
        if (eventType.Contains("DELETE_SCHEDULED", StringComparison.OrdinalIgnoreCase) ||
            eventType.Contains("PRIORITY", StringComparison.OrdinalIgnoreCase) ||
            groups.Count == 0)
        {
            groups.Add("system");
        }
        return groups.Distinct().ToArray();
    }

    private static string PrimaryCategoryLabel(string eventType)
    {
        if (IsDiscordCleanupEvent(eventType)) return "Discord";
        if (eventType.Contains("COMMAND", StringComparison.OrdinalIgnoreCase)) return "Command";
        if (eventType.Contains("PROMOTION", StringComparison.OrdinalIgnoreCase)) return "Promotion";
        if (eventType.Contains("DISCORD", StringComparison.OrdinalIgnoreCase) ||
            eventType.Contains("PET_HISCORES", StringComparison.OrdinalIgnoreCase)) return "Discord";
        if (eventType.Contains("RANK_MISMATCH", StringComparison.OrdinalIgnoreCase)) return "Player";
        if (eventType.Contains("MISSING", StringComparison.OrdinalIgnoreCase) ||
            eventType is "NEW_PLAYER" or "STATUS_UPDATED" or "MERGE_SUGGESTED") return "Player";
        return "System";
    }

    private static bool IsDiscordCleanupEvent(string eventType)
    {
        return eventType is
                "PROMOTION_DISCORD_DELETE_SCHEDULED" or
                "TEMPLE_MISSING_DISCORD_DELETE_SCHEDULED" or
                "WOM_MISSING_DISCORD_DELETE_SCHEDULED" or
                "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED" or
                "DISCORD_INTERACTION_RESPONSE_DELETE_SCHEDULED" ||
            (eventType.Contains("DISCORD", StringComparison.OrdinalIgnoreCase) &&
             eventType.Contains("DELETE", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<ActivityDetailDto> BuildDetails(Dictionary<string, string> metadata, PromotionActivityInfo? candidate)
    {
        var details = metadata
            .Where(x => !IsSensitiveKey(x.Key) && !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => new ActivityDetailDto(HumanizeKey(x.Key), FormatDetailValue(x.Key, x.Value)))
            .ToList();

        if (candidate is not null)
        {
            AddDetailIfMissing(details, "Candidate Id", candidate.Id.ToString(CultureInfo.InvariantCulture));
            AddDetailIfMissing(details, "Old Rank", candidate.OldRank);
            AddDetailIfMissing(details, "New Rank", candidate.NewRank);
            AddDetailIfMissing(details, "Promotion Status", candidate.Status);
            AddDetailIfMissing(details, "Reason", candidate.Reason);
        }

        return details.ToArray();
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
            values["Metadata"] = "Unreadable metadata";
        }
        return values;
    }

    private static void AddMetadataValue(Dictionary<string, string> values, string name, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in value.EnumerateObject())
            {
                if (!values.ContainsKey(prop.Name))
                {
                    values[prop.Name] = JsonValueToString(prop.Value);
                }
                else
                {
                    values[$"{name}.{prop.Name}"] = JsonValueToString(prop.Value);
                }
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

    private static int? GetCandidateId(Dictionary<string, string> metadata)
    {
        var raw = Pick(metadata, "CandidateId");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    private static void AddDetailIfMissing(List<ActivityDetailDto> details, string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (details.Any(x => string.Equals(x.Label, label, StringComparison.OrdinalIgnoreCase))) return;
        details.Add(new ActivityDetailDto(label, value));
    }

    private static bool IsSensitiveKey(string key)
    {
        return key.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("VerificationCode", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatDetailValue(string key, string value)
    {
        if (key.Equals("Status", StringComparison.OrdinalIgnoreCase))
        {
            return FormatPlayerStatus(value) ?? value;
        }
        return value;
    }

    private static string? FormatPlayerStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (int.TryParse(value, out var numeric) && Enum.IsDefined(typeof(PlayerStatus), numeric))
        {
            return ((PlayerStatus)numeric).ToString();
        }
        return value;
    }

    private static string FormatRoleName(string value)
    {
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' ').ToLowerInvariant());
    }

    private static string HumanizeEventType(string eventType) => HumanizeKey(eventType);

    private static string HumanizeKey(string key)
    {
        var text = key.Replace("_", " ");
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
    }

    private static string GetWomRankMismatchDirection(string expectedRank, string actualWomRole)
    {
        var expected = RankOrder(expectedRank);
        var actual = RankOrder(actualWomRole);
        if (actual > expected) return "higher";
        if (actual < expected) return "lower";
        return "different";
    }

    private static int RankOrder(string rank)
    {
        if (string.IsNullOrWhiteSpace(rank)) return 0;
        var normalized = rank.Replace('_', ' ').Trim();
        string[] order = ["Recruit", "Officer", "Commander", "Lieutenant", "Captain", "Astral", "General", "Brigadier", "Admiral", "Marshal", "Beast"];
        for (var i = 0; i < order.Length; i++)
        {
            if (string.Equals(order[i], normalized, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return 0;
    }
}

public record ActivityEventDto(
    int Id,
    int PlayerId,
    string? Player,
    string EventType,
    string Title,
    string Description,
    IReadOnlyList<string> Groups,
    string CategoryLabel,
    string Status,
    DateTimeOffset CreatedAt,
    string? Actor,
    IReadOnlyList<ActivityDetailDto> Details);

public record ActivityDetailDto(string Label, string Value);

internal record PromotionActivityInfo(
    int Id,
    int PlayerId,
    string Player,
    string OldRank,
    string NewRank,
    string Reason,
    string Status);
