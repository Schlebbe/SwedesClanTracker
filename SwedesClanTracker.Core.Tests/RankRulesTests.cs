using SwedesClanTracker.Core;

namespace SwedesClanTracker.Core.Tests;

public class RankRulesTests
{
    [Fact]
    public void OrderedClanRanks_ExposesCanonicalRankOrder()
    {
        Assert.Equal(
            [
                "Brassican",
                "Commander",
                "Lieutenant",
                "Captain",
                "General",
                "Colonel",
                "Brigadier",
                "Admiral",
                "Marshal",
                "Beast"
            ],
            RankRules.OrderedClanRanks);
    }

    [Fact]
    public void AssignableClanRanks_IncludesBrassicanAndAllCanonicalRanks()
    {
        Assert.Equal(RankRules.OrderedClanRanks, RankRules.AssignableClanRanks);
    }

    [Fact]
    public void RankEvaluatorRequirements_FollowAssignableClanRankOrder()
    {
        Assert.Equal(RankRules.AssignableClanRanks, RankEvaluator.Requirements.Select(x => x.Rank));
    }

    [Fact]
    public void NewPlayerDefaults_UseBrassican()
    {
        var player = new Player();

        Assert.Equal("Brassican", player.CurrentRank);
        Assert.Equal("Brassican", player.EligibleRank);
    }

    [Fact]
    public void Evaluate_DefaultsToBrassicanWhenNoRequirementIsMet()
    {
        var result = RankEvaluator.Evaluate(Snapshot());

        Assert.Equal("Brassican", result.Rank);
        Assert.Equal("Brassican via: No rank requirement met.", result.Explanation);
    }

    [Theory]
    [MemberData(nameof(RankBoundaryCases))]
    public void Evaluate_UsesEveryPolicyBoundary(
        int totalLevel,
        double ehb,
        double ehp,
        int pets,
        int collections,
        string expectedRank)
    {
        var result = RankEvaluator.Evaluate(Snapshot(totalLevel, ehb, ehp, pets, collections));

        Assert.Equal(expectedRank, result.Rank);
    }

    [Fact]
    public void Evaluate_SelectsHighestMatchingRankFromMixedStats()
    {
        var result = RankEvaluator.Evaluate(Snapshot(
            totalLevel: 2200,
            ehb: 1500,
            ehp: 2000,
            pets: 25,
            collections: 1050));

        Assert.Equal("Colonel", result.Rank);
    }

    public static IEnumerable<object[]> RankBoundaryCases()
    {
        // Commander total-level boundary (2100 is not a threshold in the new policy).
        yield return new object[] { 2099, 0d, 0d, 0, 0, "Brassican" };
        yield return new object[] { 2100, 0d, 0d, 0, 0, "Brassican" };
        yield return new object[] { 2199, 0d, 0d, 0, 0, "Brassican" };
        yield return new object[] { 2200, 0d, 0d, 0, 0, "Commander" };
        yield return new object[] { 2201, 0d, 0d, 0, 0, "Commander" };

        // EHB boundaries: just below, exact, and just above each threshold.
        yield return new object[] { 0, 749d, 0d, 0, 0, "Brassican" };
        yield return new object[] { 0, 750d, 0d, 0, 0, "Lieutenant" };
        yield return new object[] { 0, 751d, 0d, 0, 0, "Lieutenant" };
        yield return new object[] { 0, 999d, 0d, 0, 0, "Lieutenant" };
        yield return new object[] { 0, 1000d, 0d, 0, 0, "Captain" };
        yield return new object[] { 0, 1001d, 0d, 0, 0, "Captain" };
        yield return new object[] { 0, 1249d, 0d, 0, 0, "Captain" };
        yield return new object[] { 0, 1250d, 0d, 0, 0, "General" };
        yield return new object[] { 0, 1251d, 0d, 0, 0, "General" };
        yield return new object[] { 0, 1499d, 0d, 0, 0, "General" };
        yield return new object[] { 0, 1500d, 0d, 0, 0, "Colonel" };
        yield return new object[] { 0, 1501d, 0d, 0, 0, "Colonel" };
        yield return new object[] { 0, 1999d, 0d, 0, 0, "Colonel" };
        yield return new object[] { 0, 2000d, 0d, 0, 0, "Brigadier" };
        yield return new object[] { 0, 2001d, 0d, 0, 0, "Brigadier" };
        yield return new object[] { 0, 2999d, 0d, 0, 0, "Brigadier" };
        yield return new object[] { 0, 3000d, 0d, 0, 0, "Admiral" };
        yield return new object[] { 0, 3001d, 0d, 0, 0, "Admiral" };
        yield return new object[] { 0, 3999d, 0d, 0, 0, "Admiral" };
        yield return new object[] { 0, 4000d, 0d, 0, 0, "Marshal" };
        yield return new object[] { 0, 4001d, 0d, 0, 0, "Marshal" };
        yield return new object[] { 0, 4999d, 0d, 0, 0, "Marshal" };
        yield return new object[] { 0, 5000d, 0d, 0, 0, "Beast" };
        yield return new object[] { 0, 5001d, 0d, 0, 0, "Beast" };

        // EHP boundaries: just below, exact, and just above each threshold.
        yield return new object[] { 0, 0d, 999d, 0, 0, "Brassican" };
        yield return new object[] { 0, 0d, 1000d, 0, 0, "Lieutenant" };
        yield return new object[] { 0, 0d, 1001d, 0, 0, "Lieutenant" };
        yield return new object[] { 0, 0d, 1499d, 0, 0, "Lieutenant" };
        yield return new object[] { 0, 0d, 1500d, 0, 0, "Captain" };
        yield return new object[] { 0, 0d, 1501d, 0, 0, "Captain" };
        yield return new object[] { 0, 0d, 1749d, 0, 0, "Captain" };
        yield return new object[] { 0, 0d, 1750d, 0, 0, "General" };
        yield return new object[] { 0, 0d, 1751d, 0, 0, "General" };
        yield return new object[] { 0, 0d, 1999d, 0, 0, "General" };
        yield return new object[] { 0, 0d, 2000d, 0, 0, "Colonel" };
        yield return new object[] { 0, 0d, 2001d, 0, 0, "Colonel" };
        yield return new object[] { 0, 0d, 2499d, 0, 0, "Colonel" };
        yield return new object[] { 0, 0d, 2500d, 0, 0, "Brigadier" };
        yield return new object[] { 0, 0d, 2501d, 0, 0, "Brigadier" };
        yield return new object[] { 0, 0d, 3999d, 0, 0, "Brigadier" };
        yield return new object[] { 0, 0d, 4000d, 0, 0, "Admiral" };
        yield return new object[] { 0, 0d, 4001d, 0, 0, "Admiral" };
        yield return new object[] { 0, 0d, 4999d, 0, 0, "Admiral" };
        yield return new object[] { 0, 0d, 5000d, 0, 0, "Marshal" };
        yield return new object[] { 0, 0d, 5001d, 0, 0, "Marshal" };
        yield return new object[] { 0, 0d, 6999d, 0, 0, "Marshal" };
        yield return new object[] { 0, 0d, 7000d, 0, 0, "Beast" };
        yield return new object[] { 0, 0d, 7001d, 0, 0, "Beast" };

        // Pet-count boundaries: just below, exact, and just above each threshold.
        yield return new object[] { 0, 0d, 0d, 9, 0, "Brassican" };
        yield return new object[] { 0, 0d, 0d, 10, 0, "Captain" };
        yield return new object[] { 0, 0d, 0d, 11, 0, "Captain" };
        yield return new object[] { 0, 0d, 0d, 14, 0, "Captain" };
        yield return new object[] { 0, 0d, 0d, 15, 0, "General" };
        yield return new object[] { 0, 0d, 0d, 16, 0, "General" };
        yield return new object[] { 0, 0d, 0d, 24, 0, "General" };
        yield return new object[] { 0, 0d, 0d, 25, 0, "Colonel" };
        yield return new object[] { 0, 0d, 0d, 26, 0, "Colonel" };
        yield return new object[] { 0, 0d, 0d, 34, 0, "Colonel" };
        yield return new object[] { 0, 0d, 0d, 35, 0, "Brigadier" };
        yield return new object[] { 0, 0d, 0d, 36, 0, "Brigadier" };
        yield return new object[] { 0, 0d, 0d, 44, 0, "Brigadier" };
        yield return new object[] { 0, 0d, 0d, 45, 0, "Admiral" };
        yield return new object[] { 0, 0d, 0d, 46, 0, "Admiral" };
        yield return new object[] { 0, 0d, 0d, 54, 0, "Admiral" };
        yield return new object[] { 0, 0d, 0d, 55, 0, "Marshal" };
        yield return new object[] { 0, 0d, 0d, 56, 0, "Marshal" };
        yield return new object[] { 0, 0d, 0d, 64, 0, "Marshal" };
        yield return new object[] { 0, 0d, 0d, 65, 0, "Beast" };
        yield return new object[] { 0, 0d, 0d, 66, 0, "Beast" };

        // Collection-count boundaries: just below, exact, and just above each threshold.
        yield return new object[] { 0, 0d, 0d, 0, 799, "Brassican" };
        yield return new object[] { 0, 0d, 0d, 0, 800, "Captain" };
        yield return new object[] { 0, 0d, 0d, 0, 801, "Captain" };
        yield return new object[] { 0, 0d, 0d, 0, 899, "Captain" };
        yield return new object[] { 0, 0d, 0d, 0, 900, "General" };
        yield return new object[] { 0, 0d, 0d, 0, 901, "General" };
        yield return new object[] { 0, 0d, 0d, 0, 1049, "General" };
        yield return new object[] { 0, 0d, 0d, 0, 1050, "Colonel" };
        yield return new object[] { 0, 0d, 0d, 0, 1051, "Colonel" };
        yield return new object[] { 0, 0d, 0d, 0, 1199, "Colonel" };
        yield return new object[] { 0, 0d, 0d, 0, 1200, "Brigadier" };
        yield return new object[] { 0, 0d, 0d, 0, 1201, "Brigadier" };
        yield return new object[] { 0, 0d, 0d, 0, 1349, "Brigadier" };
        yield return new object[] { 0, 0d, 0d, 0, 1350, "Admiral" };
        yield return new object[] { 0, 0d, 0d, 0, 1351, "Admiral" };
        yield return new object[] { 0, 0d, 0d, 0, 1449, "Admiral" };
        yield return new object[] { 0, 0d, 0d, 0, 1450, "Marshal" };
        yield return new object[] { 0, 0d, 0d, 0, 1451, "Marshal" };
        yield return new object[] { 0, 0d, 0d, 0, 1549, "Marshal" };
        yield return new object[] { 0, 0d, 0d, 0, 1550, "Beast" };
        yield return new object[] { 0, 0d, 0d, 0, 1551, "Beast" };
    }

    [Theory]
    [InlineData("General", "general")]
    [InlineData("Brassican", "brassican")]
    [InlineData("Deputy Owner", "deputy_owner")]
    [InlineData("short green guy", "short_green_guy")]
    public void ToWomRoleValue_NormalizesForWiseOldMan(string rank, string expected)
    {
        Assert.Equal(expected, RankRules.ToWomRoleValue(rank));
    }

    [Fact]
    public void IsSpecialWomRole_KeepsLegacyRecruitSpecialButBrassicanAssignable()
    {
        Assert.True(RankRules.IsSpecialWomRole("Recruit"));
        Assert.False(RankRules.IsSpecialWomRole("Brassican"));
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

    [Theory]
    [InlineData("Goblin")]
    [InlineData("future wom role")]
    [InlineData("future_wom_role")]
    [InlineData("Officer")]
    [InlineData("Astral")]
    public void IsSpecialWomRole_TreatsRemovedAndUnknownRolesAsSpecial(string role)
    {
        Assert.True(RankRules.IsSpecialWomRole(role));
    }

    [Theory]
    [InlineData("Officer")]
    [InlineData("Astral")]
    [InlineData("Recruit")]
    public void IsKnownClanRank_ExcludesRetiredRanks(string rank)
    {
        Assert.False(RankRules.IsKnownClanRank(rank));
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
    public void ClassifyPromotionCandidate_TreatsMemberAsNeedingBrassicanWomUpdate()
    {
        Assert.Equal(
            PromotionCandidateType.needs_wom_rank_update,
            RankRules.ClassifyPromotionCandidate("Brassican", "Member"));
    }

    [Fact]
    public void ClassifyPromotionCandidate_TreatsBrassicanWomRoleAsAlreadyAtNewRank()
    {
        Assert.Equal(
            PromotionCandidateType.wom_already_at_new_rank,
            RankRules.ClassifyPromotionCandidate("Brassican", "brassican"));
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
        Assert.True(RankRules.IsExactKnownClanRankMatch("Brassican", "brassican"));
    }

    [Fact]
    public void IsExactKnownClanRankMatch_DoesNotTreatMemberOrLegacyRecruitAsBrassican()
    {
        Assert.False(RankRules.IsExactKnownClanRankMatch("Brassican", "member"));
        Assert.False(RankRules.IsExactKnownClanRankMatch("Brassican", "Recruit"));
    }

    [Fact]
    public void IsExactKnownClanRankMatch_DoesNotMatchSpecialUnknownRoles()
    {
        Assert.False(RankRules.IsExactKnownClanRankMatch("General", "Short green guy"));
    }

    private static PlayerSnapshot Snapshot(
        int totalLevel = 0,
        double ehb = 0,
        double ehp = 0,
        int pets = 0,
        int collections = 0) =>
        new()
        {
            TotalLevel = totalLevel,
            Ehb = ehb,
            Ehp = ehp,
            PetCount = pets,
            Collections = collections
        };
}
