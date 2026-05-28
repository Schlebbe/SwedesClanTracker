using SwedesClanTracker.Core;

namespace SwedesClanTracker.Core.Tests;

public class PromotionCardPatchGuardTests
{
    [Fact]
    public void Decide_WhenCandidateIsNoLongerPending_SkipsPatchForCleanup()
    {
        var decision = PromotionCardPatchGuard.Decide(PromotionStatus.APPROVED, hasActionablePromotionButtons: true);

        Assert.Equal(PromotionCardPatchDecision.SkipCandidateNotPending, decision);
    }

    [Fact]
    public void Decide_WhenCandidatePendingButMessageHasNoActionButtons_SkipsPatch()
    {
        var decision = PromotionCardPatchGuard.Decide(PromotionStatus.PENDING, hasActionablePromotionButtons: false);

        Assert.Equal(PromotionCardPatchDecision.SkipMessageNotActionable, decision);
    }

    [Fact]
    public void Decide_WhenCandidatePendingAndMessageActionable_AllowsPatch()
    {
        var decision = PromotionCardPatchGuard.Decide(PromotionStatus.PENDING, hasActionablePromotionButtons: true);

        Assert.Equal(PromotionCardPatchDecision.Patch, decision);
    }
}
