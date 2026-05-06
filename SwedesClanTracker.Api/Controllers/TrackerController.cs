using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.Api.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public class TrackerController(TrackerDbContext db, ITrackerSyncService syncService) : ControllerBase
{
    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken ct)
    {
        var players = await db.Players.CountAsync(ct);
        var pendingPromotions = await db.PromotionCandidates.CountAsync(x => x.Status == PromotionStatus.PENDING, ct);
        var missing = await db.Players.CountAsync(x => x.Status == PlayerStatus.MISSING_PENDING_REVIEW, ct);
        var pendingReview = await db.Players.CountAsync(x => x.Status == PlayerStatus.NEW_PENDING_REVIEW || x.Status == PlayerStatus.MERGE_SUGGESTED, ct);
        return Ok(new { players, pendingPromotions, missing, pendingReview });
    }

    [HttpGet("players")]
    public async Task<IActionResult> Players(CancellationToken ct)
    {
        var rows = await db.Players
            .OrderBy(x => x.Username)
            .Select(x => new
            {
                x.Id,
                x.Username,
                x.CurrentRank,
                x.Status,
                x.StoredPetCount,
                x.ManualPetOverride,
                x.LastSeen,
                x.LastSynced,
                Latest = x.Snapshots
                    .OrderByDescending(s => s.Timestamp)
                    .Select(s => new
                    {
                        s.TotalLevel,
                        s.Ehb,
                        s.Ehp
                    })
                    .FirstOrDefault()
            })
            .Select(x => new
            {
                x.Id,
                x.Username,
                x.CurrentRank,
                x.Status,
                x.StoredPetCount,
                x.ManualPetOverride,
                x.LastSeen,
                x.LastSynced,
                TotalLevel = x.Latest != null ? x.Latest.TotalLevel : (int?)null,
                Ehb = x.Latest != null ? x.Latest.Ehb : (double?)null,
                Ehp = x.Latest != null ? x.Latest.Ehp : (double?)null
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPost("players/{id:int}/manual-pets")]
    public async Task<IActionResult> SetManualPets(int id, [FromBody] SetManualPetRequest req, CancellationToken ct)
    {
        var p = await db.Players.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        p.ManualPetOverride = req.Count;
        await db.SaveChangesAsync(ct);
        return Ok();
    }

    [HttpPost("sync/run-once")]
    public async Task<IActionResult> RunSync(CancellationToken ct)
    {
        var queued = await syncService.SyncRosterAndQueueAsync(ct);
        return Ok(new { queued = queued.Count });
    }
}

public record SetManualPetRequest(int? Count);
