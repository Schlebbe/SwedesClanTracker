using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwedesClanTracker.Api.Contracts;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.Api.Services;

/// <summary>
/// Read-only projections for the operator console. This service deliberately does not
/// call external integrations or change lifecycle, promotion, or player state.
/// </summary>
public sealed class ReadOnlyConsoleQueryService(TrackerDbContext db, IConfiguration configuration)
{
    private static readonly string[] ReviewEventTypes =
    [
        "TEMPLE_MISSING_ACTION_REQUIRED",
        "WOM_MISSING_ACTION_REQUIRED",
        "WOM_ONLY_ACTION_REQUIRED",
        "TEMPLE_NAME_CHANGE_REQUIRED",
        "MERGE_ACTION_REQUIRED",
        "WOM_RANK_MISMATCH_REQUIRED"
    ];

    private static readonly string[] IgnoredEventTypes =
    [
        "WOM_ONLY_IGNORED",
        "WOM_RANK_MISMATCH_IGNORED"
    ];

    private static readonly string[] TrendRanges = ["30d", "90d", "all"];

    public async Task<ConsoleOverviewResponse> GetOverviewAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var health = await BuildHealthAsync(now, ct);
        var cases = await BuildReviewCasesAsync(now, ct);
        var activeCases = cases.Where(x => x.State is not "superseded" && x.State is not "ignored").ToArray();

        var openWork = activeCases
            .GroupBy(x => x.Family, StringComparer.OrdinalIgnoreCase)
            .Select(group => ToWorkLane(group.Key, group.ToArray()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var attention = await BuildAttentionAsync(cases, now, ct);
        var recentChanges = await GetRecentChangesAsync(now, 8, ct);
        return new ConsoleOverviewResponse(now, health, openWork, attention, recentChanges);
    }

    public async Task<ConsoleRosterResponse> GetRosterAsync(
        string? search,
        string? status,
        string? rank,
        string? attention,
        string? sort,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        page = Math.Max(1, page);
        pageSize = 50;

        var query = db.Players.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.Username.Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<PlayerStatus>(status, true, out var parsedStatus))
        {
            query = query.Where(x => x.Status == parsedStatus);
        }

        if (!string.IsNullOrWhiteSpace(rank))
        {
            var rankValue = rank.Trim();
            query = query.Where(x => x.CurrentRank == rankValue || x.EligibleRank == rankValue);
        }

        var pendingPromotionIds = await db.PromotionCandidates
            .AsNoTracking()
            .Where(x => x.Status == PromotionStatus.PENDING)
            .Select(x => x.PlayerId)
            .Distinct()
            .ToListAsync(ct);

        var mismatchIds = await db.LifecycleEvents
            .AsNoTracking()
            .Where(x => x.Status == "OPEN" && x.EventType == "WOM_RANK_MISMATCH_REQUIRED")
            .Select(x => x.PlayerId)
            .Distinct()
            .ToListAsync(ct);

        var reviewIds = await db.Players
            .AsNoTracking()
            .Where(x => x.Status == PlayerStatus.NEW_PENDING_REVIEW ||
                x.Status == PlayerStatus.MISSING_PENDING_REVIEW ||
                x.Status == PlayerStatus.MERGE_SUGGESTED)
            .Select(x => x.Id)
            .ToListAsync(ct);

        var staleIds = await db.Players
            .AsNoTracking()
            .Where(x => !x.LastSynced.HasValue || x.LastSynced < now.AddHours(-24))
            .Select(x => x.Id)
            .ToListAsync(ct);

        var attentionKey = attention?.Trim().ToLowerInvariant();
        if (attentionKey is "stale" or "review" or "promotion" or "rank-mismatch")
        {
            var ids = attentionKey switch
            {
                "stale" => staleIds,
                "review" => reviewIds,
                "promotion" => pendingPromotionIds,
                _ => mismatchIds
            };
            query = query.Where(x => ids.Contains(x.Id));
        }

        query = (sort ?? "username").Trim().ToLowerInvariant() switch
        {
            "lastsynced" => query.OrderByDescending(x => x.LastSynced).ThenBy(x => x.Username),
            "lastseen" => query.OrderByDescending(x => x.LastSeen).ThenBy(x => x.Username),
            "rank" => query.OrderByDescending(x => x.CurrentRank).ThenBy(x => x.Username),
            _ => query.OrderBy(x => x.Username)
        };

        var totalCount = await query.CountAsync(ct);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Min(page, totalPages);
        var players = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var playerIds = players.Select(x => x.Id).ToArray();
        var latestSnapshots = await db.PlayerSnapshots
            .AsNoTracking()
            .Where(x => playerIds.Contains(x.PlayerId))
            .GroupBy(x => x.PlayerId)
            .Select(group => group.OrderByDescending(x => x.Timestamp).ThenByDescending(x => x.Id).First())
            .ToDictionaryAsync(x => x.PlayerId, ct);

        var rows = players.Select(player =>
        {
            var flags = AttentionForPlayer(player, now, reviewIds.Contains(player.Id), pendingPromotionIds.Contains(player.Id), mismatchIds.Contains(player.Id));
            latestSnapshots.TryGetValue(player.Id, out var latest);
            return new ConsoleRosterRowResponse(
                player.Id,
                player.Username,
                player.CurrentRank,
                player.EligibleRank,
                player.Status.ToString(),
                player.LastSynced,
                player.LastSeen,
                FreshnessFor(player.LastSynced, now),
                flags,
                latest is null ? null : ToSnapshot(latest));
        }).ToArray();

        return new ConsoleRosterResponse(now, page, pageSize, totalCount, totalPages, rows);
    }

    public async Task<ConsoleProfileResponse?> GetProfileAsync(int playerId, string? range, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(x => x.Id == playerId, ct);
        if (player is null) return null;

        var selectedRange = NormalizeTrendRange(range);
        var cutoff = selectedRange switch
        {
            "30d" => now.AddDays(-30),
            "90d" => now.AddDays(-90),
            _ => DateTimeOffset.MinValue
        };

        var snapshots = await db.PlayerSnapshots
            .AsNoTracking()
            .Where(x => x.PlayerId == playerId && x.Timestamp >= cutoff)
            .OrderBy(x => x.Timestamp)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);

        var latest = await db.PlayerSnapshots
            .AsNoTracking()
            .Where(x => x.PlayerId == playerId)
            .OrderByDescending(x => x.Timestamp)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct);

        var cases = await BuildReviewCasesAsync(now, ct);
        var playerCases = cases
            .Where(x => x.PlayerId == playerId && x.State is not "superseded" and not "ignored")
            .OrderByDescending(x => x.OpenedAt)
            .Select(x => new ConsoleOpenCaseResponse(x.CaseId, x.Family, x.State, x.Summary, x.OpenedAt))
            .ToArray();

        var events = await db.LifecycleEvents
            .AsNoTracking()
            .Where(x => x.PlayerId == playerId && x.EventType != AppStatusConstants.EventType)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(40)
            .ToListAsync(ct);

        var lifecycle = events.Select(x => ToLifecycleEvent(x, player.Username, cases)).ToArray();
        return new ConsoleProfileResponse(
            player.Id,
            player.Username,
            player.CurrentRank,
            player.EligibleRank,
            player.Status.ToString(),
            player.LastSynced,
            player.LastSeen,
            FreshnessFor(player.LastSynced, now),
            latest is null ? null : ToSnapshot(latest),
            playerCases,
            lifecycle,
            selectedRange,
            snapshots.Select(ToTrendPoint).ToArray(),
            TrendRanges);
    }

    public async Task<ConsoleReviewQueueResponse> GetReviewQueueAsync(
        string? family,
        string? state,
        string? search,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var cases = await BuildReviewCasesAsync(now, ct);
        var query = cases.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(family))
        {
            query = query.Where(x => string.Equals(x.Family, family.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(state))
        {
            query = query.Where(x => string.Equals(x.State, state.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.Subject.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                x.Summary.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = query
            .OrderBy(x => x.State == "superseded" ? 1 : 0)
            .ThenByDescending(x => x.OpenedAt)
            .ToArray();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 50);
        var totalPages = Math.Max(1, (int)Math.Ceiling(filtered.Length / (double)pageSize));
        page = Math.Min(page, totalPages);
        var items = filtered.Skip((page - 1) * pageSize).Take(pageSize).Select(ToReviewResponse).ToArray();

        var lanes = filtered
            .Where(x => x.State is not "superseded" and not "ignored")
            .GroupBy(x => x.Family, StringComparer.OrdinalIgnoreCase)
            .Select(group => ToReviewLane(group.Key, group.ToArray()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ConsoleReviewQueueResponse(now, page, pageSize, filtered.Length, totalPages, lanes, items);
    }

    public async Task<ConsoleReviewDetailResponse?> GetReviewDetailAsync(string caseId, CancellationToken ct)
    {
        var cases = await BuildReviewCasesAsync(DateTimeOffset.UtcNow, ct);
        var item = cases.FirstOrDefault(x => string.Equals(x.CaseId, caseId, StringComparison.OrdinalIgnoreCase));
        if (item is null) return null;

        var suppressedSignals = item.SupersededCases
            .Select(x => $"{x.Family} work for {x.Subject} is superseded by this case.")
            .Concat(item.RelatedCases
                .Where(x => x.Relationship is "suppressed" or "superseded")
                .Select(x => $"{x.Family} signal for {x.Subject} is {x.Relationship}."))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ConsoleReviewDetailResponse(
            ToReviewResponse(item),
            item.Precedence,
            suppressedSignals,
            "read_only_gate_3",
            false);
    }

    public async Task<ConsoleIgnoredPoliciesResponse> GetIgnoredPoliciesAsync(CancellationToken ct)
    {
        var events = await db.LifecycleEvents
            .AsNoTracking()
            .Where(x => x.Status == "OPEN" && IgnoredEventTypes.Contains(x.EventType))
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

        var items = events.Select(x =>
        {
            var metadata = ReadMetadata(x.MetadataJson);
            var family = x.EventType == "WOM_ONLY_IGNORED" ? "wom-only" : "wom-rank-mismatch";
            var subject = Pick(metadata, "Username", "Player") ?? "Unknown subject";
            var action = new ConsoleActionDescriptorResponse(
                "resume",
                "Resume tracking",
                "safe",
                false,
                true,
                false,
                "Resume tracking is not available until Gate 4 decision architecture is approved.");
            return new ConsoleIgnoredPolicyResponse(
                $"ignored:{x.Id.ToString(CultureInfo.InvariantCulture)}",
                family,
                subject,
                x.CreatedAt,
                x.Status,
                Pick(metadata, "Source"),
                action);
        }).ToArray();

        return new ConsoleIgnoredPoliciesResponse(DateTimeOffset.UtcNow, items);
    }

    public async Task<ConsoleActivityResponse> GetActivityAsync(
        string? eventType,
        string? status,
        string? player,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? source,
        string? correlationId,
        int take,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        take = Math.Clamp(take <= 0 ? 100 : take, 1, 300);
        var query = db.LifecycleEvents.AsNoTracking().Where(x => x.EventType != AppStatusConstants.EventType);
        if (!string.IsNullOrWhiteSpace(eventType)) query = query.Where(x => x.EventType == eventType.Trim());
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status.Trim());
        if (from.HasValue) query = query.Where(x => x.CreatedAt >= from.Value);
        if (to.HasValue) query = query.Where(x => x.CreatedAt <= to.Value);

        var events = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(take)
            .ToListAsync(ct);

        var playerIds = events.Select(x => x.PlayerId).Distinct().ToArray();
        var players = await db.Players
            .AsNoTracking()
            .Where(x => playerIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);

        var projected = events
            .Select(x => ToActivityEvent(x, players.TryGetValue(x.PlayerId, out var player) ? player.Username : null, awaitCaseList: null))
            .Where(x => string.IsNullOrWhiteSpace(source) || string.Equals(x.Source, source.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(correlationId) || string.Equals(x.CorrelationId, correlationId.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(player) ||
                (x.PlayerId?.ToString(CultureInfo.InvariantCulture) == player.Trim()) ||
                x.PlayerName?.Contains(player.Trim(), StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();

        var eventTypes = await db.LifecycleEvents
            .AsNoTracking()
            .Where(x => x.EventType != AppStatusConstants.EventType)
            .Select(x => x.EventType)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(ct);
        var statuses = await db.LifecycleEvents
            .AsNoTracking()
            .Where(x => x.EventType != AppStatusConstants.EventType)
            .Select(x => x.Status)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(ct);

        return new ConsoleActivityResponse(now, projected.Length, eventTypes, statuses, projected);
    }

    public async Task<ConsoleSystemResponse> GetSystemAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var health = await BuildHealthAsync(now, ct);
        var checks = new List<ConsoleSystemCheckResponse>
        {
            new("API", health.Api.State, health.Api.Detail, health.Api.ObservedAt),
            new("Worker", health.Worker.State, health.Worker.Detail, health.Worker.ObservedAt),
            new("PostgreSQL", health.Database.State, health.Database.Detail, health.Database.ObservedAt),
            new("Latest sync", health.LatestSync.State, health.LatestSync.Detail, health.LatestSync.ObservedAt),
            new("Readiness", health.Readiness.State, health.Readiness.Detail, now)
        };

        var configurationChecks = new[]
        {
            ConfigurationCheck("TempleOsrs:ApiKey", "Temple API key", required: true),
            ConfigurationCheck("TempleOsrs:GroupId", "Temple group id", required: true),
            ConfigurationCheck("WiseOldMan:VerificationCode", "Wise Old Man verification code", required: true),
            ConfigurationCheck("WiseOldMan:GroupId", "Wise Old Man group id", required: true)
        };

        return new ConsoleSystemResponse(now, health, checks, configurationChecks);
    }

    private async Task<ConsoleHealthSummaryResponse> BuildHealthAsync(DateTimeOffset now, CancellationToken ct)
    {
        var latestSync = await db.Players
            .AsNoTracking()
            .Where(x => x.LastSynced.HasValue)
            .OrderByDescending(x => x.LastSynced)
            .Select(x => new { x.Username, x.LastSynced })
            .FirstOrDefaultAsync(ct);

        var workerStatus = await db.LifecycleEvents
            .AsNoTracking()
            .Where(x => x.EventType == AppStatusConstants.EventType && x.Status == "OPEN")
            .OrderByDescending(x => x.CreatedAt)
            .Take(50)
            .ToListAsync(ct);
        var worker = workerStatus
            .Select(x => new { Event = x, Metadata = ReadMetadata(x.MetadataJson) })
            .Where(x => !string.Equals(Pick(x.Metadata, "Component"), "API", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => TryParseDate(Pick(x.Metadata, "HeartbeatAt")) ?? x.Event.CreatedAt)
            .FirstOrDefault();

        var workerHeartbeat = worker is null ? null : TryParseDate(Pick(worker.Metadata, "HeartbeatAt"));
        var workerAge = workerHeartbeat.HasValue ? Math.Max(0, (int)(now - workerHeartbeat.Value).TotalSeconds) : (int?)null;
        var workerState = worker is null
            ? "unknown"
            : workerAge > 600
                ? "offline"
                : workerAge > 120
                    ? "stale"
                    : Pick(worker.Metadata, "State")?.ToLowerInvariant() switch
                    {
                        var value when value?.Contains("error", StringComparison.OrdinalIgnoreCase) == true => "offline",
                        var value when value?.Contains("work", StringComparison.OrdinalIgnoreCase) == true => "working",
                        var value when value?.Contains("sync", StringComparison.OrdinalIgnoreCase) == true => "syncing",
                        _ => "healthy"
                    };

        var workerDetail = worker is null
            ? "No worker heartbeat is available."
            : $"{Pick(worker.Metadata, "State") ?? "Unknown"}; heartbeat {FormatTimestamp(workerHeartbeat ?? worker.Event.CreatedAt)}.";
        var latestSyncState = latestSync?.LastSynced is null
            ? "unavailable"
            : latestSync.LastSynced < now.AddHours(-24) ? "stale" : "healthy";
        var latestSyncDetail = latestSync?.LastSynced is { } syncAt
            ? $"{latestSync.Username} at {FormatTimestamp(syncAt)}."
            : "No player sync timestamp is available.";

        var databaseState = "healthy";
        var databaseDetail = "Database query completed.";
        try
        {
            if (!await db.Database.CanConnectAsync(ct))
            {
                databaseState = "unavailable";
                databaseDetail = "Database reachability check returned unavailable.";
            }
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            databaseState = "unavailable";
            databaseDetail = $"Database reachability check failed: {ex.GetType().Name}.";
        }

        var readiness = BuildReadinessSummary();
        var overall = new[] { workerState, latestSyncState, databaseState, readiness.State }
            .Any(x => x is "offline" or "unavailable")
            ? "warning"
            : new[] { workerState, latestSyncState, readiness.State }.Any(x => x is "stale" or "unknown" or "missing")
                ? "warning"
                : "healthy";

        return new ConsoleHealthSummaryResponse(
            overall,
            new ConsoleHealthComponentResponse("healthy", "Authenticated API is responding.", now, null),
            new ConsoleHealthComponentResponse(workerState, workerDetail, workerHeartbeat ?? worker?.Event.CreatedAt, Pick(worker?.Metadata ?? new Dictionary<string, string>(), "Details.Username", "Username", "Player")),
            new ConsoleHealthComponentResponse(databaseState, databaseDetail, now, null),
            new ConsoleHealthComponentResponse(latestSyncState, latestSyncDetail, latestSync?.LastSynced, latestSync?.Username),
            readiness);
    }

    private ConsoleReadinessSummaryResponse BuildReadinessSummary()
    {
        var checks = new[]
        {
            configuration["TempleOsrs:ApiKey"],
            configuration["TempleOsrs:GroupId"],
            configuration["WiseOldMan:VerificationCode"],
            configuration["WiseOldMan:GroupId"]
        };
        var configured = checks.Count(x => !string.IsNullOrWhiteSpace(x));
        var state = configured == checks.Length ? "configured" : configured == 0 ? "unavailable" : "unknown";
        var detail = configured == checks.Length
            ? "Required client configuration keys are present; external reachability is observed by the Worker."
            : $"{configured} of {checks.Length} required configuration values are present. Secret values are not exposed.";
        return new ConsoleReadinessSummaryResponse(state, detail, configured, checks.Length);
    }

    private ConsoleConfigurationCheckResponse ConfigurationCheck(string key, string label, bool required)
    {
        var configured = !string.IsNullOrWhiteSpace(configuration[key]);
        return new ConsoleConfigurationCheckResponse(
            key,
            configured ? "configured" : required ? "unavailable" : "unknown",
            configured ? $"{label} is configured; value hidden." : $"{label} is not configured.");
    }

    private async Task<IReadOnlyList<ConsoleAttentionResponse>> BuildAttentionAsync(
        IReadOnlyList<ReviewCaseData> cases,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var stale = await db.Players.AsNoTracking()
            .Where(x => !x.LastSynced.HasValue || x.LastSynced < now.AddHours(-24))
            .Select(x => new { x.Id, x.LastSynced })
            .ToListAsync(ct);
        var reviewCases = cases.Where(x => (x.Family is "processing" or "temple-missing" or "wom-missing" or "rename-merge") && x.State != "superseded").ToArray();
        var promotion = cases.Where(x => x.Family == "promotion" && x.State != "superseded").ToArray();
        var mismatch = cases.Where(x => x.Family == "wom-rank-mismatch" && x.State != "superseded").ToArray();

        return
        [
            new ConsoleAttentionResponse("stale", "Stale sync", stale.Count, stale.Select(x => x.LastSynced).Where(x => x.HasValue).MinOrDefault(), stale.Count == 0 ? "success" : "warning", "/members?attention=stale"),
            new ConsoleAttentionResponse("review", "Review attention", reviewCases.Length, reviewCases.Select(x => (DateTimeOffset?)x.OpenedAt).MinOrDefault(), reviewCases.Length == 0 ? "success" : "danger", "/reviews?state=open"),
            new ConsoleAttentionResponse("promotion", "Promotion ready", promotion.Length, promotion.Select(x => (DateTimeOffset?)x.OpenedAt).MinOrDefault(), promotion.Length == 0 ? "success" : "warning", "/reviews?family=promotion"),
            new ConsoleAttentionResponse("rank-mismatch", "Rank observations", mismatch.Length, mismatch.Select(x => (DateTimeOffset?)x.OpenedAt).MinOrDefault(), mismatch.Length == 0 ? "success" : "warning", "/reviews?family=wom-rank-mismatch")
        ];
    }

    private async Task<IReadOnlyList<ConsoleRecentChangeResponse>> GetRecentChangesAsync(DateTimeOffset now, int take, CancellationToken ct)
    {
        var events = await db.LifecycleEvents
            .AsNoTracking()
            .Where(x => x.EventType != AppStatusConstants.EventType)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(100)
            .ToListAsync(ct);
        var playerIds = events.Select(x => x.PlayerId).Distinct().ToArray();
        var players = await db.Players.AsNoTracking().Where(x => playerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        return events
            .Select(x => ToActivityEvent(x, players.TryGetValue(x.PlayerId, out var player) ? player.Username : null, null))
            .OrderByDescending(x => x.OccurredAt)
            .Take(take)
            .Select(x => new ConsoleRecentChangeResponse(
                x.EventId,
                x.EventType,
                x.Label,
                x.Evidence.FirstOrDefault(e => e.Label == "Observation")?.Value ?? x.Label,
                x.Status,
                x.OccurredAt,
                x.PlayerId?.ToString(CultureInfo.InvariantCulture),
                x.PlayerName,
                x.Source,
                x.CorrelationId,
                x.CaseId))
            .ToArray();
    }

    private async Task<List<ReviewCaseData>> BuildReviewCasesAsync(DateTimeOffset now, CancellationToken ct)
    {
        var players = await db.Players.AsNoTracking().ToListAsync(ct);
        var byId = players.ToDictionary(x => x.Id);
        var byUsername = players
            .GroupBy(x => UsernameRules.NormalizeUsername(x.Username), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var events = await db.LifecycleEvents
            .AsNoTracking()
            .Where(x => x.Status == "OPEN" && (ReviewEventTypes.Contains(x.EventType) || x.EventType == "NEW_PLAYER"))
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync(ct);
        var candidates = await db.PromotionCandidates
            .AsNoTracking()
            .Where(x => x.Status == PromotionStatus.PENDING)
            .Include(x => x.Player)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

        var result = new List<ReviewCaseData>();
        foreach (var candidate in candidates)
        {
            result.Add(CreatePromotionCase(candidate, now));
        }

        foreach (var group in events.GroupBy(x => x.EventType, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key == "NEW_PLAYER") continue;
            foreach (var lifecycle in group.GroupBy(x => ResolveSubjectKey(x, byId)))
            {
                var eventRow = lifecycle.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).First();
                result.Add(CreateLifecycleCase(eventRow, byId, byUsername, now));
            }
        }

        foreach (var player in players.Where(x => x.Status == PlayerStatus.NEW_PENDING_REVIEW))
        {
            var newEvent = events.FirstOrDefault(x => x.EventType == "NEW_PLAYER" && x.PlayerId == player.Id);
            result.Add(CreateProcessingCase(player, newEvent?.CreatedAt ?? player.LastSeen, now));
        }

        ApplyPrecedence(result);
        return result
            .OrderBy(x => x.State == "superseded" ? 1 : 0)
            .ThenByDescending(x => x.OpenedAt)
            .ToList();
    }

    private static void ApplyPrecedence(List<ReviewCaseData> cases)
    {
        foreach (var merge in cases.Where(x => x.Family == "rename-merge" && x.State != "superseded"))
        {
            var related = cases.Where(x => x != merge && x.PlayerId.HasValue && x.PlayerId == merge.PlayerId).ToArray();
            foreach (var item in related)
            {
                item.State = "superseded";
                item.Precedence = "Rename/merge is the controlling identity signal for this player.";
                merge.SupersededCases.Add(new ConsoleRelatedCaseResponse(item.CaseId, item.Family, "superseded", item.Subject));
                item.RelatedCases.Add(new ConsoleRelatedCaseResponse(merge.CaseId, merge.Family, "superseded", merge.Subject));
            }
        }

        foreach (var templeNameChange in cases.Where(x => x.Family == "temple-name-change" && x.State != "superseded"))
        {
            foreach (var item in cases.Where(x => x != templeNameChange && x.PlayerId.HasValue && x.PlayerId == templeNameChange.PlayerId && x.Family is "temple-missing" or "wom-missing" or "wom-only"))
            {
                item.State = "superseded";
                item.Precedence = "Temple name-change evidence is the controlling review signal.";
                templeNameChange.SupersededCases.Add(new ConsoleRelatedCaseResponse(item.CaseId, item.Family, "superseded", item.Subject));
                item.RelatedCases.Add(new ConsoleRelatedCaseResponse(templeNameChange.CaseId, templeNameChange.Family, "superseded", templeNameChange.Subject));
            }
        }

        foreach (var playerGroup in cases.Where(x => x.PlayerId.HasValue).GroupBy(x => x.PlayerId!.Value))
        {
            var temple = playerGroup.FirstOrDefault(x => x.Family == "temple-missing" && x.State != "superseded");
            var wom = playerGroup.FirstOrDefault(x => x.Family == "wom-missing" && x.State != "superseded");
            if (temple is null || wom is null) continue;
            temple.State = wom.State = "conflict";
            temple.Warnings.Add("Temple and Wise Old Man missing signals are both open; the external observations conflict and require recheck.");
            wom.Warnings.Add("Temple and Wise Old Man missing signals are both open; the external observations conflict and require recheck.");
            temple.RelatedCases.Add(new ConsoleRelatedCaseResponse(wom.CaseId, wom.Family, "related", wom.Subject));
            wom.RelatedCases.Add(new ConsoleRelatedCaseResponse(temple.CaseId, temple.Family, "related", temple.Subject));
        }
    }

    private static ReviewCaseData CreatePromotionCase(PromotionCandidate candidate, DateTimeOffset now)
    {
        var evidence = new List<ConsoleEvidenceResponse>
        {
            new("Current rank", candidate.OldRank, "observed", candidate.CreatedAt),
            new("Eligible rank", candidate.NewRank, "observed", candidate.CreatedAt),
            new("Candidate reason", candidate.Reason, "observed", candidate.CreatedAt),
            new("Candidate status", candidate.Status.ToString(), "observed", candidate.CreatedAt)
        };
        var observations = new List<string> { $"Promotion candidate recorded for {candidate.Player.Username}: {candidate.OldRank} → {candidate.NewRank}." };
        var warnings = new List<string> { "This Gate 3 projection cannot approve, dismiss, or requeue the candidate." };
        var actions = Actions(
            ("approve", "Approve promotion", "safe", false),
            ("dismiss", "Dismiss candidate", "warning", false),
            ("mark-rename", "Mark rename suspect", "warning", false));
        return new ReviewCaseData(
            $"promotion:{candidate.Id.ToString(CultureInfo.InvariantCulture)}",
            "promotion",
            "open",
            candidate.Player.Username,
            candidate.PlayerId,
            candidate.CreatedAt,
            candidate.CreatedAt,
            "Promotion candidate is still pending.",
            "OPEN",
            "promotion-candidate",
            evidence,
            observations,
            warnings,
            actions,
            "No durable case version or execution idempotency key exists in the current schema.");
    }

    private static ReviewCaseData CreateProcessingCase(Player player, DateTimeOffset openedAt, DateTimeOffset now)
    {
        return new ReviewCaseData(
            $"processing:{player.Id.ToString(CultureInfo.InvariantCulture)}",
            "processing",
            "processing",
            player.Username,
            player.Id,
            openedAt,
            player.LastSeen,
            "New player is pending initial review and supported snapshot collection.",
            player.Status.ToString(),
            "player-status",
            [new ConsoleEvidenceResponse("Player status", player.Status.ToString(), "observed", openedAt)],
            [$"New player status recorded for {player.Username}; no review outcome is inferred."],
            ["Processing state is informational. No action is available in Gate 3."],
            [],
            "Processing remains pending until existing sync/review behavior changes the persisted state.");
    }

    private static ReviewCaseData CreateLifecycleCase(
        LifecycleEvent lifecycle,
        IReadOnlyDictionary<int, Player> byId,
        IReadOnlyDictionary<string, Player> byUsername,
        DateTimeOffset now)
    {
        var metadata = ReadMetadata(lifecycle.MetadataJson);
        var family = FamilyForEvent(lifecycle.EventType);
        var subjectName = Pick(metadata, "NewUsername", "Username", "Player", "NewPlayer") ??
            (byId.TryGetValue(lifecycle.PlayerId, out var player) ? player.Username : "Unknown subject");
        int? subjectPlayerId = null;
        if (lifecycle.EventType == "WOM_ONLY_ACTION_REQUIRED")
        {
            if (byUsername.TryGetValue(UsernameRules.NormalizeUsername(subjectName), out var womPlayer))
            {
                subjectPlayerId = womPlayer.Id;
            }
        }
        else if (byId.ContainsKey(lifecycle.PlayerId))
        {
            subjectPlayerId = lifecycle.PlayerId;
        }
        var evidence = new List<ConsoleEvidenceResponse>();
        var observations = new List<string>();
        var warnings = new List<string> { "Review actions are informational only until the Gate 4 decision API exists." };
        var summary = SummaryFor(family, subjectName);
        var precedence = "No higher-precedence signal was derived for this case.";
        IReadOnlyList<ConsoleActionDescriptorResponse> actions;

        AddEvidence(evidence, "Source", Pick(metadata, "Source", "Channel"), lifecycle.CreatedAt);
        AddEvidence(evidence, "Observed at", Pick(metadata, "ObservedAt", "DetectedAt", "MissingAt"), lifecycle.CreatedAt);

        switch (family)
        {
            case "temple-missing":
                AddEvidence(evidence, "Temple membership", Pick(metadata, "InTemple", "TempleMembership"), lifecycle.CreatedAt);
                AddEvidence(evidence, "WOM membership", Pick(metadata, "InWom", "WomMembership"), lifecycle.CreatedAt);
                observations.Add($"Temple membership review signal is open for {subjectName}.");
                actions = Actions(
                    ("add-temple", "Add back to Temple and ensure WOM membership", "safe", false),
                    ("remove-wom-delete", "Remove from WOM and permanently delete the tracker record", "danger", true));
                break;
            case "wom-missing":
                AddEvidence(evidence, "Temple membership", Pick(metadata, "InTemple", "TempleMembership"), lifecycle.CreatedAt);
                AddEvidence(evidence, "WOM membership", Pick(metadata, "InWom", "WomMembership"), lifecycle.CreatedAt);
                observations.Add($"Wise Old Man membership review signal is open for {subjectName}.");
                actions = Actions(
                    ("reinstate-wom", "Reinstate in WOM", "safe", false),
                    ("remove-temple-delete", "Remove from Temple and permanently delete the tracker record", "danger", true));
                break;
            case "wom-only":
                AddEvidence(evidence, "WOM role", Pick(metadata, "ActualWomRole"), lifecycle.CreatedAt);
                AddEvidence(evidence, "Detected at", Pick(metadata, "DetectedAt"), lifecycle.CreatedAt);
                observations.Add($"{subjectName} is present in Wise Old Man data without a tracked player identity.");
                actions = Actions(
                    ("add-temple", "Add to Temple", "safe", false),
                    ("ignore", "Ignore future tracking", "warning", false));
                break;
            case "temple-name-change":
                AddEvidence(evidence, "Previous RSN", Pick(metadata, "PreviousUsername", "PreviousPlayer"), lifecycle.CreatedAt);
                AddEvidence(evidence, "New RSN", Pick(metadata, "NewUsername", "Username", "NewPlayer"), lifecycle.CreatedAt);
                AddEvidence(evidence, "Rank", Pick(metadata, "Rank", "CurrentRank"), lifecycle.CreatedAt);
                AddEvidence(evidence, "WOM role", Pick(metadata, "WomRole", "ActualWomRole"), lifecycle.CreatedAt);
                observations.Add($"A Temple name-change requirement is open for {subjectName}; manual external follow-up remains unconfirmed.");
                warnings.Add("The current API does not prove an external Temple rename has been applied.");
                actions = Actions(
                    ("confirm-rename", "Confirm likely rename / begin manual Temple follow-up", "warning", false),
                    ("decline", "Decline", "warning", false));
                break;
            case "rename-merge":
                AddEvidence(evidence, "Suggested previous RSN", Pick(metadata, "SuggestedPrevious", "PreviousPlayer"), lifecycle.CreatedAt);
                AddEvidence(evidence, "Alternative previous RSNs", Pick(metadata, "CandidatePreviousPlayers"), lifecycle.CreatedAt);
                observations.Add($"Rename/merge review is open for {subjectName}; no identity transfer is inferred by this projection.");
                warnings.Add("Snapshot, lifecycle, promotion, and external cleanup impact require Gate 4 preflight architecture.");
                actions = Actions(
                    ("confirm-previous", "Confirm suggested previous player", "warning", true),
                    ("choose-alternative", "Choose listed alternative", "warning", true),
                    ("enter-previous", "Enter previous RSN manually", "warning", true),
                    ("abort", "Abort", "danger", false));
                break;
            case "wom-rank-mismatch":
                AddEvidence(evidence, "Expected tracker rank", Pick(metadata, "ExpectedRank"), lifecycle.CreatedAt);
                AddEvidence(evidence, "Actual WOM role", Pick(metadata, "ActualWomRole"), lifecycle.CreatedAt);
                AddEvidence(evidence, "Direction", Pick(metadata, "Direction"), lifecycle.CreatedAt);
                observations.Add($"Tracker rank and Wise Old Man role differ for {subjectName}; the observation is open.");
                actions = Actions(
                    ("update-wom", "Update WOM rank", "warning", false),
                    ("update-tracker", "Update tracker rank", "warning", false),
                    ("ignore", "Ignore future mismatch", "warning", false));
                break;
            default:
                actions = [];
                break;
        }

        if (evidence.All(x => x.Value is null))
        {
            warnings.Add("This lifecycle event has no structured external observation fields; only the persisted event fact is shown.");
        }

        return new ReviewCaseData(
            CaseIdForEvent(lifecycle, family),
            family,
            "open",
            subjectName,
            subjectPlayerId,
            lifecycle.CreatedAt,
            TryParseDate(Pick(metadata, "ObservedAt", "DetectedAt", "MissingAt")) ?? lifecycle.CreatedAt,
            summary,
            lifecycle.Status,
            Pick(metadata, "Source", "Channel"),
            evidence,
            observations,
            warnings,
            actions,
            precedence);
    }

    private static ConsoleReviewCaseResponse ToReviewResponse(ReviewCaseData item)
    {
        return new ConsoleReviewCaseResponse(
            item.CaseId,
            item.Family,
            item.State,
            item.Subject,
            item.PlayerId,
            item.OpenedAt,
            item.ObservedAt,
            item.Summary,
            item.Status,
            item.Source,
            null,
            item.Evidence,
            item.Observations,
            item.Warnings,
            item.Actions,
            item.RelatedCases,
            item.SupersededCases);
    }

    private static ConsoleReviewLaneResponse ToReviewLane(string family, IReadOnlyList<ReviewCaseData> items)
    {
        var (label, tone) = LaneLabel(family);
        return new ConsoleReviewLaneResponse(family, label, items.Count, tone);
    }

    private static ConsoleWorkLaneResponse ToWorkLane(string family, IReadOnlyList<ReviewCaseData> items)
    {
        var (label, tone) = LaneLabel(family);
        return new ConsoleWorkLaneResponse(family, label, items.Count, items.Select(x => (DateTimeOffset?)x.OpenedAt).MinOrDefault(), tone, $"/reviews?family={Uri.EscapeDataString(family)}");
    }

    private static (string Label, string Tone) LaneLabel(string family) => family switch
    {
        "promotion" => ("Promotions", "warning"),
        "temple-missing" => ("Temple missing", "danger"),
        "wom-missing" => ("WOM missing", "danger"),
        "wom-only" => ("WOM only", "warning"),
        "temple-name-change" => ("Temple name change", "info"),
        "rename-merge" => ("Rename / merge", "warning"),
        "wom-rank-mismatch" => ("Rank mismatch", "warning"),
        "processing" => ("Processing", "info"),
        _ => (family, "neutral")
    };

    private static IReadOnlyList<ConsoleActionDescriptorResponse> Actions(params (string Id, string Label, string Direction, bool RequiresExactRsn)[] actions) =>
        actions.Select(action => new ConsoleActionDescriptorResponse(
            action.Id,
            action.Label,
            action.Direction,
            false,
            true,
            action.RequiresExactRsn,
            "Read-only foundation: execution and preflight are not available in Gate 3.")).ToArray();

    private static ConsoleLifecycleEventResponse ToLifecycleEvent(LifecycleEvent lifecycle, string username, IReadOnlyList<ReviewCaseData> cases)
    {
        var metadata = ReadMetadata(lifecycle.MetadataJson);
        var family = FamilyForEvent(lifecycle.EventType);
        var caseId = cases.FirstOrDefault(x => x.CaseId == CaseIdForEvent(lifecycle, family))?.CaseId;
        var evidence = new List<ConsoleEvidenceResponse>();
        AddEvidence(evidence, "Source", Pick(metadata, "Source", "Channel"), lifecycle.CreatedAt);
        AddEvidence(evidence, "Observation", ObservationForEvent(lifecycle.EventType, username, metadata), lifecycle.CreatedAt);
        return new ConsoleLifecycleEventResponse(
            lifecycle.Id.ToString(CultureInfo.InvariantCulture),
            lifecycle.EventType,
            HumanizeEventType(lifecycle.EventType),
            lifecycle.Status,
            lifecycle.CreatedAt,
            Pick(metadata, "Source", "Channel"),
            Pick(metadata, "CorrelationId", "CorrelationID", "TraceId"),
            caseId,
            evidence);
    }

    private static ConsoleActivityEventResponse ToActivityEvent(LifecycleEvent lifecycle, string? username, IReadOnlyList<ReviewCaseData>? awaitCaseList)
    {
        var metadata = ReadMetadata(lifecycle.MetadataJson);
        var family = FamilyForEvent(lifecycle.EventType);
        var caseId = family is "unknown" ? null : CaseIdForEvent(lifecycle, family);
        var observation = ObservationForEvent(lifecycle.EventType, username, metadata);
        var evidence = new List<ConsoleEvidenceResponse>
        {
            new("Observation", observation, "observed", lifecycle.CreatedAt),
            new("Source", Pick(metadata, "Source", "Channel"), Pick(metadata, "Source", "Channel") is null ? "unknown" : "observed", lifecycle.CreatedAt)
        };
        return new ConsoleActivityEventResponse(
            lifecycle.Id.ToString(CultureInfo.InvariantCulture),
            lifecycle.EventType,
            HumanizeEventType(lifecycle.EventType),
            lifecycle.Status,
            lifecycle.CreatedAt,
            lifecycle.PlayerId > 0 ? lifecycle.PlayerId : null,
            username,
            Pick(metadata, "Source", "Channel"),
            Pick(metadata, "CorrelationId", "CorrelationID", "TraceId"),
            caseId,
            evidence);
    }

    private static string? ResolveSubjectKey(LifecycleEvent lifecycle, IReadOnlyDictionary<int, Player> byId)
    {
        var metadata = ReadMetadata(lifecycle.MetadataJson);
        if (lifecycle.EventType == "WOM_ONLY_ACTION_REQUIRED")
        {
            return UsernameRules.NormalizeUsername(Pick(metadata, "Username", "Player") ?? $"anchor:{lifecycle.PlayerId}");
        }
        return byId.TryGetValue(lifecycle.PlayerId, out var player)
            ? $"player:{player.Id}"
            : $"anchor:{lifecycle.PlayerId}";
    }

    private static string FamilyForEvent(string eventType) => eventType switch
    {
        "TEMPLE_MISSING_ACTION_REQUIRED" => "temple-missing",
        "WOM_MISSING_ACTION_REQUIRED" => "wom-missing",
        "WOM_ONLY_ACTION_REQUIRED" => "wom-only",
        "TEMPLE_NAME_CHANGE_REQUIRED" => "temple-name-change",
        "MERGE_ACTION_REQUIRED" => "rename-merge",
        "WOM_RANK_MISMATCH_REQUIRED" => "wom-rank-mismatch",
        _ => "unknown"
    };

    private static string CaseIdForEvent(LifecycleEvent lifecycle, string family) =>
        $"{family}:{lifecycle.Id.ToString(CultureInfo.InvariantCulture)}";

    private static string SummaryFor(string family, string subject) => family switch
    {
        "temple-missing" => $"Temple membership decision is open for {subject}.",
        "wom-missing" => $"Wise Old Man membership decision is open for {subject}.",
        "wom-only" => $"{subject} is observed in Wise Old Man data without a tracker record.",
        "temple-name-change" => $"Temple name-change follow-up is open for {subject}.",
        "rename-merge" => $"Rename/merge review is open for {subject}.",
        "wom-rank-mismatch" => $"Wise Old Man and tracker rank observations differ for {subject}.",
        _ => $"Lifecycle review signal is open for {subject}."
    };

    private static string ObservationForEvent(string eventType, string? username, IReadOnlyDictionary<string, string> metadata)
    {
        var subject = username ?? Pick(metadata, "Username", "Player", "NewPlayer") ?? "the subject";
        return eventType switch
        {
            "NEW_PLAYER" => $"{subject} was recorded as a new player.",
            "MISSING_IN_ROSTER" => $"{subject} was observed missing from a roster source.",
            "TEMPLE_MISSING_ACTION_REQUIRED" => $"Temple membership review signal is open for {subject}.",
            "WOM_MISSING_ACTION_REQUIRED" => $"Wise Old Man membership review signal is open for {subject}.",
            "WOM_ONLY_ACTION_REQUIRED" => $"{subject} was observed in Wise Old Man data without a tracker record.",
            "MERGE_ACTION_REQUIRED" => $"Rename/merge review signal is open for {subject}.",
            "TEMPLE_NAME_CHANGE_REQUIRED" => $"Temple name-change follow-up is open for {subject}.",
            "WOM_RANK_MISMATCH_REQUIRED" => $"Rank mismatch observation is open for {subject}.",
            _ => HumanizeEventType(eventType)
        };
    }

    private static string HumanizeEventType(string eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType)) return "Unknown event";
        var normalized = eventType.Replace('_', ' ').Trim().ToLowerInvariant();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized);
    }

    private static string FreshnessFor(DateTimeOffset? lastSynced, DateTimeOffset now) => lastSynced switch
    {
        null => "unavailable",
        var value when value < now.AddHours(-24) => "stale",
        _ => "current"
    };

    private static string[] AttentionForPlayer(Player player, DateTimeOffset now, bool review, bool promotion, bool mismatch)
    {
        var items = new List<string>();
        if (!player.LastSynced.HasValue || player.LastSynced < now.AddHours(-24)) items.Add("stale");
        if (review) items.Add("review");
        if (promotion) items.Add("promotion");
        if (mismatch) items.Add("rank-mismatch");
        return items.ToArray();
    }

    private static ConsoleSnapshotResponse ToSnapshot(PlayerSnapshot snapshot) =>
        new(snapshot.Timestamp, snapshot.TotalLevel, snapshot.Ehb, snapshot.Ehp, snapshot.Collections, snapshot.PetCount);

    private static ConsoleTrendPointResponse ToTrendPoint(PlayerSnapshot snapshot) =>
        new(snapshot.Timestamp, snapshot.TotalLevel, snapshot.Ehb, snapshot.Ehp, snapshot.Collections, snapshot.PetCount);

    private static string NormalizeTrendRange(string? range) =>
        TrendRanges.Contains(range?.Trim().ToLowerInvariant()) ? range!.Trim().ToLowerInvariant() : "90d";

    private static void AddEvidence(List<ConsoleEvidenceResponse> evidence, string label, string? value, DateTimeOffset observedAt)
    {
        evidence.Add(new ConsoleEvidenceResponse(label, value, value is null ? "unknown" : "observed", observedAt));
    }

    private static string? Pick(IReadOnlyDictionary<string, string> metadata, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return TrimValue(value);
        }
        return null;
    }

    private static Dictionary<string, string> ReadMetadata(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                Flatten(document.RootElement, result, "");
            }
        }
        catch (JsonException)
        {
            // Malformed historical metadata is represented as unknown, never as an API error.
        }
        return result;
    }

    private static void Flatten(JsonElement element, Dictionary<string, string> values, string prefix)
    {
        foreach (var property in element.EnumerateObject())
        {
            var key = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    Flatten(property.Value, values, key);
                    break;
                case JsonValueKind.Array:
                    values[key] = property.Value.GetRawText();
                    break;
                case JsonValueKind.String:
                    values[key] = property.Value.GetString() ?? "";
                    break;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    values[key] = property.Value.ToString();
                    break;
            }
        }
    }

    private static DateTimeOffset? TryParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result) ? result : null;

    private static string FormatTimestamp(DateTimeOffset timestamp) => timestamp.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static string TrimValue(string value) => value.Length <= 512 ? value : value[..512] + "…";

    private sealed class ReviewCaseData(
        string caseId,
        string family,
        string state,
        string subject,
        int? playerId,
        DateTimeOffset openedAt,
        DateTimeOffset? observedAt,
        string summary,
        string status,
        string? source,
        IReadOnlyList<ConsoleEvidenceResponse> evidence,
        IReadOnlyList<string> observations,
        IReadOnlyList<string> warnings,
        IReadOnlyList<ConsoleActionDescriptorResponse> actions,
        string precedence)
    {
        public string CaseId { get; } = caseId;
        public string Family { get; } = family;
        public string State { get; set; } = state;
        public string Subject { get; } = subject;
        public int? PlayerId { get; } = playerId;
        public DateTimeOffset OpenedAt { get; } = openedAt;
        public DateTimeOffset? ObservedAt { get; } = observedAt;
        public string Summary { get; } = summary;
        public string Status { get; } = status;
        public string? Source { get; } = source;
        public IReadOnlyList<ConsoleEvidenceResponse> Evidence { get; } = evidence;
        public IReadOnlyList<string> Observations { get; } = observations;
        public List<string> Warnings { get; } = warnings.ToList();
        public IReadOnlyList<ConsoleActionDescriptorResponse> Actions { get; } = actions;
        public List<ConsoleRelatedCaseResponse> RelatedCases { get; } = [];
        public List<ConsoleRelatedCaseResponse> SupersededCases { get; } = [];
        public string Precedence { get; set; } = precedence;
    }
}

internal static class ConsoleQueryExtensions
{
    public static DateTimeOffset? MinOrDefault(this IEnumerable<DateTimeOffset?> values)
    {
        var actual = values.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        return actual.Length == 0 ? null : actual.Min();
    }

    public static string NormalizeUsername(this string value) => UsernameRules.NormalizeUsername(value);
}
