using Microsoft.EntityFrameworkCore;

namespace SwedesClanTracker.Core;

public interface IPromotionApprovalService
{
    Task<PromotionApprovalResult> ApproveAsync(
        PromotionCandidate candidate,
        Player player,
        string handledBy,
        string source,
        CancellationToken ct);
}

public sealed record PromotionApprovalResult(
    bool Approved,
    bool Retryable,
    PromotionCandidateType CandidateType,
    string Details,
    WomRoleUpdateResult? WomUpdate = null);

public sealed class PromotionApprovalService(
    TrackerDbContext db,
    IWiseOldManClient wiseOldManClient) : IPromotionApprovalService
{
    private static readonly TimeSpan WomApprovalTimeout = TimeSpan.FromSeconds(10);

    public async Task<PromotionApprovalResult> ApproveAsync(
        PromotionCandidate candidate,
        Player player,
        string handledBy,
        string source,
        CancellationToken ct)
    {
        if (candidate.Status != PromotionStatus.PENDING)
        {
            return new PromotionApprovalResult(
                Approved: false,
                Retryable: false,
                candidate.CandidateType,
                $"Promotion was already handled ({candidate.Status}).");
        }

        var candidateType = candidate.CandidateType;
        var womRoleBefore = candidate.WomRoleAtEvaluation;

        if (candidateType == PromotionCandidateType.unknown_wom_role)
        {
            var resolution = await ResolveUnknownCandidateAsync(candidate, player, ct);
            if (!resolution.Success)
            {
                return resolution.Result!;
            }

            candidateType = candidate.CandidateType;
            womRoleBefore = candidate.WomRoleAtEvaluation;
        }

        WomRoleUpdateResult? womUpdate = null;
        if (candidateType == PromotionCandidateType.needs_wom_rank_update)
        {
            womUpdate = await UpdateWomRoleAsync(player.Username, candidate.NewRank, ct);
            if (!womUpdate.Success)
            {
                await db.SaveChangesAsync(ct);
                return new PromotionApprovalResult(
                    Approved: false,
                    Retryable: IsRetryableWomFailure(womUpdate),
                    candidateType,
                    "Wise Old Man did not accept the required role update. The promotion remains pending and can be retried.",
                    womUpdate);
            }
        }

        player.CurrentRank = candidate.NewRank;
        await CloseOpenLifecycleEventsAsync(
            player.Id,
            ct,
            "WOM_RANK_MISMATCH_REQUIRED",
            "WOM_RANK_MISMATCH_IGNORED");

        var details = candidateType == PromotionCandidateType.wom_already_at_new_rank
            ? "Wise Old Man already matched the candidate when it was evaluated; database rank updated without a Wise Old Man request."
            : womUpdate?.Details ?? "Wise Old Man role update completed.";

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
                UpdatedRole = womUpdate?.UpdatedRole ?? womRoleBefore,
                Success = true,
                Skipped = candidateType == PromotionCandidateType.wom_already_at_new_rank,
                CandidateType = candidateType,
                HttpStatus = womUpdate?.HttpStatus ?? 0,
                WiseOldManPlayerId = womUpdate?.WomPlayerId,
                WiseOldManDisplayName = womUpdate?.DisplayName,
                Details = details,
                HandledBy = handledBy,
                Source = $"promotion-approval-{source}"
            }),
            Status = "DONE",
            CreatedAt = DateTimeOffset.UtcNow
        });

        candidate.Status = PromotionStatus.APPROVED;
        return new PromotionApprovalResult(
            Approved: true,
            Retryable: false,
            candidateType,
            details,
            womUpdate ?? new WomRoleUpdateResult(true, 0, details, womRoleBefore, null, null));
    }

    private async Task<(bool Success, PromotionApprovalResult? Result)> ResolveUnknownCandidateAsync(
        PromotionCandidate candidate,
        Player player,
        CancellationToken ct)
    {
        try
        {
            using var timeout = CreateWomTimeout(ct);
            var womRole = await wiseOldManClient.GetMemberRoleAsync(player.Username, timeout.Token);
            candidate.WomRoleAtEvaluation = NormalizeObservedRole(womRole);
            candidate.CandidateType = RankRules.ClassifyPromotionCandidate(candidate.NewRank, womRole);

            if (candidate.CandidateType == PromotionCandidateType.unknown_wom_role)
            {
                await db.SaveChangesAsync(ct);
                return (false, new PromotionApprovalResult(
                    Approved: false,
                    Retryable: false,
                    candidate.CandidateType,
                    "Wise Old Man returned no recognized clan role for this candidate. The promotion remains pending for review."));
            }

            return (true, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, new PromotionApprovalResult(
                Approved: false,
                Retryable: true,
                candidate.CandidateType,
                "Wise Old Man did not respond in time. The promotion remains pending and can be retried."));
        }
        catch (WiseOldManRateLimitException)
        {
            return (false, new PromotionApprovalResult(
                Approved: false,
                Retryable: true,
                candidate.CandidateType,
                "Wise Old Man is rate limited. The promotion remains pending and can be retried."));
        }
        catch (HttpRequestException)
        {
            return (false, new PromotionApprovalResult(
                Approved: false,
                Retryable: true,
                candidate.CandidateType,
                "Wise Old Man is currently unavailable. The promotion remains pending and can be retried."));
        }
    }

    private async Task<WomRoleUpdateResult> UpdateWomRoleAsync(
        string username,
        string role,
        CancellationToken ct)
    {
        using var timeout = CreateWomTimeout(ct);
        return await wiseOldManClient.UpdateMemberRoleAsync(username, role, timeout.Token);
    }

    private static CancellationTokenSource CreateWomTimeout(CancellationToken ct)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(WomApprovalTimeout);
        return timeout;
    }

    private static string? NormalizeObservedRole(string? role) =>
        string.IsNullOrWhiteSpace(role) ? null : RankRules.NormalizeRankName(role);

    private static bool IsRetryableWomFailure(WomRoleUpdateResult result) =>
        result.HttpStatus == 0 ||
        result.HttpStatus == 408 ||
        result.HttpStatus == 429 ||
        result.HttpStatus >= 500;

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
