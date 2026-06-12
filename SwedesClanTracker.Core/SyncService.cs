using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SwedesClanTracker.Core;

public interface ITrackerSyncService
{
    Task<IReadOnlyList<int>> SyncRosterAndQueueAsync(CancellationToken ct);
    Task ProcessPlayerAsync(int playerId, CancellationToken ct);
}

public class TrackerSyncService(TrackerDbContext db, ITempleClient templeClient, IWiseOldManClient wiseOldManClient) : ITrackerSyncService
{
    public async Task<IReadOnlyList<int>> SyncRosterAndQueueAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var roster = await templeClient.GetRosterAsync(ct);
        var rosterSet = roster
            .Select(NormalizeUsername)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var womRoles = await wiseOldManClient.GetMemberRolesAsync(ct);
        var existing = await db.Players
            .OrderBy(x => x.LastSynced == null ? 0 : 1)
            .ThenBy(x => x.LastSynced)
            .ThenBy(x => x.ManualPetOverride == null && x.StoredPetCount == 0 ? 0 : 1)
            .ThenBy(x => x.Username)
            .ToListAsync(ct);
        var trackedDbUsernames = existing
            .Select(x => NormalizeUsername(x.Username))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queue = new List<int>();

        foreach (var player in existing)
        {
            await CloseReviewLifecycleEventsResolvedByStatusAsync(player, ct);

            womRoles.TryGetValue(NormalizeUsername(player.Username), out var womRole);
            var missingInWom = string.IsNullOrWhiteSpace(womRole);

            if (rosterSet.Contains(NormalizeUsername(player.Username)))
            {
                player.LastSeen = now;
                if (missingInWom && player.Status != PlayerStatus.REMOVED_CONFIRMED)
                {
                    player.Status = PlayerStatus.MISSING_PENDING_REVIEW;
                    await CloseOpenLifecycleEventsAsync(player.Id, ct, "NEW_PLAYER", "MERGE_SUGGESTED", "MERGE_ACTION_REQUIRED");
                    var hasWomPendingAction = await db.LifecycleEvents.AnyAsync(x =>
                        x.PlayerId == player.Id &&
                        x.EventType == "WOM_MISSING_ACTION_REQUIRED" &&
                        x.Status == "OPEN", ct);
                    if (!hasWomPendingAction)
                    {
                        db.LifecycleEvents.Add(new LifecycleEvent
                        {
                            PlayerId = player.Id,
                            EventType = "WOM_MISSING_ACTION_REQUIRED",
                            MetadataJson = JsonUtil.Serialize(new { player.Username, MissingAt = now }),
                            Status = "OPEN",
                            CreatedAt = now
                        });
                    }
                }
                else if (player.Status == PlayerStatus.MISSING_PENDING_REVIEW)
                {
                    player.Status = PlayerStatus.ACTIVE;
                    await CloseOpenLifecycleEventsAsync(player.Id, ct,
                        "NEW_PLAYER",
                        "MERGE_SUGGESTED",
                        "MERGE_ACTION_REQUIRED",
                        "MISSING_IN_ROSTER",
                        "TEMPLE_MISSING_ACTION_REQUIRED",
                        "WOM_MISSING_ACTION_REQUIRED");
                }
                if (!missingInWom)
                {
                    await EnsureWomRankMismatchLifecycleAsync(player, womRole!, now, ct);
                }
                queue.Add(player.Id);
            }
            else if (player.Status != PlayerStatus.REMOVED_CONFIRMED)
            {
                player.Status = PlayerStatus.MISSING_PENDING_REVIEW;
                await CloseOpenLifecycleEventsAsync(player.Id, ct, "NEW_PLAYER", "MERGE_SUGGESTED", "MERGE_ACTION_REQUIRED");
                var hasOpenMissingEvent = await db.LifecycleEvents.AnyAsync(x =>
                    x.PlayerId == player.Id &&
                    x.EventType == "MISSING_IN_ROSTER" &&
                    x.Status == "OPEN", ct);
                if (!hasOpenMissingEvent)
                {
                    db.LifecycleEvents.Add(new LifecycleEvent
                    {
                        PlayerId = player.Id,
                        EventType = "MISSING_IN_ROSTER",
                        MetadataJson = JsonUtil.Serialize(new { player.Username, MissingAt = now }),
                        Status = "OPEN",
                        CreatedAt = now
                    });
                }
                var hasPendingAction = await db.LifecycleEvents.AnyAsync(x =>
                    x.PlayerId == player.Id &&
                    x.EventType == "TEMPLE_MISSING_ACTION_REQUIRED" &&
                    x.Status == "OPEN", ct);
                if (!hasPendingAction)
                {
                    db.LifecycleEvents.Add(new LifecycleEvent
                    {
                        PlayerId = player.Id,
                        EventType = "TEMPLE_MISSING_ACTION_REQUIRED",
                        MetadataJson = JsonUtil.Serialize(new { player.Username, MissingAt = now }),
                        Status = "OPEN",
                        CreatedAt = now
                    });
                }
            }
        }

        foreach (var username in rosterSet.Where(trackedDbUsernames.Add).OrderBy(x => x))
        {
            var p = new Player
            {
                Username = username,
                LastSeen = now,
                Status = PlayerStatus.NEW_PENDING_REVIEW
            };
            db.Players.Add(p);
            await db.SaveChangesAsync(ct);
            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = p.Id,
                EventType = "NEW_PLAYER",
                MetadataJson = JsonUtil.Serialize(new { p.Username, CreatedAt = now }),
                Status = "OPEN",
                CreatedAt = now
            });
            queue.Add(p.Id);
        }

        await EnsureWomOnlyActionRequiredLifecycleAsync(
            now,
            rosterSet,
            trackedDbUsernames,
            womRoles,
            ct);

        await db.SaveChangesAsync(ct);
        return queue;
    }

    private async Task EnsureWomOnlyActionRequiredLifecycleAsync(
        DateTimeOffset now,
        HashSet<string> rosterUsernames,
        HashSet<string> dbUsernames,
        IReadOnlyDictionary<string, string> womRoles,
        CancellationToken ct)
    {
        var anchorPlayerId = await db.Players
            .OrderBy(x => x.Id)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(ct);
        if (!anchorPlayerId.HasValue) return;

        var openIgnored = await db.LifecycleEvents
            .Where(x => x.EventType == "WOM_ONLY_IGNORED" && x.Status == "OPEN")
            .ToListAsync(ct);
        var ignoredByUsername = openIgnored
            .Select(x => (Event: x, Username: ReadLifecycleUsername(x.MetadataJson)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Username))
            .GroupBy(x => NormalizeUsername(x.Username!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(x => x.Event.CreatedAt).Select(x => x.Event).ToList(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var ignoredGroup in ignoredByUsername.Values)
        {
            ignoredGroup[0].PlayerId = anchorPlayerId.Value;
            foreach (var duplicateIgnored in ignoredGroup.Skip(1))
            {
                duplicateIgnored.Status = "DONE";
            }
        }

        var ignoredSet = ignoredByUsername.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mergeSuppressedUsernames = await GetMergePendingPreviousUsernamesAsync(ct);

        var openRequired = await db.LifecycleEvents
            .Where(x => x.EventType == "WOM_ONLY_ACTION_REQUIRED" && x.Status == "OPEN")
            .ToListAsync(ct);
        var requiredByUsername = openRequired
            .Select(x => (Event: x, Username: ReadLifecycleUsername(x.MetadataJson)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Username))
            .GroupBy(x => NormalizeUsername(x.Username!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(x => x.Event.CreatedAt).Select(x => x.Event).ToList(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var requiredGroup in requiredByUsername.Values)
        {
            foreach (var duplicateRequired in requiredGroup.Skip(1))
            {
                duplicateRequired.Status = "DONE";
            }
        }

        foreach (var openEventWithoutUsername in openRequired.Where(x => string.IsNullOrWhiteSpace(ReadLifecycleUsername(x.MetadataJson))))
        {
            openEventWithoutUsername.Status = "DONE";
        }

        foreach (var (womUsername, womRoleRaw) in womRoles)
        {
            var normalizedUsername = NormalizeUsername(womUsername);
            var womRole = womRoleRaw?.Trim() ?? "";
            if (!ShouldRequireWomOnlyAction(normalizedUsername, womRole, rosterUsernames, dbUsernames, ignoredSet))
            {
                continue;
            }
            if (mergeSuppressedUsernames.Contains(normalizedUsername))
            {
                continue;
            }

            if (requiredByUsername.TryGetValue(normalizedUsername, out var existingRequired) && existingRequired.Count > 0)
            {
                var requiredEvent = existingRequired[0];
                requiredEvent.PlayerId = anchorPlayerId.Value;
                requiredEvent.MetadataJson = JsonUtil.Serialize(new
                {
                    Username = normalizedUsername,
                    ActualWomRole = womRole,
                    Source = "roster-sync",
                    DetectedAt = now
                });
                continue;
            }

            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = anchorPlayerId.Value,
                EventType = "WOM_ONLY_ACTION_REQUIRED",
                MetadataJson = JsonUtil.Serialize(new
                {
                    Username = normalizedUsername,
                    ActualWomRole = womRole,
                    Source = "roster-sync",
                    DetectedAt = now
                }),
                Status = "OPEN",
                CreatedAt = now
            });
        }

        foreach (var (username, requiredEvents) in requiredByUsername)
        {
            if (requiredEvents.Count == 0) continue;
            if (!womRoles.TryGetValue(username, out var womRoleRaw))
            {
                requiredEvents[0].Status = "DONE";
                continue;
            }

            var womRole = womRoleRaw?.Trim() ?? "";
            if (!ShouldRequireWomOnlyAction(username, womRole, rosterUsernames, dbUsernames, ignoredSet))
            {
                requiredEvents[0].Status = "DONE";
                continue;
            }
            if (mergeSuppressedUsernames.Contains(username))
            {
                requiredEvents[0].Status = "DONE";
                continue;
            }

            requiredEvents[0].PlayerId = anchorPlayerId.Value;
            requiredEvents[0].MetadataJson = JsonUtil.Serialize(new
            {
                Username = username,
                ActualWomRole = womRole,
                Source = "roster-sync",
                DetectedAt = now
            });
        }
    }

    private static bool ShouldRequireWomOnlyAction(
        string normalizedUsername,
        string womRole,
        HashSet<string> rosterUsernames,
        HashSet<string> dbUsernames,
        HashSet<string> ignoredUsernames)
    {
        if (string.IsNullOrWhiteSpace(normalizedUsername)) return false;
        if (string.IsNullOrWhiteSpace(womRole)) return false;
        if (dbUsernames.Contains(normalizedUsername)) return false;
        if (rosterUsernames.Contains(normalizedUsername)) return false;
        if (ignoredUsernames.Contains(normalizedUsername)) return false;
        return !IsWomOnlyIgnoredRole(womRole);
    }

    private static string? ReadLifecycleUsername(string metadataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("Username", out var usernameProperty)) return null;
            if (usernameProperty.ValueKind != JsonValueKind.String) return null;
            return NormalizeUsername(usernameProperty.GetString() ?? "");
        }
        catch
        {
            return null;
        }
    }

    private async Task<HashSet<string>> GetMergePendingPreviousUsernamesAsync(CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var openMergeEvents = await db.LifecycleEvents
            .Where(x => x.EventType == "MERGE_ACTION_REQUIRED" && x.Status == "OPEN")
            .ToListAsync(ct);
        foreach (var ev in openMergeEvents)
        {
            var previous = ReadSuggestedPrevious(ev.MetadataJson);
            if (!string.IsNullOrWhiteSpace(previous))
            {
                result.Add(NormalizeUsername(previous));
                continue;
            }

            var fallback = await db.LifecycleEvents
                .Where(x => x.PlayerId == ev.PlayerId && x.EventType == "MERGE_DISCORD_POSTED")
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => x.MetadataJson)
                .FirstOrDefaultAsync(ct);
            var fallbackPrevious = ReadSuggestedPrevious(fallback ?? "");
            if (!string.IsNullOrWhiteSpace(fallbackPrevious))
            {
                result.Add(NormalizeUsername(fallbackPrevious));
            }
        }
        return result;
    }

    private static string? ReadSuggestedPrevious(string metadataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("SuggestedPrevious", out var property)) return null;
            if (property.ValueKind != JsonValueKind.String) return null;
            return property.GetString();
        }
        catch
        {
            return null;
        }
    }

    private async Task EnsureWomRankMismatchLifecycleAsync(Player player, string womRole, DateTimeOffset now, CancellationToken ct)
    {
        if (player.Status != PlayerStatus.ACTIVE) return;
        if (string.Equals(player.CurrentRank, "Recruit", StringComparison.OrdinalIgnoreCase) ||
            IsSpecialWomRole(womRole) ||
            string.Equals(NormalizeRankName(player.CurrentRank), NormalizeRankName(womRole), StringComparison.OrdinalIgnoreCase))
        {
            await CloseOpenLifecycleEventsAsync(player.Id, ct, "WOM_RANK_MISMATCH_REQUIRED");
            return;
        }

        var isIgnored = await db.LifecycleEvents.AnyAsync(x =>
            x.PlayerId == player.Id &&
            x.EventType == "WOM_RANK_MISMATCH_IGNORED" &&
            x.Status == "OPEN", ct);
        if (isIgnored)
        {
            await CloseOpenLifecycleEventsAsync(player.Id, ct, "WOM_RANK_MISMATCH_REQUIRED");
            return;
        }

        var direction = GetWomRankMismatchDirection(player.CurrentRank, womRole);
        var openMismatches = await db.LifecycleEvents
            .Where(x =>
            x.PlayerId == player.Id &&
            x.EventType == "WOM_RANK_MISMATCH_REQUIRED" &&
            x.Status == "OPEN")
            .ToListAsync(ct);
        if (openMismatches.Count > 0)
        {
            foreach (var ev in openMismatches)
            {
                ev.MetadataJson = JsonUtil.Serialize(new
                {
                    player.Username,
                    ExpectedRank = player.CurrentRank,
                    ActualWomRole = womRole,
                    Direction = direction,
                    Source = "roster-sync",
                    DetectedAt = now
                });
            }
            return;
        }

        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = player.Id,
            EventType = "WOM_RANK_MISMATCH_REQUIRED",
            MetadataJson = JsonUtil.Serialize(new
            {
                player.Username,
                ExpectedRank = player.CurrentRank,
                ActualWomRole = womRole,
                Direction = direction,
                Source = "roster-sync",
                DetectedAt = now
            }),
            Status = "OPEN",
            CreatedAt = now
        });
    }

    public async Task ProcessPlayerAsync(int playerId, CancellationToken ct)
    {
        var player = await db.Players.FirstOrDefaultAsync(x => x.Id == playerId, ct);
        if (player is null) return;
        var stats = await templeClient.GetPlayerStatsAsync(player.Username, ct);
        if (stats is null) return;
        var apiPets = await templeClient.GetPetsAsync(player.Username, ct);

        var candidateStoredPetCount = player.StoredPetCount;
        if (apiPets.HasValue && apiPets.Value > candidateStoredPetCount)
        {
            candidateStoredPetCount = apiPets.Value;
        }

        var updatedPets = candidateStoredPetCount;
        int? updatedManualPetOverride = player.ManualPetOverride;
        if (player.ManualPetOverride.HasValue)
        {
            if (candidateStoredPetCount >= player.ManualPetOverride.Value)
            {
                updatedManualPetOverride = null;
            }
            else
            {
                updatedPets = player.ManualPetOverride.Value;
            }
        }

        var syncedAt = DateTimeOffset.UtcNow;
        var snapshot = new PlayerSnapshot
        {
            PlayerId = player.Id,
            Timestamp = syncedAt,
            TotalLevel = stats.TotalLevel,
            Ehb = stats.Ehb,
            Ehp = stats.Ehp,
            Collections = stats.Collections,
            PetCount = updatedPets
        };
        var latestSnapshot = await db.PlayerSnapshots
            .Where(x => x.PlayerId == player.Id)
            .OrderByDescending(x => x.Timestamp)
            .FirstOrDefaultAsync(ct);
        var contamination = PlayerSnapshotContaminationGuard.Evaluate(latestSnapshot, snapshot);
        if (contamination.IsContaminated)
        {
            await UpsertOpenLifecycleEventAsync(player.Id, PlayerSnapshotContaminationGuard.EventType, new
            {
                player.Username,
                LatestSnapshotId = latestSnapshot!.Id,
                LatestSnapshotAt = latestSnapshot.Timestamp,
                PreviousTotalLevel = latestSnapshot.TotalLevel,
                NewTotalLevel = snapshot.TotalLevel,
                PreviousEhb = latestSnapshot.Ehb,
                NewEhb = snapshot.Ehb,
                PreviousEhp = latestSnapshot.Ehp,
                NewEhp = snapshot.Ehp,
                contamination.TotalLevelDrop,
                contamination.EhbDrop,
                contamination.EhpDrop,
                contamination.Reasons,
                Source = "sync-snapshot-guard",
                DetectedAt = syncedAt
            }, ct);
            return;
        }

        player.StoredPetCount = candidateStoredPetCount;
        player.ManualPetOverride = updatedManualPetOverride;
        await CloseOpenLifecycleEventsAsync(player.Id, ct, PlayerSnapshotContaminationGuard.EventType);
        if (latestSnapshot is null || !snapshot.HasSameStats(latestSnapshot))
        {
            db.PlayerSnapshots.Add(snapshot);
        }
        var rankResult = RankEvaluator.Evaluate(snapshot);
        player.EligibleRank = rankResult.Rank;
        player.LastSynced = syncedAt;
        await DismissSatisfiedPendingPromotionCandidatesAsync(player, syncedAt, ct);
        if (player.Status is PlayerStatus.NEW_PENDING_REVIEW)
        {
            var missingCandidates = await db.Players
                .Where(x => x.Status == PlayerStatus.MISSING_PENDING_REVIEW && x.Id != player.Id)
                .Select(x => new
                {
                    x.Id,
                    x.Username,
                    Last = x.Snapshots.OrderByDescending(s => s.Timestamp).FirstOrDefault()
                })
                .ToListAsync(ct);
            var rankedCandidates = missingCandidates
                .Where(x => x.Last is not null)
                .Select(x => new
                {
                    x.Username,
                    TotalLevelDelta = Math.Abs(x.Last!.TotalLevel - snapshot.TotalLevel),
                    EhbDelta = Math.Abs(x.Last!.Ehb - snapshot.Ehb),
                    EhpDelta = Math.Abs(x.Last!.Ehp - snapshot.Ehp)
                })
                .Where(x => x.TotalLevelDelta <= 5 && x.EhbDelta <= 10 && x.EhpDelta <= 10)
                .OrderBy(x => x.TotalLevelDelta + x.EhbDelta + x.EhpDelta)
                .ThenBy(x => x.Username)
                .ToList();
            var match = rankedCandidates.FirstOrDefault();
            if (match is not null)
            {
                player.Status = PlayerStatus.MERGE_SUGGESTED;
                await CloseOpenLifecycleEventsAsync(player.Id, ct, "NEW_PLAYER");
                var mergeMetadata = new
                {
                    NewPlayer = player.Username,
                    SuggestedPrevious = match.Username,
                    CandidatePreviousPlayers = rankedCandidates.Take(5).Select(x => new
                    {
                        PreviousPlayer = x.Username,
                        x.TotalLevelDelta,
                        x.EhbDelta,
                        x.EhpDelta
                    }),
                    Source = "sync-auto-rename",
                    DetectedAt = DateTimeOffset.UtcNow
                };
                await UpsertOpenLifecycleEventAsync(player.Id, "MERGE_SUGGESTED", mergeMetadata, ct);
                await UpsertOpenLifecycleEventAsync(player.Id, "MERGE_ACTION_REQUIRED", mergeMetadata, ct);
            }
            else
            {
                player.Status = PlayerStatus.ACTIVE;
                await CloseOpenLifecycleEventsAsync(player.Id, ct, "NEW_PLAYER", "MERGE_SUGGESTED", "MERGE_ACTION_REQUIRED");
            }
        }

        var isImpAccount = await wiseOldManClient.IsImpAccountAsync(player.Username, ct);
        if (isImpAccount)
        {
            var pendingForImp = await db.PromotionCandidates
                .Where(x => x.PlayerId == player.Id && x.Status == PromotionStatus.PENDING)
                .ToListAsync(ct);
            if (pendingForImp.Count > 0)
            {
                db.PromotionCandidates.RemoveRange(pendingForImp);
            }
        }
        else if (RankOrder(player.EligibleRank) > RankOrder(player.CurrentRank))
        {
            var exists = await db.PromotionCandidates.AnyAsync(x =>
                x.PlayerId == player.Id && x.Status == PromotionStatus.PENDING && x.NewRank == player.EligibleRank, ct);
            if (!exists)
            {
                db.PromotionCandidates.Add(new PromotionCandidate
                {
                    PlayerId = player.Id,
                    OldRank = player.CurrentRank,
                    NewRank = player.EligibleRank,
                    Reason = rankResult.Explanation,
                    Status = PromotionStatus.PENDING,
                    CreatedAt = DateTimeOffset.UtcNow
                });
                db.LifecycleEvents.Add(new LifecycleEvent
                {
                    PlayerId = player.Id,
                    EventType = "PROMOTION_CANDIDATE_CREATED",
                    MetadataJson = JsonUtil.Serialize(new
                    {
                        player.Username,
                        OldRank = player.CurrentRank,
                        NewRank = player.EligibleRank,
                        Reason = rankResult.Explanation
                    }),
                    Status = "DONE",
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task DismissSatisfiedPendingPromotionCandidatesAsync(Player player, DateTimeOffset now, CancellationToken ct)
    {
        var satisfiedCandidates = await db.PromotionCandidates
            .Where(x =>
                x.PlayerId == player.Id &&
                x.Status == PromotionStatus.PENDING)
            .ToListAsync(ct);
        satisfiedCandidates = satisfiedCandidates
            .Where(x => RankOrder(x.NewRank) <= RankOrder(player.CurrentRank))
            .ToList();
        if (satisfiedCandidates.Count == 0) return;

        foreach (var candidate in satisfiedCandidates)
        {
            candidate.Status = PromotionStatus.DISMISSED;
            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = player.Id,
                EventType = "PROMOTION_CANDIDATE_ALREADY_CURRENT_RANK",
                MetadataJson = JsonUtil.Serialize(new
                {
                    CandidateId = candidate.Id,
                    player.Username,
                    CurrentRank = player.CurrentRank,
                    CandidateNewRank = candidate.NewRank,
                    Source = "player-sync"
                }),
                Status = "DONE",
                CreatedAt = now
            });
        }
    }

    private async Task CloseReviewLifecycleEventsResolvedByStatusAsync(Player player, CancellationToken ct)
    {
        var resolvedEventTypes = new List<string>();
        if (player.Status != PlayerStatus.NEW_PENDING_REVIEW)
        {
            resolvedEventTypes.Add("NEW_PLAYER");
        }
        if (player.Status != PlayerStatus.MERGE_SUGGESTED)
        {
            resolvedEventTypes.Add("MERGE_SUGGESTED");
            resolvedEventTypes.Add("MERGE_ACTION_REQUIRED");
        }
        else
        {
            await EnsureOpenMergeSuggestedEventAsync(player, ct);
        }
        resolvedEventTypes.Add("DISCORD_MARK_RENAME_SUSPECT");
        if (player.Status != PlayerStatus.MISSING_PENDING_REVIEW)
        {
            resolvedEventTypes.AddRange([
                "MISSING_IN_ROSTER",
                "TEMPLE_MISSING_ACTION_REQUIRED",
                "WOM_MISSING_ACTION_REQUIRED"
            ]);
        }
        if (player.Status == PlayerStatus.REMOVED_CONFIRMED)
        {
            resolvedEventTypes.AddRange([
                "WOM_RANK_MISMATCH_REQUIRED",
                "WOM_RANK_MISMATCH_IGNORED"
            ]);
        }

        if (resolvedEventTypes.Count > 0)
        {
            await CloseOpenLifecycleEventsAsync(player.Id, ct, resolvedEventTypes.Distinct().ToArray());
        }
    }

    private async Task EnsureOpenMergeSuggestedEventAsync(Player player, CancellationToken ct)
    {
        var hasOpenMergeSuggested = await db.LifecycleEvents.AnyAsync(x =>
            x.PlayerId == player.Id &&
            x.EventType == "MERGE_SUGGESTED" &&
            x.Status == "OPEN", ct);
        var hasOpenMergeRequired = await db.LifecycleEvents.AnyAsync(x =>
            x.PlayerId == player.Id &&
            x.EventType == "MERGE_ACTION_REQUIRED" &&
            x.Status == "OPEN", ct);

        // Never overwrite existing open metadata here. Only ensure missing companion event exists.
        if (hasOpenMergeSuggested && hasOpenMergeRequired)
        {
            return;
        }

        if (hasOpenMergeSuggested && !hasOpenMergeRequired)
        {
            var sourceMetadata = await db.LifecycleEvents
                .Where(x => x.PlayerId == player.Id && x.EventType == "MERGE_SUGGESTED" && x.Status == "OPEN")
                .OrderByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.Id)
                .Select(x => x.MetadataJson)
                .FirstAsync(ct);
            await UpsertOpenLifecycleEventFromJsonAsync(player.Id, "MERGE_ACTION_REQUIRED", sourceMetadata, ct);
            return;
        }

        if (!hasOpenMergeSuggested && hasOpenMergeRequired)
        {
            var sourceMetadata = await db.LifecycleEvents
                .Where(x => x.PlayerId == player.Id && x.EventType == "MERGE_ACTION_REQUIRED" && x.Status == "OPEN")
                .OrderByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.Id)
                .Select(x => x.MetadataJson)
                .FirstAsync(ct);
            await UpsertOpenLifecycleEventFromJsonAsync(player.Id, "MERGE_SUGGESTED", sourceMetadata, ct);
            return;
        }

        var metadata = new { NewPlayer = player.Username, Source = "status-normalizer", DetectedAt = DateTimeOffset.UtcNow };
        await UpsertOpenLifecycleEventAsync(player.Id, "MERGE_SUGGESTED", metadata, ct);
        await UpsertOpenLifecycleEventAsync(player.Id, "MERGE_ACTION_REQUIRED", metadata, ct);
    }

    private async Task UpsertOpenLifecycleEventAsync(int playerId, string eventType, object metadata, CancellationToken ct)
    {
        var openEvents = await db.LifecycleEvents
            .Where(x => x.PlayerId == playerId && x.EventType == eventType && x.Status == "OPEN")
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);
        if (openEvents.Count > 0)
        {
            if (!openEvents[0].MetadataJson.Contains("\"SuggestedPrevious\":"))
            {
                openEvents[0].MetadataJson = JsonUtil.Serialize(metadata);
            }
            foreach (var duplicate in openEvents.Skip(1))
            {
                duplicate.Status = "DONE";
            }
            return;
        }

        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = playerId,
            EventType = eventType,
            MetadataJson = JsonUtil.Serialize(metadata),
            Status = "OPEN",
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private async Task UpsertOpenLifecycleEventFromJsonAsync(int playerId, string eventType, string metadataJson, CancellationToken ct)
    {
        var openEvents = await db.LifecycleEvents
            .Where(x => x.PlayerId == playerId && x.EventType == eventType && x.Status == "OPEN")
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);
        if (openEvents.Count > 0)
        {
            if (!openEvents[0].MetadataJson.Contains("\"SuggestedPrevious\":"))
            {
                openEvents[0].MetadataJson = metadataJson;
            }
            foreach (var duplicate in openEvents.Skip(1))
            {
                duplicate.Status = "DONE";
            }
            return;
        }

        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = playerId,
            EventType = eventType,
            MetadataJson = metadataJson,
            Status = "OPEN",
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private async Task CloseOpenLifecycleEventsAsync(int playerId, CancellationToken ct, params string[] eventTypes)
    {
        var events = await db.LifecycleEvents
            .Where(x => x.PlayerId == playerId && x.Status == "OPEN" && eventTypes.Contains(x.EventType))
            .ToListAsync(ct);
        foreach (var ev in events)
        {
            ev.Status = "DONE";
        }
    }

    private static int RankOrder(string rank)
    {
        var normalized = NormalizeRankName(rank);
        string[] order = ["Recruit", "Officer", "Commander", "Lieutenant", "Captain", "Astral", "General", "Brigadier", "Admiral", "Marshal", "Beast"];
        for (var i = 0; i < order.Length; i++)
        {
            if (string.Equals(order[i], normalized, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return 0;
    }

    private static string GetWomRankMismatchDirection(string expectedRank, string actualWomRole)
    {
        var expected = RankOrder(expectedRank);
        var actual = RankOrder(actualWomRole);
        if (actual > expected) return "higher";
        if (actual < expected) return "lower";
        return "different";
    }

    private static bool IsSpecialWomRole(string role)
    {
        string[] specialRoles = ["imp", "Kitten", "Administrator", "Deputy Owner", "Owner", "short green guy", "member", "recruit", "apothecary"];
        var normalized = NormalizeRankName(role);
        return specialRoles.Any(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWomOnlyIgnoredRole(string role)
    {
        string[] ignoredRoles = ["imp", "kitten", "administrator", "deputy owner"];
        var normalized = NormalizeRankName(role);
        return ignoredRoles.Any(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeRankName(string rank) => RankRules.NormalizeRankName(rank);
    private static string NormalizeUsername(string input) => UsernameRules.NormalizeUsername(input);
}
