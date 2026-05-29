using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/app")]
public class AppController(TrackerDbContext db, IConfiguration config) : ControllerBase
{
    [HttpGet("home")]
    public async Task<IActionResult> GetHome(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var totalMembers = await db.Players.CountAsync(ct);
        var activeMembers = await db.Players.CountAsync(x => x.Status == PlayerStatus.ACTIVE, ct);
        var pendingPromotions = await db.PromotionCandidates.CountAsync(x => x.Status == PromotionStatus.PENDING, ct);
        var openAdminCases = await db.Players.CountAsync(x =>
            x.Status == PlayerStatus.NEW_PENDING_REVIEW ||
            x.Status == PlayerStatus.MISSING_PENDING_REVIEW ||
            x.Status == PlayerStatus.MERGE_SUGGESTED, ct);

        var latestSync = await db.Players
            .Where(x => x.LastSynced.HasValue)
            .OrderByDescending(x => x.LastSynced)
            .Select(x => new { x.Username, x.LastSynced })
            .FirstOrDefaultAsync(ct);

        var workerStatusRows = await db.LifecycleEvents
            .Where(x => x.EventType == AppStatusConstants.EventType && x.Status == "OPEN")
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.MetadataJson, x.CreatedAt })
            .ToListAsync(ct);

        var latestWorkerStatus = workerStatusRows
            .Select(x =>
            {
                var metadata = ReadMetadata(x.MetadataJson);
                return new
                {
                    Component = Pick(metadata, "Component"),
                    State = Pick(metadata, "State"),
                    CurrentPlayer = Pick(metadata, "Details.Username", "Username", "Player"),
                    HeartbeatAt = TryParseDate(Pick(metadata, "HeartbeatAt")),
                    CreatedAt = x.CreatedAt
                };
            })
            .Where(x => !string.Equals(x.Component, "API", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.HeartbeatAt ?? x.CreatedAt)
            .FirstOrDefault();

        var workerAgeSeconds = latestWorkerStatus?.HeartbeatAt is { } hb
            ? Math.Max(0, (int)(now - hb).TotalSeconds)
            : (int?)null;

        var workerState = latestWorkerStatus?.State?.ToLowerInvariant() switch
        {
            null => "unknown",
            _ when workerAgeSeconds.HasValue && workerAgeSeconds.Value > 600 => "offline",
            _ when workerAgeSeconds.HasValue && workerAgeSeconds.Value > 120 => "stale",
            var s when s.Contains("error", StringComparison.OrdinalIgnoreCase) => "offline",
            var s when s.Contains("work", StringComparison.OrdinalIgnoreCase) || s.Contains("sync", StringComparison.OrdinalIgnoreCase) => "syncing",
            _ => "idle"
        };

        var overall = workerState == "offline"
            ? "critical"
            : workerState == "stale"
                ? "warning"
                : "healthy";

        var missingReviewCount = await db.Players.CountAsync(x => x.Status == PlayerStatus.MISSING_PENDING_REVIEW || x.Status == PlayerStatus.NEW_PENDING_REVIEW, ct);
        var mergeReviewCount = await db.Players.CountAsync(x => x.Status == PlayerStatus.MERGE_SUGGESTED, ct);
        var staleSyncCount = await db.Players.CountAsync(x => !x.LastSynced.HasValue || x.LastSynced < now.AddHours(-24), ct);
        var rankMismatchCount = await db.LifecycleEvents.CountAsync(x => x.EventType == "WOM_RANK_MISMATCH_REQUIRED" && x.Status == "OPEN", ct);

        var meaningfulChanges = await db.LifecycleEvents
            .Where(x => x.EventType != AppStatusConstants.EventType)
            .OrderByDescending(x => x.CreatedAt)
            .Take(100)
            .Select(x => new
            {
                x.Id,
                x.EventType,
                x.CreatedAt,
                Player = db.Players.Where(p => p.Id == x.PlayerId).Select(p => p.Username).FirstOrDefault()
            })
            .ToListAsync(ct);

        var meaningful = meaningfulChanges
            .Select(x => ToMeaningfulChange(x.Id, x.EventType, x.Player, x.CreatedAt, now))
            .Where(x => x is not null)
            .Take(6)
            .Cast<HomeMeaningfulChangeDto>()
            .ToArray();

        var queueCases = await BuildAdminQueueCases(ct, now);
        var queuePreview = queueCases
            .OrderByDescending(x => RiskRank(x.Risk))
            .ThenByDescending(x => ParseAgeMinutes(x.Age))
            .Take(3)
            .Select(x => new HomeWorkPreviewItemDto(x.Id, x.Title, x.Risk, x.Age))
            .ToArray();

        var response = new HomeResponseDto(
            new HomeOverviewDto(totalMembers, activeMembers, pendingPromotions, openAdminCases),
            new HomeHealthDto(
                overall,
                new HomeApiHealthDto("online", 0),
                new HomeWorkerHealthDto(workerState, latestWorkerStatus?.CurrentPlayer ?? "unknown", workerAgeSeconds.HasValue ? HumanizeSeconds(workerAgeSeconds.Value) : "unknown"),
                new HomeLatestSyncDto(latestSync?.Username ?? "unknown", latestSync?.LastSynced is { } ls ? HumanizeSeconds(Math.Max(0, (int)(now - ls).TotalSeconds)) : "unknown")
            ),
            [
                new HomeRosterPostureItemDto("Stale Sync", staleSyncCount, "warning", "Older than 24h"),
                new HomeRosterPostureItemDto("Missing Review", missingReviewCount, missingReviewCount > 0 ? "danger" : "success", "Missing/new player review"),
                new HomeRosterPostureItemDto("Merge Review", mergeReviewCount, mergeReviewCount > 0 ? "warning" : "success", "Rename suspects"),
                new HomeRosterPostureItemDto("Rank Mismatch", rankMismatchCount, rankMismatchCount > 0 ? "warning" : "success", "WOM vs tracker")
            ],
            meaningful,
            queuePreview);

        return Ok(response);
    }

    [HttpGet("admin-queue")]
    public async Task<IActionResult> GetAdminQueue(CancellationToken ct)
    {
        var cases = await BuildAdminQueueCases(ct, DateTimeOffset.UtcNow);
        return Ok(cases);
    }

    [HttpGet("admin-queue/{caseId}")]
    public async Task<IActionResult> GetAdminQueueCase(string caseId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        if (caseId.StartsWith("promotion:", StringComparison.OrdinalIgnoreCase))
        {
            var idRaw = caseId["promotion:".Length..];
            if (!int.TryParse(idRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var promotionId))
            {
                return BadRequest("Invalid promotion case id.");
            }

            var candidate = await db.PromotionCandidates
                .Where(x => x.Id == promotionId && x.Status == PromotionStatus.PENDING)
                .Select(x => new
                {
                    x.Id,
                    Player = x.Player.Username,
                    x.OldRank,
                    x.NewRank,
                    x.Reason,
                    x.CreatedAt
                })
                .FirstOrDefaultAsync(ct);

            if (candidate is null) return NotFound();

            var risk = ClassifyPromotionRisk(candidate.Reason);
            var detail = new AdminQueueCaseDetailDto(
                $"promotion:{candidate.Id}",
                "promotion",
                candidate.Player,
                $"{candidate.OldRank} -> {candidate.NewRank}",
                risk,
                risk == "low" ? "high" : "medium",
                HumanizeAge(now - candidate.CreatedAt),
                "Approve promotion",
                [
                    "Promotion candidate is still pending approval.",
                    $"Candidate reason: {candidate.Reason}",
                    $"Rank transition: {candidate.OldRank} -> {candidate.NewRank}"
                ],
                ["Dismiss candidate", "Mark rename suspect"],
                "Dismiss without review can hide legitimate promotion opportunities.");

            return Ok(detail);
        }

        if (caseId.StartsWith("review:", StringComparison.OrdinalIgnoreCase))
        {
            var split = caseId.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (split.Length != 3 || !int.TryParse(split[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var playerId))
            {
                return BadRequest("Invalid review case id.");
            }

            var player = await db.Players
                .Where(x => x.Id == playerId)
                .Select(x => new
                {
                    x.Id,
                    x.Username,
                    Status = x.Status.ToString(),
                    x.CurrentRank,
                    x.EligibleRank,
                    x.LastSeen,
                    MergeMeta = db.LifecycleEvents
                        .Where(e => e.PlayerId == x.Id && e.EventType == "MERGE_SUGGESTED")
                        .OrderByDescending(e => e.CreatedAt)
                        .Select(e => e.MetadataJson)
                        .FirstOrDefault()
                })
                .FirstOrDefaultAsync(ct);

            if (player is null) return NotFound();

            var isMerge = string.Equals(split[2], "merge", StringComparison.OrdinalIgnoreCase);
            var statusLabel = isMerge ? "merge" : "missing";
            var recommendedAction = isMerge ? "Confirm suggested previous player" : "Validate identity before add/remove";
            var alternatives = isMerge
                ? new[] { "Pick different previous", "Abort rename" }
                : new[] { "Add back to Temple", "Remove from DB" };

            var evidence = new List<string>
            {
                $"Player status: {player.Status}",
                $"Current rank: {player.CurrentRank}",
                $"Eligible rank: {player.EligibleRank}",
                $"Last seen: {player.LastSeen:yyyy-MM-dd HH:mm} UTC"
            };

            if (isMerge && !string.IsNullOrWhiteSpace(player.MergeMeta))
            {
                var metadata = ReadMetadata(player.MergeMeta);
                var suggestedPrevious = Pick(metadata, "SuggestedPrevious");
                if (!string.IsNullOrWhiteSpace(suggestedPrevious))
                {
                    evidence.Add($"Suggested previous player: {suggestedPrevious}");
                }
            }

            var detail = new AdminQueueCaseDetailDto(
                $"review:{player.Id}:{statusLabel}",
                "review",
                player.Username,
                isMerge ? "Rename/merge suggestion" : "Missing/new player review",
                isMerge ? "medium" : "high",
                isMerge ? "medium" : "medium",
                HumanizeAge(now - player.LastSeen),
                recommendedAction,
                evidence.ToArray(),
                alternatives,
                isMerge
                    ? "Abort rename can create duplicate player timelines."
                    : "Remove from DB permanently drops snapshots and lifecycle history.");

            return Ok(detail);
        }

        return NotFound();
    }

    [HttpGet("roster")]
    public async Task<IActionResult> GetRoster(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var pendingPromotionPlayerIds = await db.PromotionCandidates
            .Where(x => x.Status == PromotionStatus.PENDING)
            .Select(x => x.PlayerId)
            .Distinct()
            .ToListAsync(ct);

        var rankMismatchPlayerIds = await db.LifecycleEvents
            .Where(x => x.EventType == "WOM_RANK_MISMATCH_REQUIRED" && x.Status == "OPEN")
            .Select(x => x.PlayerId)
            .Distinct()
            .ToListAsync(ct);

        var roster = await db.Players
            .OrderBy(x => x.Username)
            .Select(x => new
            {
                x.Id,
                x.Username,
                x.CurrentRank,
                Status = x.Status.ToString(),
                x.LastSynced,
                x.LastSeen,
                HasOpenReviewCase = x.Status == PlayerStatus.NEW_PENDING_REVIEW ||
                    x.Status == PlayerStatus.MISSING_PENDING_REVIEW ||
                    x.Status == PlayerStatus.MERGE_SUGGESTED
            })
            .ToListAsync(ct);

        var rows = roster.Select(x => new RosterRowDto(
            x.Id,
            x.Username,
            x.CurrentRank,
            x.Status,
            x.LastSynced,
            x.LastSeen,
            !x.LastSynced.HasValue || x.LastSynced < now.AddHours(-24),
            x.HasOpenReviewCase,
            pendingPromotionPlayerIds.Contains(x.Id),
            rankMismatchPlayerIds.Contains(x.Id)))
            .ToArray();

        return Ok(new RosterResponseDto(rows));
    }

    [HttpGet("players/{id:int}/profile")]
    public async Task<IActionResult> GetPlayerProfile(int id, CancellationToken ct)
    {
        var player = await db.Players
            .Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id,
                x.Username,
                x.CurrentRank,
                Status = x.Status.ToString(),
                x.LastSynced,
                x.LastSeen,
                x.EligibleRank
            })
            .FirstOrDefaultAsync(ct);

        if (player is null) return NotFound();

        var hasPendingPromotion = await db.PromotionCandidates
            .AnyAsync(x => x.PlayerId == id && x.Status == PromotionStatus.PENDING, ct);

        var hasRankMismatch = await db.LifecycleEvents
            .AnyAsync(x => x.PlayerId == id && x.EventType == "WOM_RANK_MISMATCH_REQUIRED" && x.Status == "OPEN", ct);

        var recentEvents = await db.LifecycleEvents
            .Where(x => x.PlayerId == id && x.EventType != AppStatusConstants.EventType)
            .OrderByDescending(x => x.CreatedAt)
            .Take(8)
            .Select(x => new PlayerProfileRecentEventDto(
                x.Id,
                HumanizeEventType(x.EventType),
                x.CreatedAt,
                HumanizeAge(DateTimeOffset.UtcNow - x.CreatedAt)))
            .ToArrayAsync(ct);

        var openCases = new List<PlayerProfileOpenCaseDto>();
        if (player.Status is "NEW_PENDING_REVIEW" or "MISSING_PENDING_REVIEW" or "MERGE_SUGGESTED")
        {
            openCases.Add(new PlayerProfileOpenCaseDto("review", "Open player review case"));
        }

        if (hasPendingPromotion)
        {
            openCases.Add(new PlayerProfileOpenCaseDto("promotion", "Pending promotion candidate"));
        }

        if (hasRankMismatch)
        {
            openCases.Add(new PlayerProfileOpenCaseDto("mismatch", "Open WOM rank mismatch case"));
        }

        var profile = new PlayerProfileResponseDto(
            player.Id,
            player.Username,
            player.CurrentRank,
            player.EligibleRank,
            player.Status,
            player.LastSynced,
            player.LastSeen,
            !player.LastSynced.HasValue,
            hasPendingPromotion,
            hasRankMismatch,
            recentEvents,
            openCases,
            new PlayerProfileHistoryAvailabilityDto(
                false,
                false,
                "Historical rank/stat trend endpoints are not productionized yet."));

        return Ok(profile);
    }

    [HttpGet("clan-log")]
    public async Task<IActionResult> GetClanLog(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var events = await db.LifecycleEvents
            .Where(x => x.EventType != AppStatusConstants.EventType)
            .OrderByDescending(x => x.CreatedAt)
            .Take(300)
            .Select(x => new
            {
                x.Id,
                x.EventType,
                x.CreatedAt,
                x.MetadataJson,
                Player = db.Players.Where(p => p.Id == x.PlayerId).Select(p => p.Username).FirstOrDefault()
            })
            .ToListAsync(ct);

        var important = new List<ClanLogImportantItemDto>();
        var routine = new List<string>();

        foreach (var item in events)
        {
            var metadata = ReadMetadata(item.MetadataJson);
            var projection = ToClanLogProjection(item.EventType, item.Player, metadata);
            if (projection is null)
            {
                continue;
            }

            if (projection.IsRoutine)
            {
                routine.Add(projection.Detail);
                continue;
            }

            important.Add(new ClanLogImportantItemDto(
                item.Id.ToString(CultureInfo.InvariantCulture),
                projection.Group,
                projection.Title,
                projection.Detail,
                HumanizeAge(now - item.CreatedAt)));
        }

        var response = new ClanLogResponseDto(
            ["important", "promotions", "roster", "reviews", "sync-system", "all"],
            important.Take(40).ToArray(),
            routine.Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray());

        return Ok(response);
    }

    [HttpGet("readiness")]
    public async Task<IActionResult> GetReadiness(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var latestSync = await db.Players
            .Where(x => x.LastSynced.HasValue)
            .OrderByDescending(x => x.LastSynced)
            .Select(x => new { x.Username, x.LastSynced })
            .FirstOrDefaultAsync(ct);

        var latestWorkerStatusRow = await db.LifecycleEvents
            .Where(x => x.EventType == AppStatusConstants.EventType && x.Status == "OPEN")
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.MetadataJson, x.CreatedAt })
            .FirstOrDefaultAsync(ct);

        var workerMetadata = latestWorkerStatusRow is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : ReadMetadata(latestWorkerStatusRow.MetadataJson);
        var workerHeartbeat = TryParseDate(Pick(workerMetadata, "HeartbeatAt"));
        var workerCurrentPlayer = Pick(workerMetadata, "Details.Username", "Username", "Player") ?? "unknown";
        var workerStateRaw = Pick(workerMetadata, "State") ?? "unknown";
        var workerAgeSeconds = workerHeartbeat.HasValue ? Math.Max(0, (int)(now - workerHeartbeat.Value).TotalSeconds) : (int?)null;

        var workerState = workerAgeSeconds switch
        {
            > 600 => "Offline",
            > 120 => "Stale",
            _ => workerStateRaw
        };
        var workerTone = workerAgeSeconds switch
        {
            > 600 => "danger",
            > 120 => "warning",
            _ => workerStateRaw.Contains("error", StringComparison.OrdinalIgnoreCase) ? "danger" : "info"
        };

        var apiTone = "success";
        var syncAgeSeconds = latestSync?.LastSynced is { } syncAt ? Math.Max(0, (int)(now - syncAt).TotalSeconds) : (int?)null;
        var syncTone = syncAgeSeconds switch
        {
            null => "warning",
            > 86400 => "warning",
            _ => "success"
        };

        var runtime = new List<ReadinessRuntimeItemDto>
        {
            new("API", "Online", apiTone, "Frontend-authenticated API is reachable."),
            new("Worker", workerState, workerTone, workerHeartbeat.HasValue
                ? $"Current: {workerCurrentPlayer}. Last heartbeat {HumanizeSeconds(workerAgeSeconds ?? 0)}."
                : "No worker heartbeat reported yet."),
            new("Latest Sync", latestSync?.Username is null ? "Unknown" : "Observed", syncTone, latestSync?.LastSynced is { } ls
                ? $"{latestSync.Username} synced {HumanizeSeconds(Math.Max(0, (int)(now - ls).TotalSeconds))}."
                : "No player sync timestamp found yet."),
            new("PostgreSQL", "Reachable", "success", "Read models loaded from database.")
        };

        var configRows = new List<ReadinessConfigItemDto>
        {
            new("Default connection string", !string.IsNullOrWhiteSpace(config.GetConnectionString("DefaultConnection")) ? "Configured" : "Missing"),
            new("Temple API key", !string.IsNullOrWhiteSpace(config["Tracker:TempleApiKey"]) ? "Configured" : "Missing"),
            new("WOM verification code", !string.IsNullOrWhiteSpace(config["Tracker:WomVerificationCode"]) ? "Configured" : "Missing"),
            new("API rate limit / minute", (config.GetValue<int?>("Tracker:TempleApiCallsPerMinute") ?? 5).ToString(CultureInfo.InvariantCulture))
        };

        return Ok(new ReadinessResponseDto(runtime, configRows));
    }

    private async Task<IReadOnlyList<AdminQueueCaseListItemDto>> BuildAdminQueueCases(CancellationToken ct, DateTimeOffset now)
    {
        var promotionCases = await db.PromotionCandidates
            .Where(x => x.Status == PromotionStatus.PENDING)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new AdminQueueCaseListItemDto(
                $"promotion:{x.Id}",
                "promotion",
                ClassifyPromotionLane(x.Reason),
                x.Player.Username,
                $"{x.OldRank} -> {x.NewRank}",
                ClassifyPromotionRisk(x.Reason),
                ClassifyPromotionRisk(x.Reason) == "low" ? "high" : "medium",
                HumanizeAge(now - x.CreatedAt),
                "Approve promotion"))
            .ToListAsync(ct);

        var reviewPlayers = await db.Players
            .Where(x => x.Status == PlayerStatus.NEW_PENDING_REVIEW || x.Status == PlayerStatus.MISSING_PENDING_REVIEW || x.Status == PlayerStatus.MERGE_SUGGESTED)
            .OrderBy(x => x.Username)
            .Select(x => new
            {
                x.Id,
                x.Username,
                Status = x.Status.ToString(),
                x.LastSeen
            })
            .ToListAsync(ct);

        var reviewCases = reviewPlayers
            .Select(x =>
            {
                var isMerge = string.Equals(x.Status, "MERGE_SUGGESTED", StringComparison.OrdinalIgnoreCase);
                var lane = isMerge ? "inspect" : "high-risk";
                var risk = isMerge ? "medium" : "high";
                var type = isMerge ? "merge" : "missing";
                var title = isMerge ? "Rename/merge suggestion" : "Missing/new review";
                return new AdminQueueCaseListItemDto(
                    $"review:{x.Id}:{type}",
                    "review",
                    lane,
                    x.Username,
                    title,
                    risk,
                    "medium",
                    HumanizeAge(now - x.LastSeen),
                    isMerge ? "Confirm suggested previous player" : "Validate identity before add/remove");
            })
            .ToList();

        return promotionCases
            .Concat(reviewCases)
            .OrderByDescending(x => RiskRank(x.Risk))
            .ThenByDescending(x => ParseAgeMinutes(x.Age))
            .ToList();
    }

    private static HomeMeaningfulChangeDto? ToMeaningfulChange(int id, string eventType, string? player, DateTimeOffset createdAt, DateTimeOffset now)
    {
        if (eventType.Contains("DELETE_SCHEDULED", StringComparison.OrdinalIgnoreCase)) return null;

        var category = eventType.Contains("PROMOTION", StringComparison.OrdinalIgnoreCase)
            ? "Promotion"
            : eventType.Contains("MERGE", StringComparison.OrdinalIgnoreCase) || eventType.Contains("MISSING", StringComparison.OrdinalIgnoreCase)
                ? "Review"
                : eventType.Contains("STATUS", StringComparison.OrdinalIgnoreCase) || eventType.Contains("NEW_PLAYER", StringComparison.OrdinalIgnoreCase)
                    ? "Roster"
                    : "System";

        var tone = category switch
        {
            "Promotion" => "success",
            "Review" => "warning",
            "Roster" => "info",
            _ => "warning"
        };

        var title = player is null
            ? HumanizeEventType(eventType)
            : $"{HumanizeEventType(eventType)}: {player}";

        return new HomeMeaningfulChangeDto(id, category, tone, title, HumanizeAge(now - createdAt));
    }

    private static int RiskRank(string risk) => risk switch
    {
        "high" => 3,
        "medium" => 2,
        _ => 1
    };

    private static int ParseAgeMinutes(string age)
    {
        if (string.IsNullOrWhiteSpace(age)) return 0;
        var raw = age.Trim().ToLowerInvariant();
        if (raw.EndsWith("m") && int.TryParse(raw[..^1], out var mins)) return mins;
        if (raw.EndsWith("h") && int.TryParse(raw[..^1], out var hours)) return hours * 60;
        if (raw.EndsWith("d") && int.TryParse(raw[..^1], out var days)) return days * 24 * 60;
        return 0;
    }

    private static string ClassifyPromotionRisk(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "low";
        }

        if (reason.Contains("rename", StringComparison.OrdinalIgnoreCase) || reason.Contains("mismatch", StringComparison.OrdinalIgnoreCase))
        {
            return "medium";
        }

        return "low";
    }

    private static string ClassifyPromotionLane(string? reason)
    {
        return ClassifyPromotionRisk(reason) == "low" ? "safe" : "inspect";
    }

    private static string HumanizeEventType(string eventType)
    {
        var text = eventType.Replace("_", " ");
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
    }

    private static string HumanizeSeconds(int seconds)
    {
        if (seconds < 60) return $"{seconds}s ago";
        if (seconds < 3600) return $"{seconds / 60}m ago";
        if (seconds < 86400) return $"{seconds / 3600}h ago";
        return $"{seconds / 86400}d ago";
    }

    private static string HumanizeAge(TimeSpan age)
    {
        var minutes = Math.Max(0, (int)age.TotalMinutes);
        if (minutes < 60) return $"{minutes}m";
        var hours = minutes / 60;
        if (hours < 24) return $"{hours}h";
        return $"{hours / 24}d";
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
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var nested in prop.Value.EnumerateObject())
                    {
                        values[nested.Name] = JsonValueToString(nested.Value);
                    }
                }
                else
                {
                    values[prop.Name] = JsonValueToString(prop.Value);
                }
            }
        }
        catch
        {
            return values;
        }

        return values;
    }

    private static string JsonValueToString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
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

    private static ClanLogProjection? ToClanLogProjection(string eventType, string? player, Dictionary<string, string> metadata)
    {
        var evt = eventType.ToUpperInvariant();
        var actor = Pick(metadata, "HandledBy", "RequestedBy", "User", "IgnoredBy");
        var action = Pick(metadata, "Action");
        var status = Pick(metadata, "Status");
        var oldRank = Pick(metadata, "OldRank");
        var newRank = Pick(metadata, "NewRank");
        var suggestedPrevious = Pick(metadata, "SuggestedPrevious", "PreviousPlayer");
        var reason = Pick(metadata, "Reason");
        if (eventType.Contains("DELETE_SCHEDULED", StringComparison.OrdinalIgnoreCase))
        {
            return new ClanLogProjection(
                "System",
                "Routine cleanup queued",
                "A cleanup task was queued to remove obsolete Discord/system messages and keep channels readable.",
                true);
        }

        var playerName = player ?? Pick(metadata, "Username", "Player", "NewPlayer") ?? "Unknown player";
        if (evt == "PROMOTION_CANDIDATE_CREATED")
        {
            var rankText = !string.IsNullOrWhiteSpace(oldRank) && !string.IsNullOrWhiteSpace(newRank)
                ? $"{oldRank} -> {newRank}"
                : "next eligible rank";
            return new ClanLogProjection(
                "Promotion",
                "Promotion candidate created",
                $"{playerName} became eligible for promotion ({rankText}). This opens an officer decision in Admin Queue.",
                false);
        }

        if (evt == "PROMOTION_DISCORD_ACTION_APPLIED")
        {
            var handledBy = string.IsNullOrWhiteSpace(actor) ? "an officer" : actor;
            var actionText = string.IsNullOrWhiteSpace(action) ? "updated" : action.ToLowerInvariant();
            return new ClanLogProjection(
                "Promotion",
                "Promotion decision applied",
                $"{handledBy} {actionText} {playerName}'s promotion review. This changes whether the rank action proceeds.",
                false);
        }

        if (evt.Contains("PROMOTION", StringComparison.Ordinal))
        {
            var why = string.IsNullOrWhiteSpace(reason) ? "Promotion workflow progressed." : $"Reason: {reason}.";
            return new ClanLogProjection(
                "Promotion",
                HumanizeEventType(eventType),
                $"{playerName} promotion event recorded. {why}",
                false);
        }

        if (evt == "MERGE_SUGGESTED")
        {
            var previous = string.IsNullOrWhiteSpace(suggestedPrevious) ? "a previous member record" : suggestedPrevious;
            return new ClanLogProjection(
                "Review",
                "Possible rename detected",
                $"{playerName} may match {previous}. Officer review is needed to prevent duplicate timelines.",
                false);
        }

        if (evt == "MERGE_ACTION_APPLIED")
        {
            var handledBy = string.IsNullOrWhiteSpace(actor) ? "an officer" : actor;
            return new ClanLogProjection(
                "Review",
                "Rename review handled",
                $"{handledBy} resolved the rename/merge decision for {playerName}. This preserves correct player history continuity.",
                false);
        }

        if (evt is "MISSING_IN_ROSTER" or "TEMPLE_MISSING_ACTION_REQUIRED" or "WOM_MISSING_ACTION_REQUIRED")
        {
            return new ClanLogProjection(
                "Review",
                "Missing player review required",
                $"{playerName} was not found in one source and now needs admin confirmation to add/remove safely.",
                false);
        }

        if (evt == "STATUS_UPDATED")
        {
            var statusText = string.IsNullOrWhiteSpace(status) ? "new lifecycle state" : status;
            return new ClanLogProjection(
                "Roster",
                "Player status updated",
                $"{playerName} moved to {statusText}. This affects which queues and actions apply next.",
                false);
        }

        if (evt == "NEW_PLAYER")
        {
            return new ClanLogProjection(
                "Roster",
                "New player tracked",
                $"{playerName} entered tracker coverage and now appears in roster monitoring and review workflows.",
                false);
        }

        if (evt.Contains("RANK_MISMATCH", StringComparison.Ordinal))
        {
            return new ClanLogProjection(
                "Review",
                "Rank mismatch flagged",
                $"{playerName} has a Wise Old Man rank mismatch that requires officer confirmation or sync alignment.",
                false);
        }

        if (eventType.Contains("MERGE", StringComparison.OrdinalIgnoreCase) ||
            eventType.Contains("MISSING", StringComparison.OrdinalIgnoreCase) ||
            eventType.Contains("REVIEW", StringComparison.OrdinalIgnoreCase))
        {
            return new ClanLogProjection(
                "Review",
                HumanizeEventType(eventType),
                $"{playerName} review workflow changed. Admin attention may be required before lifecycle actions continue.",
                false);
        }

        if (evt.Contains("DISCORD", StringComparison.Ordinal) ||
            evt.Contains("COMMAND", StringComparison.Ordinal))
        {
            return new ClanLogProjection(
                "System",
                "Routine Discord/system update",
                $"{playerName} triggered a routine Discord/system sync step for operational consistency.",
                true);
        }

        if (eventType.Contains("SYNC", StringComparison.OrdinalIgnoreCase) ||
            eventType.Contains("PET_HISCORES", StringComparison.OrdinalIgnoreCase) ||
            eventType.Contains("PRIORITY", StringComparison.OrdinalIgnoreCase))
        {
            return new ClanLogProjection(
                "System",
                "Routine tracker maintenance",
                $"{playerName} was included in a routine sync/maintenance pass to keep tracker data current.",
                true);
        }

        return new ClanLogProjection(
            "System",
            HumanizeEventType(eventType),
            $"{playerName} generated a system event that was logged for operational traceability.",
            true);
    }
}

public record HomeResponseDto(
    HomeOverviewDto Overview,
    HomeHealthDto Health,
    IReadOnlyList<HomeRosterPostureItemDto> RosterPosture,
    IReadOnlyList<HomeMeaningfulChangeDto> MeaningfulChanges,
    IReadOnlyList<HomeWorkPreviewItemDto> WorkPreview);

public record HomeOverviewDto(int TotalMembers, int ActiveMembers, int PendingPromotions, int OpenAdminCases);
public record HomeHealthDto(string Overall, HomeApiHealthDto Api, HomeWorkerHealthDto Worker, HomeLatestSyncDto Sync);
public record HomeApiHealthDto(string State, int LatencyMs);
public record HomeWorkerHealthDto(string State, string CurrentPlayer, string LastHeartbeatAgo);
public record HomeLatestSyncDto(string LastPlayer, string SyncedAgo);
public record HomeRosterPostureItemDto(string Label, int Value, string Tone, string Hint);
public record HomeMeaningfulChangeDto(int Id, string Category, string Tone, string Title, string Time);
public record HomeWorkPreviewItemDto(string CaseId, string Label, string Risk, string Age);

public record AdminQueueCaseListItemDto(
    string Id,
    string Type,
    string Lane,
    string Player,
    string Title,
    string Risk,
    string Confidence,
    string Age,
    string RecommendedAction);

public record AdminQueueCaseDetailDto(
    string Id,
    string Type,
    string Player,
    string Title,
    string Risk,
    string Confidence,
    string Age,
    string RecommendedAction,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Alternatives,
    string Dangerous);

public record RosterResponseDto(IReadOnlyList<RosterRowDto> Rows);

public record RosterRowDto(
    int Id,
    string Username,
    string Rank,
    string Status,
    DateTimeOffset? LastSync,
    DateTimeOffset LastSeen,
    bool IsSyncStale,
    bool HasOpenReviewCase,
    bool HasPendingPromotion,
    bool HasRankMismatch);

public record PlayerProfileResponseDto(
    int Id,
    string Username,
    string CurrentRank,
    string EligibleRank,
    string Status,
    DateTimeOffset? LastSync,
    DateTimeOffset LastSeen,
    bool IsSyncMissing,
    bool HasPendingPromotion,
    bool HasRankMismatch,
    IReadOnlyList<PlayerProfileRecentEventDto> RecentEvents,
    IReadOnlyList<PlayerProfileOpenCaseDto> OpenCases,
    PlayerProfileHistoryAvailabilityDto HistoryAvailability);

public record PlayerProfileRecentEventDto(int Id, string Title, DateTimeOffset OccurredAt, string TimeAgo);
public record PlayerProfileOpenCaseDto(string Type, string Label);
public record PlayerProfileHistoryAvailabilityDto(bool RankHistoryAvailable, bool StatHistoryAvailable, string Reason);

public record ClanLogResponseDto(
    IReadOnlyList<string> Filters,
    IReadOnlyList<ClanLogImportantItemDto> Important,
    IReadOnlyList<string> Routine);

public record ClanLogImportantItemDto(
    string Id,
    string Group,
    string Title,
    string Detail,
    string Time);

public record ReadinessResponseDto(
    IReadOnlyList<ReadinessRuntimeItemDto> Runtime,
    IReadOnlyList<ReadinessConfigItemDto> Config);

public record ReadinessRuntimeItemDto(string Label, string State, string Tone, string Detail);
public record ReadinessConfigItemDto(string Key, string Value);

internal record ClanLogProjection(string Group, string Title, string Detail, bool IsRoutine);
