using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Security.Claims;
using System.Text;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/review")]
public class ReviewController(TrackerDbContext db, IConfiguration config, IHttpClientFactory httpClientFactory, IMergeReviewService mergeReviewService) : ControllerBase
{
    [HttpGet("queue")]
    public async Task<IActionResult> Queue(CancellationToken ct)
    {
        var players = await db.Players
            .Where(x => x.Status == PlayerStatus.NEW_PENDING_REVIEW || x.Status == PlayerStatus.MISSING_PENDING_REVIEW || x.Status == PlayerStatus.MERGE_SUGGESTED)
            .OrderBy(x => x.Username)
            .ToListAsync(ct);

        var playerIds = players.Select(x => x.Id).ToList();
        var lifecycle = playerIds.Count == 0
            ? []
            : await db.LifecycleEvents
                .Where(x => playerIds.Contains(x.PlayerId))
                .OrderByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.Id)
                .ToListAsync(ct);

        var rows = players.Select(player =>
        {
            string cardState = "Handled";
            if (player.Status is PlayerStatus.NEW_PENDING_REVIEW or PlayerStatus.MISSING_PENDING_REVIEW or PlayerStatus.MERGE_SUGGESTED)
            {
                var eventsForPlayer = lifecycle.Where(x => x.PlayerId == player.Id).ToList();
                var hasOpenRequirement =
                    eventsForPlayer.Any(x => x.Status == "OPEN" &&
                        (x.EventType == "TEMPLE_MISSING_ACTION_REQUIRED" ||
                         x.EventType == "WOM_MISSING_ACTION_REQUIRED" ||
                         x.EventType == "WOM_ONLY_ACTION_REQUIRED" ||
                         x.EventType == "MERGE_ACTION_REQUIRED"));
                var latestPosted = eventsForPlayer.FirstOrDefault(x =>
                    x.EventType == "TEMPLE_MISSING_DISCORD_POSTED" ||
                    x.EventType == "WOM_MISSING_DISCORD_POSTED" ||
                    x.EventType == "WOM_ONLY_DISCORD_POSTED" ||
                    x.EventType == "MERGE_DISCORD_POSTED");
                var latestMissingSignal = eventsForPlayer.FirstOrDefault(x => x.EventType == "DISCORD_POSTED_MESSAGE_MISSING");

                cardState = hasOpenRequirement
                    ? latestPosted is null
                        ? "Card missing (reposting)"
                        : latestMissingSignal is not null && latestMissingSignal.CreatedAt > latestPosted.CreatedAt
                            ? "Card missing (reposting)"
                            : "Card live"
                    : "Handled";
            }

            var mergeMeta = lifecycle
                .Where(x => x.PlayerId == player.Id && x.EventType == "MERGE_SUGGESTED")
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => x.MetadataJson)
                .FirstOrDefault();
            return new ReviewQueueRowDto(
                player.Id,
                player.Username,
                player.Status.ToString(),
                player.CurrentRank,
                player.EligibleRank,
                player.LastSeen,
                cardState,
                mergeMeta);
        });

        return Ok(rows);
    }

    [HttpPost("players/{id:int}/status")]
    public async Task<IActionResult> SetStatus(int id, [FromBody] SetStatusRequest req, CancellationToken ct)
    {
        var p = await db.Players.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        var handledBy = User.FindFirstValue(ClaimTypes.Name) ?? "web-admin";
        p.Status = req.Status;
        await CloseReviewLifecycleEventsResolvedByStatusAsync(p, handledBy, ct);
        if (req.Status == PlayerStatus.MERGE_SUGGESTED)
        {
            var candidate = await db.PromotionCandidates
                .Where(x => x.PlayerId == p.Id && x.Status == PromotionStatus.PENDING)
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (candidate is not null)
            {
                QueueDiscordMessageUpdate(p.Id, candidate.Id, "rename");
            }
        }
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = p.Id,
            EventType = "STATUS_UPDATED",
            MetadataJson = JsonUtil.Serialize(new { req.Status, HandledBy = handledBy, Source = "web" }),
            Status = "DONE",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
        return Ok();
    }

    [HttpPost("players/{id:int}/temple-missing/add")]
    public async Task<IActionResult> AddBackToTemple(int id, CancellationToken ct)
    {
        var p = await db.Players.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        var templeGroupId = config.GetValue<int?>("TempleOsrs:GroupId") ?? 449;
        var templeApiKey = config["TempleOsrs:ApiKey"] ?? "";
        var womGroupId = config.GetValue<int?>("WiseOldMan:GroupId") ?? 0;
        var womVerificationCode = config["WiseOldMan:VerificationCode"] ?? "";
        if (string.IsNullOrWhiteSpace(templeApiKey)) return BadRequest("Temple API key missing.");

        var client = httpClientFactory.CreateClient();
        var templeBody = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = templeGroupId.ToString(),
            ["key"] = templeApiKey,
            ["players"] = p.Username
        });
        var templeResp = await client.PostAsync("https://templeosrs.com/api/add_group_member.php", templeBody, ct);
        if (!templeResp.IsSuccessStatusCode) return BadRequest($"Temple add failed ({(int)templeResp.StatusCode}).");

        if (womGroupId > 0 && !string.IsNullOrWhiteSpace(womVerificationCode))
        {
            var womBody = JsonSerializer.Serialize(new
            {
                verificationCode = womVerificationCode,
                members = new[] { new { username = p.Username, role = "member" } }
            });
            var womReq = new HttpRequestMessage(HttpMethod.Post, $"https://api.wiseoldman.net/v2/groups/{womGroupId}/members")
            {
                Content = new StringContent(womBody, Encoding.UTF8, "application/json")
            };
            var womResp = await client.SendAsync(womReq, ct);
            if (!womResp.IsSuccessStatusCode)
            {
                var alreadyInWom = await IsPlayerInWiseOldManGroupAsync(client, p.Username, womGroupId, ct);
                if (!alreadyInWom) return BadRequest($"WiseOldMan add failed ({(int)womResp.StatusCode}).");
            }
        }

        p.Status = PlayerStatus.ACTIVE;
        await CloseReviewLifecycleEventsResolvedByStatusAsync(p, User.FindFirstValue(ClaimTypes.Name) ?? "web-admin", ct);
        QueueTempleMissingDiscordUpdate(p.Id, p.Username, "add");
        await db.SaveChangesAsync(ct);
        return Ok(new { player = p.Username, action = "added_to_temple_and_wom" });
    }

    [HttpPost("players/{id:int}/temple-missing/remove-db")]
    public async Task<IActionResult> RemoveFromDb(int id, CancellationToken ct)
    {
        var p = await db.Players.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        var womGroupId = config.GetValue<int?>("WiseOldMan:GroupId") ?? 0;
        var womVerificationCode = config["WiseOldMan:VerificationCode"] ?? "";
        if (womGroupId > 0 && !string.IsNullOrWhiteSpace(womVerificationCode))
        {
            var client = httpClientFactory.CreateClient();
            var womBody = JsonSerializer.Serialize(new
            {
                verificationCode = womVerificationCode,
                members = new[] { p.Username }
            });
            var womReq = new HttpRequestMessage(HttpMethod.Delete, $"https://api.wiseoldman.net/v2/groups/{womGroupId}/members")
            {
                Content = new StringContent(womBody, Encoding.UTF8, "application/json")
            };
            var womResp = await client.SendAsync(womReq, ct);
            if (!womResp.IsSuccessStatusCode)
            {
                var stillInWom = await IsPlayerInWiseOldManGroupAsync(client, p.Username, womGroupId, ct);
                if (stillInWom) return BadRequest($"WiseOldMan remove failed ({(int)womResp.StatusCode}).");
            }
        }

        QueueTempleMissingDiscordUpdate(p.Id, p.Username, "remove");
        var replacementPlayerId = await db.Players
            .Where(x => x.Id != p.Id)
            .OrderBy(x => x.Id)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(ct);
        var statusRows = await db.LifecycleEvents
            .Where(x => x.PlayerId == p.Id && x.EventType == AppStatusConstants.EventType)
            .ToListAsync(ct);
        if (replacementPlayerId.HasValue)
        {
            foreach (var status in statusRows) status.PlayerId = replacementPlayerId.Value;
        }
        else
        {
            db.LifecycleEvents.RemoveRange(statusRows);
        }

        var lifecycle = db.LifecycleEvents.Where(x => x.PlayerId == p.Id && x.EventType != AppStatusConstants.EventType);
        var snapshots = db.PlayerSnapshots.Where(x => x.PlayerId == p.Id);
        var promotions = db.PromotionCandidates.Where(x => x.PlayerId == p.Id);
        db.LifecycleEvents.RemoveRange(lifecycle);
        db.PlayerSnapshots.RemoveRange(snapshots);
        db.PromotionCandidates.RemoveRange(promotions);
        db.Players.Remove(p);
        await db.SaveChangesAsync(ct);
        return Ok(new { player = p.Username, action = "removed_from_db_and_wom" });
    }

    [HttpPost("players/{id:int}/requeue-discord-card")]
    public async Task<IActionResult> RequeueDiscordCard(int id, CancellationToken ct)
    {
        var p = await db.Players.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();

        var handledBy = User.FindFirstValue(ClaimTypes.Name) ?? "web-admin";
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = p.Id,
            EventType = "DISCORD_REVIEW_REQUEUE_REQUESTED",
            MetadataJson = JsonUtil.Serialize(new
            {
                Player = p.Username,
                HandledBy = handledBy,
                Source = "web"
            }),
            Status = "OPEN",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
        return Ok(new { player = p.Username, action = "requeue_discord_card" });
    }

    [HttpPost("players/{id:int}/merge/confirm")]
    public async Task<IActionResult> ConfirmMergeSuggested(int id, CancellationToken ct)
    {
        var handledBy = User.FindFirstValue(ClaimTypes.Name) ?? "web-admin";
        var result = await mergeReviewService.ConfirmSuggestedAsync(id, handledBy, "web", ct);
        if (!result.Success) return BadRequest(result.Message);
        return Ok(new { message = result.Message });
    }

    [HttpPost("players/{id:int}/merge/reassign")]
    public async Task<IActionResult> ReassignMerge(int id, [FromBody] MergeReassignRequest req, CancellationToken ct)
    {
        var handledBy = User.FindFirstValue(ClaimTypes.Name) ?? "web-admin";
        var result = await mergeReviewService.ReassignAsync(id, req.PreviousUsername ?? "", handledBy, "web", ct);
        if (!result.Success) return BadRequest(result.Message);
        return Ok(new { message = result.Message });
    }

    [HttpPost("players/{id:int}/merge/manual")]
    public async Task<IActionResult> ManualMerge(int id, [FromBody] MergeReassignRequest req, CancellationToken ct)
    {
        var handledBy = User.FindFirstValue(ClaimTypes.Name) ?? "web-admin";
        var result = await mergeReviewService.ReassignAsync(id, req.PreviousUsername ?? "", handledBy, "web", ct);
        if (!result.Success) return BadRequest(result.Message);
        return Ok(new { message = result.Message });
    }

    [HttpPost("players/{id:int}/merge/abort")]
    public async Task<IActionResult> AbortMerge(int id, CancellationToken ct)
    {
        var handledBy = User.FindFirstValue(ClaimTypes.Name) ?? "web-admin";
        var result = await mergeReviewService.AbortAsync(id, handledBy, "web", ct);
        if (!result.Success) return BadRequest(result.Message);
        return Ok(new { message = result.Message });
    }

    private static async Task<bool> IsPlayerInWiseOldManGroupAsync(HttpClient client, string username, int groupId, CancellationToken ct)
    {
        try
        {
            var csv = await client.GetStringAsync($"https://api.wiseoldman.net/v2/groups/{groupId}/csv", ct);
            var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = 1; i < lines.Length; i++)
            {
                var firstField = lines[i].Split(',', 2)[0].Trim().Trim('"');
                if (string.Equals(firstField, username, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private void QueueDiscordMessageUpdate(int playerId, int candidateId, string action)
    {
        var handledBy = User.FindFirstValue(ClaimTypes.Name) ?? "web-admin";
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = playerId,
            EventType = "PROMOTION_DISCORD_ACTION_APPLIED",
            MetadataJson = JsonUtil.Serialize(new
            {
                CandidateId = candidateId,
                Action = action,
                HandledBy = handledBy,
                Source = "web"
            }),
            Status = "OPEN",
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private void QueueTempleMissingDiscordUpdate(int playerId, string username, string action)
    {
        var handledBy = User.FindFirstValue(ClaimTypes.Name) ?? "web-admin";
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = playerId,
            EventType = "TEMPLE_MISSING_ACTION_APPLIED",
            MetadataJson = JsonUtil.Serialize(new
            {
                Player = username,
                Action = action,
                HandledBy = handledBy,
                Source = "web"
            }),
            Status = "OPEN",
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private async Task CloseReviewLifecycleEventsResolvedByStatusAsync(Player player, string handledBy, CancellationToken ct)
    {
        var eventTypes = new List<string>
        {
            "DISCORD_MARK_RENAME_SUSPECT"
        };
        if (player.Status != PlayerStatus.NEW_PENDING_REVIEW)
        {
            eventTypes.Add("NEW_PLAYER");
        }
        if (player.Status != PlayerStatus.MERGE_SUGGESTED)
        {
            eventTypes.Add("MERGE_SUGGESTED");
        }
        else
        {
            await EnsureOpenMergeSuggestedEventAsync(player, handledBy, ct);
        }
        if (player.Status != PlayerStatus.MISSING_PENDING_REVIEW)
        {
            eventTypes.AddRange([
                "MISSING_IN_ROSTER",
                "TEMPLE_MISSING_ACTION_REQUIRED",
                "WOM_MISSING_ACTION_REQUIRED"
            ]);
        }
        if (player.Status == PlayerStatus.REMOVED_CONFIRMED)
        {
            eventTypes.AddRange([
                "WOM_RANK_MISMATCH_REQUIRED",
                "WOM_RANK_MISMATCH_IGNORED"
            ]);
        }

        await CloseOpenLifecycleEventsAsync(player.Id, ct, eventTypes.Distinct().ToArray());
    }

    private async Task EnsureOpenMergeSuggestedEventAsync(Player player, string handledBy, CancellationToken ct)
    {
        var hasOpenMerge = await db.LifecycleEvents.AnyAsync(x =>
            x.PlayerId == player.Id &&
            x.EventType == "MERGE_SUGGESTED" &&
            x.Status == "OPEN", ct);
        if (hasOpenMerge) return;

        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = player.Id,
            EventType = "MERGE_SUGGESTED",
            MetadataJson = JsonUtil.Serialize(new { player.Username, Source = "web", HandledBy = handledBy }),
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
}

public record SetStatusRequest(PlayerStatus Status);
public record ReviewQueueRowDto(
    int Id,
    string Username,
    string Status,
    string CurrentRank,
    string EligibleRank,
    DateTimeOffset LastSeen,
    string DiscordCardState,
    string? MergeMetadataJson);
public record MergeReassignRequest(string? PreviousUsername);
