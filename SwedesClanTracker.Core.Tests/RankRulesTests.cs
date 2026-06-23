using SwedesClanTracker.Core;

namespace SwedesClanTracker.Core.Tests;

public class RankRulesTests
{
    [Fact]
    public void OrderedClanRanks_ExposesCanonicalRankOrder()
    {
        Assert.Equal(
            [
                "Recruit",
                "Officer",
                "Commander",
                "Lieutenant",
                "Captain",
                "Astral",
                "General",
                "Brigadier",
                "Admiral",
                "Marshal",
                "Beast"
            ],
            RankRules.OrderedClanRanks);
    }

    [Fact]
    public void AssignableClanRanks_ExcludesRecruit()
    {
        Assert.Equal(RankRules.OrderedClanRanks.Skip(1), RankRules.AssignableClanRanks);
    }

    [Fact]
    public void RankEvaluatorRequirements_FollowAssignableClanRankOrder()
    {
        Assert.Equal(RankRules.AssignableClanRanks, RankEvaluator.Requirements.Select(x => x.Rank));
    }

    [Theory]
    [InlineData("General", "general")]
    [InlineData("Deputy Owner", "deputy_owner")]
    [InlineData("short green guy", "short_green_guy")]
    public void ToWomRoleValue_NormalizesForWiseOldMan(string rank, string expected)
    {
        Assert.Equal(expected, RankRules.ToWomRoleValue(rank));
    }

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
    public void IsSpecialWomRole_KeepsWilySpecial()
    {
        Assert.True(RankRules.IsSpecialWomRole("Wily"));
    }

    [Theory]
    [InlineData("imp")]
    [InlineData("Kitten")]
    [InlineData("administrator")]
    [InlineData("Deputy Owner")]
    [InlineData("deputy_owner")]
    [InlineData("OWNER")]
    [InlineData("short_green_guy")]
    [InlineData(" recruit ")]
    [InlineData("apothecary")]
    [InlineData("WILY")]
    public void IsSpecialWomRole_NormalizesSpecialRoles(string role)
    {
        Assert.True(RankRules.IsSpecialWomRole(role));
    }

    [Fact]
    public void ClassifyPromotionCandidate_TreatsMemberAsNeedingWomUpdate()
    {
        Assert.Equal(
            PromotionCandidateType.needs_wom_rank_update,
            RankRules.ClassifyPromotionCandidate("Captain", "Member"));
    }

    [Fact]
    public void ClassifyPromotionCandidate_TreatsWilyAsUnknownWomRole()
    {
        Assert.Equal(
            PromotionCandidateType.unknown_wom_role,
            RankRules.ClassifyPromotionCandidate("Captain", "Wily"));
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
