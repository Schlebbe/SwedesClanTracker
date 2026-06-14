using SwedesClanTracker.Core;

namespace SwedesClanTracker.Core.Tests;

public class RankRulesTests
{
    [Fact]
    public void IsSpecialWomRole_KeepsRecruitSpecial()
    {
        Assert.True(RankRules.IsSpecialWomRole("Recruit"));
    }

    [Fact]
    public void IsSpecialWomRole_DoesNotTreatMemberAsSpecial()
    {
        Assert.False(RankRules.IsSpecialWomRole("Member"));
    }

    [Fact]
    public void IsSpecialWomRole_KeepsShortGreenGuySpecial()
    {
        Assert.True(RankRules.IsSpecialWomRole("Short green guy"));
    }

    [Fact]
    public void ClassifyPromotionCandidate_TreatsMemberAsNeedingWomUpdate()
    {
        Assert.Equal(
            PromotionCandidateType.needs_wom_rank_update,
            RankRules.ClassifyPromotionCandidate("Captain", "Member"));
    }

    [Fact]
    public void IsExactKnownClanRankMatch_MatchesEqualKnownRanks()
    {
        Assert.True(RankRules.IsExactKnownClanRankMatch("General", "general"));
    }

    [Fact]
    public void IsExactKnownClanRankMatch_DoesNotTreatMemberAsRecruitEquivalent()
    {
        Assert.False(RankRules.IsExactKnownClanRankMatch("Recruit", "member"));
    }

    [Fact]
    public void IsExactKnownClanRankMatch_DoesNotMatchSpecialUnknownRoles()
    {
        Assert.False(RankRules.IsExactKnownClanRankMatch("General", "Short green guy"));
    }
}
