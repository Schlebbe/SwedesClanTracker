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
        await CloseOpenLifecycleEventsAsync(p.Id, ct, "WOM_RANK_MISMATCH_IGNORED", "WOM_RANK_MISMATCH_REQUIRED");
        c.Status = PromotionStatus.APPROVED;
        QueueDiscordMessageUpdate(c.PlayerId, c.Id, "approve");
        await db.SaveChangesAsync(ct);
        return Ok();
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

        foreach (var c in pending)
        {
            if (players.TryGetValue(c.PlayerId, out var p))
            {
                p.CurrentRank = c.NewRank;
                await CloseOpenLifecycleEventsAsync(p.Id, ct, "WOM_RANK_MISMATCH_IGNORED", "WOM_RANK_MISMATCH_REQUIRED");
            }
            c.Status = PromotionStatus.APPROVED;
            QueueDiscordMessageUpdate(c.PlayerId, c.Id, "approve");
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { approved = pending.Count });
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
