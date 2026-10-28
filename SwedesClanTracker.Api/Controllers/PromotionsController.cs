using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/promotions")]
public class PromotionsController(
    TrackerDbContext db,
    IPromotionApprovalService promotionApprovalService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var rows = await db.PromotionCandidates
            .Where(x => x.Status == PromotionStatus.PENDING)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new
            {
                x.Id,
                x.PlayerId,
                Username = x.Player.Username,
                x.OldRank,
                x.NewRank,
                x.Reason,
                x.CandidateType,
                x.WomRoleAtEvaluation,
                x.CreatedAt
            })
            .ToListAsync(ct);

        return Ok(rows);
    }

    [HttpPost("{id:int}/approve")]
    public async Task<IActionResult> Approve(int id, CancellationToken ct)
    {
        var c = await db.PromotionCandidates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        var p = await db.Players.FirstAsync(x => x.Id == c.PlayerId, ct);
        var approval = await promotionApprovalService.ApproveAsync(c, p, GetHandledBy(), "web", ct);
        if (!approval.Approved)
        {
            var statusCode = approval.Retryable
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status409Conflict;
            var problem = new ProblemDetails
            {
                Status = statusCode,
                Title = approval.Retryable ? "Wise Old Man unavailable" : "Promotion cannot be approved",
                Detail = approval.Details,
                Instance = HttpContext.Request.Path
            };
            problem.Extensions["candidateType"] = approval.CandidateType.ToString();
            return StatusCode(statusCode, problem);
        }

        QueueDiscordMessageUpdate(c.PlayerId, c.Id, "approve");
        await db.SaveChangesAsync(ct);
        return Ok(new
        {
            Success = approval.WomUpdate?.Success ?? true,
            approval.Details,
            UpdatedRole = approval.WomUpdate?.UpdatedRole,
            approval.CandidateType
        });
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

        var womUpdates = new List<object>();
        var handledBy = GetHandledBy();
        var approved = 0;

        foreach (var c in pending)
        {
            var p = await db.Players.FirstOrDefaultAsync(x => x.Id == c.PlayerId, ct);
            if (p is null) continue;

            var approval = await promotionApprovalService.ApproveAsync(
                c,
                p,
                handledBy,
                "web-approve-all",
                ct);
            if (approval.Approved)
            {
                approved++;
                QueueDiscordMessageUpdate(c.PlayerId, c.Id, "approve");
            }

            womUpdates.Add(new
            {
                c.Id,
                c.PlayerId,
                p.Username,
                Success = approval.Approved,
                approval.Details,
                UpdatedRole = approval.WomUpdate?.UpdatedRole,
                approval.CandidateType
            });
            await db.SaveChangesAsync(ct);
        }

        return Ok(new { approved, womUpdates });
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

}
