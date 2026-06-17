using Microsoft.EntityFrameworkCore;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.Worker;

public class TrackerWorker(
    IServiceScopeFactory scopeFactory,
    IPlayerUpdateQueue queue,
    AppStatusReporter statusReporter,
    ILogger<TrackerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextRosterSyncUtc = DateTimeOffset.UtcNow;
        await statusReporter.ReportAsync("Tracker", "Starting", "Tracker worker is starting.", stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            int? currentPlayerId = null;
            string? currentUsername = null;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var sync = scope.ServiceProvider.GetRequiredService<ITrackerSyncService>();
                var templeClient = scope.ServiceProvider.GetRequiredService<ITempleClient>();
                var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
                var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                var callsPerMinute = Math.Max(1, config.GetValue<int?>("Tracker:TempleApiCallsPerMinute") ?? 5);
                var playerDelay = TimeSpan.FromSeconds((60d / callsPerMinute) * 2d);

                if (DateTimeOffset.UtcNow >= nextRosterSyncUtc)
                {
                    await statusReporter.ReportAsync("Tracker", "Syncing roster", "Checking the Temple roster and queueing player updates.", stoppingToken);
                    var priorityRequests = await db.LifecycleEvents
                        .Where(x => x.EventType == "PRIORITY_UPDATE_REQUEST" && x.Status == "OPEN")
                        .OrderBy(x => x.CreatedAt)
                        .ToListAsync(stoppingToken);
                    HashSet<string>? rosterSet = null;
                    foreach (var req in priorityRequests)
                    {
                        var player = await db.Players.FirstOrDefaultAsync(x => x.Id == req.PlayerId, stoppingToken);
                        if (player is not null)
                        {
                            rosterSet ??= (await templeClient.GetRosterAsync(stoppingToken))
                                .Select(x => x.Trim())
                                .Where(x => !string.IsNullOrWhiteSpace(x))
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);

                            if (!rosterSet.Contains(player.Username) && player.Status != PlayerStatus.REMOVED_CONFIRMED)
                            {
                                player.Status = PlayerStatus.MISSING_PENDING_REVIEW;
                                var hasOpenMissingEvent = await db.LifecycleEvents.AnyAsync(x =>
                                    x.PlayerId == player.Id &&
                                    x.EventType == "MISSING_IN_ROSTER" &&
                                    x.Status == "OPEN", stoppingToken);
                                if (!hasOpenMissingEvent)
                                {
                                    db.LifecycleEvents.Add(new LifecycleEvent
                                    {
                                        PlayerId = player.Id,
                                        EventType = "MISSING_IN_ROSTER",
                                        MetadataJson = JsonUtil.Serialize(new { player.Username, MissingAt = DateTimeOffset.UtcNow, Source = "PRIORITY_UPDATE_REQUEST" }),
                                        Status = "OPEN",
                                        CreatedAt = DateTimeOffset.UtcNow
                                    });
                                }
                                var hasPendingAction = await db.LifecycleEvents.AnyAsync(x =>
                                    x.PlayerId == player.Id &&
                                    x.EventType == "TEMPLE_MISSING_ACTION_REQUIRED" &&
                                    x.Status == "OPEN", stoppingToken);
                                if (!hasPendingAction)
                                {
                                    db.LifecycleEvents.Add(new LifecycleEvent
                                    {
                                        PlayerId = player.Id,
                                        EventType = "TEMPLE_MISSING_ACTION_REQUIRED",
                                        MetadataJson = JsonUtil.Serialize(new { player.Username, MissingAt = DateTimeOffset.UtcNow, Source = "PRIORITY_UPDATE_REQUEST" }),
                                        Status = "OPEN",
                                        CreatedAt = DateTimeOffset.UtcNow
                                    });
                                }
                            }
                            else if (player.Status is PlayerStatus.MISSING_PENDING_REVIEW or PlayerStatus.NEW_PENDING_REVIEW)
                            {
                                queue.EnqueueMissingPriority(player.Id);
                            }
                            else
                            {
                                queue.EnqueueFront(player.Id);
                            }
                        }
                        req.Status = "DONE";
                    }
                    if (priorityRequests.Count > 0)
                    {
                        await db.SaveChangesAsync(stoppingToken);
                    }

                    var ids = await sync.SyncRosterAndQueueAsync(stoppingToken);
                    var queuedStatuses = await db.Players
                        .Where(x => ids.Contains(x.Id))
                        .Select(x => new { x.Id, x.Status })
                        .ToDictionaryAsync(x => x.Id, x => x.Status, stoppingToken);

                    foreach (var id in ids)
                    {
                        if (!queuedStatuses.TryGetValue(id, out var status)) continue;
                        if (status is PlayerStatus.NEW_PENDING_REVIEW or PlayerStatus.MISSING_PENDING_REVIEW)
                            queue.EnqueueMissingPriority(id);
                        else
                            queue.EnqueueBack(id);
                    }

                    logger.LogInformation("Queued {Count} players for sync", ids.Count);
                    await statusReporter.ReportAsync("Tracker", "Roster synced", $"Queued {ids.Count} players for sync.", stoppingToken, new { QueuedPlayers = ids.Count });
                    nextRosterSyncUtc = DateTimeOffset.UtcNow.AddMinutes(1);
                }

                if (queue.TryDequeue(out var playerId))
                {
                    var username = await db.Players
                        .Where(x => x.Id == playerId)
                        .Select(x => x.Username)
                        .FirstOrDefaultAsync(stoppingToken);
                    currentPlayerId = playerId;
                    currentUsername = username;
                    await statusReporter.ReportAsync(
                        "Tracker",
                        "Processing player",
                        string.IsNullOrWhiteSpace(username) ? $"Syncing player #{playerId}." : $"Syncing {username}.",
                        stoppingToken,
                        new { PlayerId = playerId, Username = username });
                    await sync.ProcessPlayerAsync(playerId, stoppingToken);
                    await Task.Delay(playerDelay, stoppingToken);
                    continue;
                }

                var wait = nextRosterSyncUtc - DateTimeOffset.UtcNow;
                await statusReporter.ReportAsync("Tracker", "Waiting", "Waiting for the next roster sync or queued player.", stoppingToken, new { NextRosterSyncUtc = nextRosterSyncUtc });
                await queue.WaitForItemAsync(wait, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                if (currentPlayerId.HasValue)
                {
                    await statusReporter.ReportAsync(
                        "Tracker",
                        "Error",
                        string.IsNullOrWhiteSpace(currentUsername)
                            ? $"Tracker cycle failed while syncing player #{currentPlayerId.Value}: {ex.Message}"
                            : $"Tracker cycle failed while syncing {currentUsername}: {ex.Message}",
                        stoppingToken,
                        new
                        {
                            Error = ex.GetType().Name,
                            PlayerId = currentPlayerId.Value,
                            Username = currentUsername
                        });
                    logger.LogError(
                        ex,
                        "Worker cycle failed while syncing player {PlayerId} ({Username}).",
                        currentPlayerId.Value,
                        currentUsername);
                }
                else
                {
                    await statusReporter.ReportAsync("Tracker", "Error", $"Tracker cycle failed: {ex.Message}", stoppingToken, new { Error = ex.GetType().Name });
                    logger.LogError(ex, "Worker cycle failed.");
                }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
