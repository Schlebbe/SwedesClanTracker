namespace SwedesClanTracker.Core;

public enum PromotionCardPatchDecision
{
    Patch,
    SkipCandidateNotPending,
    SkipMessageNotActionable
}

public static class PromotionCardPatchGuard
{
    public static PromotionCardPatchDecision Decide(PromotionStatus candidateStatus, bool hasActionablePromotionButtons)
    {
        if (candidateStatus != PromotionStatus.PENDING)
        {
            return PromotionCardPatchDecision.SkipCandidateNotPending;
        }

        if (!hasActionablePromotionButtons)
        {
            return PromotionCardPatchDecision.SkipMessageNotActionable;
        }

        return PromotionCardPatchDecision.Patch;
    }
}
