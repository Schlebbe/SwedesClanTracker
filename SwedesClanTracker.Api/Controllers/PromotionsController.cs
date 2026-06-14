using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/promotions")]
public class PromotionsController(TrackerDbContext db, IWiseOldManClient wiseOldManClient) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var rows = await db.PromotionCandidates
            .Where(x => x.Status == PromotionStatus.PENDING)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.Id, x.PlayerId, Username = x.Player.Username, x.OldRank, x.NewRank, x.Reason, x.CreatedAt })
            .ToListAsync(ct);

        var enriched = new List<object>(rows.Count);
        foreach (var row in rows)
        {
            var womRole = await wiseOldManClient.GetMemberRoleAsync(row.Username, ct);
            var candidateType = RankRules.ClassifyPromotionCandidate(row.NewRank, womRole);
            enriched.Add(new
            {
                row.Id,
                row.PlayerId,
                row.Username,
                row.OldRank,
                row.NewRank,
                row.Reason,
                row.CreatedAt,
                CandidateType = candidateType
            });
        }

        return Ok(enriched);
    }

    [HttpPost("{id:int}/approve")]
    public async Task<IActionResult> Approve(int id, CancellationToken ct)
    {
        var c = await db.PromotionCandidates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        var p = await db.Players.FirstAsync(x => x.Id == c.PlayerId, ct);
        p.CurrentRank = c.NewRank;
        await CloseOpenLifecycleEventsAsync(p.Id, ct, "WOM_RANK_MISMATCH_IGNORED");
        var womUpdate = await ApplyWomRoleUpdateForApprovedPromotionAsync(p, c, GetHandledBy(), "web", ct);
        c.Status = PromotionStatus.APPROVED;
        QueueDiscordMessageUpdate(c.PlayerId, c.Id, "approve");
        await db.SaveChangesAsync(ct);
        return Ok(new { womUpdate.Success, womUpdate.Details, womUpdate.UpdatedRole });
    }

    [HttpPost("{id:int}/dismiss")]
    public async Task<IActionResult> Dismiss(int id, CancellationToken ct)
    {
        var c = await db.PromotionCandidates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        c.Status = PromotionStatus.DISMISSED;
        QueueDiscordMessageUpdate(c.PlayerId, c.Id, "dismiss");
        await db.SaveChangesAsync(ct);
        return Ok();
    }

    [HttpPost("approve-all")]
    public async Task<IActionResult> ApproveAll(CancellationToken ct)
    {
        var pending = await db.PromotionCandidates
            .Where(x => x.Status == PromotionStatus.PENDING)
            .ToListAsync(ct);
        if (pending.Count == 0) return Ok(new { approved = 0 });

        var playerIds = pending.Select(x => x.PlayerId).Distinct().ToList();
        var players = await db.Players.Where(x => playerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var womRoles = await wiseOldManClient.GetMemberRolesAsync(ct);
        var womUpdates = new List<object>();
        var handledBy = GetHandledBy();
        var attemptedWomUpdates = 0;

        foreach (var c in pending)
        {
            if (players.TryGetValue(c.PlayerId, out var p))
            {
                p.CurrentRank = c.NewRank;
                await CloseOpenLifecycleEventsAsync(p.Id, ct, "WOM_RANK_MISMATCH_IGNORED");
                womRoles.TryGetValue(UsernameRules.NormalizeUsername(p.Username), out var womRoleBefore);
                var roleAlreadyAligned = IsRankAligned(c.NewRank, womRoleBefore);
                var womUpdate = await ApplyWomRoleUpdateForApprovedPromotionAsync(
                    p,
                    c,
                    handledBy,
                    "web-approve-all",
                    ct,
                    womRoleBefore,
                    fetchWomRoleBefore: false,
                    invalidateWomCache: false);
                if (!roleAlreadyAligned)
                {
                    attemptedWomUpdates++;
                }
                womUpdates.Add(new
                {
                    c.Id,
                    c.PlayerId,
                    p.Username,
                    womUpdate.Success,
                    womUpdate.Details,
                    womUpdate.UpdatedRole
                });
            }
            c.Status = PromotionStatus.APPROVED;
            QueueDiscordMessageUpdate(c.PlayerId, c.Id, "approve");
        }

        if (attemptedWomUpdates > 0)
        {
            await wiseOldManClient.InvalidateCacheAsync(ct);
        }
        await db.SaveChangesAsync(ct);
        return Ok(new { approved = pending.Count, womUpdates });
    }

    private string GetHandledBy() => User.FindFirstValue(ClaimTypes.Name) ?? "web-admin";

    private void QueueDiscordMessageUpdate(int playerId, int candidateId, string action)
    {
        var handledBy = GetHandledBy();
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

    private async Task<WomRoleUpdateResult> ApplyWomRoleUpdateForApprovedPromotionAsync(
        Player player,
        PromotionCandidate candidate,
        string handledBy,
        string source,
        CancellationToken ct,
        string? womRoleBefore = null,
        bool fetchWomRoleBefore = true,
        bool invalidateWomCache = true)
    {
        if (fetchWomRoleBefore)
        {
            womRoleBefore = await wiseOldManClient.GetMemberRoleAsync(player.Username, ct);
        }

        var result = IsRankAligned(candidate.NewRank, womRoleBefore)
            ? new WomRoleUpdateResult(true, 0, "WiseOldMan role already matched approved rank.", womRoleBefore, null, null)
            : await wiseOldManClient.UpdateMemberRoleAsync(player.Username, candidate.NewRank, ct, invalidateWomCache);
        var womRoleAfter = result.UpdatedRole ?? womRoleBefore;

        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = player.Id,
            EventType = "WOM_ROLE_UPDATE_APPLIED",
            MetadataJson = JsonUtil.Serialize(new
            {
                CandidateId = candidate.Id,
                Player = player.Username,
                RequestedRole = candidate.NewRank,
                PreviousWomRole = womRoleBefore,
                UpdatedRole = result.UpdatedRole,
                Success = result.Success,
                HttpStatus = result.HttpStatus,
                WiseOldManPlayerId = result.WomPlayerId,
                WiseOldManDisplayName = result.DisplayName,
                Details = result.Details,
                HandledBy = handledBy,
                Source = $"promotion-approval-{source}"
            }),
            Status = "DONE",
            CreatedAt = DateTimeOffset.UtcNow
        });

        if (IsRankAligned(candidate.NewRank, womRoleAfter))
        {
            await CloseOpenLifecycleEventsAsync(player.Id, ct, "WOM_RANK_MISMATCH_REQUIRED");
        }
        else
        {
            await EnsureWomRankMismatchLifecycleAsync(player, womRoleAfter, source, ct);
        }

        return result;
    }

    private async Task EnsureWomRankMismatchLifecycleAsync(Player player, string? womRole, string source, CancellationToken ct)
    {
        if (player.Status != PlayerStatus.ACTIVE) return;
        if (string.IsNullOrWhiteSpace(womRole)) return;
        if (string.Equals(player.CurrentRank, "Recruit", StringComparison.OrdinalIgnoreCase)) return;
        if (RankRules.IsSpecialWomRole(womRole)) return;
        if (IsRankAligned(player.CurrentRank, womRole)) return;

        var now = DateTimeOffset.UtcNow;
        var direction = GetWomRankMismatchDirection(player.CurrentRank, womRole);
        var openMismatches = await db.LifecycleEvents
            .Where(x =>
                x.PlayerId == player.Id &&
                x.EventType == "WOM_RANK_MISMATCH_REQUIRED" &&
                x.Status == "OPEN")
            .ToListAsync(ct);

        var metadata = JsonUtil.Serialize(new
        {
            player.Username,
            ExpectedRank = player.CurrentRank,
            ActualWomRole = womRole,
            Direction = direction,
            Source = $"promotion-approval-{source}",
            DetectedAt = now
        });

        if (openMismatches.Count > 0)
        {
            foreach (var ev in openMismatches)
            {
                ev.MetadataJson = metadata;
            }
            return;
        }

        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = player.Id,
            EventType = "WOM_RANK_MISMATCH_REQUIRED",
            MetadataJson = metadata,
            Status = "OPEN",
            CreatedAt = now
        });
    }

    private static bool IsRankAligned(string expectedRank, string? actualRank) =>
        !string.IsNullOrWhiteSpace(actualRank) &&
        string.Equals(RankRules.NormalizeRankName(expectedRank), RankRules.NormalizeRankName(actualRank), StringComparison.OrdinalIgnoreCase);

    private static string GetWomRankMismatchDirection(string expectedRank, string actualWomRole)
    {
        var expected = RankRules.RankOrder(expectedRank);
        var actual = RankRules.RankOrder(actualWomRole);
        if (actual > expected) return "higher";
        if (actual < expected) return "lower";
        return "different";
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
