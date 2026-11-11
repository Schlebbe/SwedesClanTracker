using SwedesClanTracker.Worker;

namespace SwedesClanTracker.Worker.Tests;

public class PromotionDiscordActionRulesTests
{
    [Fact]
    public void Unique_exact_match_is_eligible_for_one_click_action()
    {
        var match = new DiscordMemberGuessMatch(
            123,
            "Matteps",
            "<@123>",
            100,
            DiscordMemberMatchStrength.Exact,
            "username",
            "Matteps",
            "Matteps",
            false);

        var result = PromotionDiscordActionRules.FindUniqueExactMatch(new DiscordMemberGuessResult([match]));

        Assert.Same(match, result);
    }

    [Fact]
    public void Multiple_exact_matches_are_not_eligible_for_one_click_action()
    {
        var matches = new DiscordMemberGuessResult(
        [
            CreateMatch(123, DiscordMemberMatchStrength.Exact),
            CreateMatch(456, DiscordMemberMatchStrength.Exact)
        ]);

        Assert.Null(PromotionDiscordActionRules.FindUniqueExactMatch(matches));
    }

    [Fact]
    public void One_exact_match_is_eligible_even_when_weaker_matches_also_exist()
    {
        var exact = CreateMatch(123, DiscordMemberMatchStrength.Exact);
        var matches = new DiscordMemberGuessResult(
        [
            exact,
            CreateMatch(456, DiscordMemberMatchStrength.Possible)
        ]);

        Assert.Same(exact, PromotionDiscordActionRules.FindUniqueExactMatch(matches));
    }

    [Fact]
    public void Expected_previous_rank_role_is_replaced()
    {
        var result = PromotionDiscordActionRules.EvaluateRankRoleState(
            "Brassican",
            "Commander",
            ["Brassican"]);

        Assert.Equal(DiscordPromotionRankRoleAction.Replace, result.Action);
        Assert.True(result.CanApply);
    }

    [Fact]
    public void Unexpected_rank_role_is_rejected_without_an_override()
    {
        var result = PromotionDiscordActionRules.EvaluateRankRoleState(
            "Captain",
            "General",
            ["Lieutenant"]);

        Assert.Equal(DiscordPromotionRankRoleAction.Reject, result.Action);
        Assert.Contains("Captain", result.Details, StringComparison.Ordinal);
        Assert.Contains("Lieutenant", result.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_previous_rank_role_is_rejected()
    {
        var result = PromotionDiscordActionRules.EvaluateRankRoleState(
            "Captain",
            "General",
            []);

        Assert.Equal(DiscordPromotionRankRoleAction.Reject, result.Action);
    }

    [Fact]
    public void Brassican_promotion_without_a_previous_rank_role_can_receive_the_new_role()
    {
        var result = PromotionDiscordActionRules.EvaluateRankRoleState(
            "Brassican",
            "Commander",
            []);

        Assert.Equal(DiscordPromotionRankRoleAction.Add, result.Action);
        Assert.True(result.CanApply);
    }

    [Fact]
    public void Brassican_promotion_with_only_a_guest_role_can_receive_the_new_role()
    {
        var result = PromotionDiscordActionRules.EvaluateRankRoleState(
            "Brassican",
            "Commander",
            [PromotionDiscordActionRules.GuestRoleName]);

        Assert.Equal(DiscordPromotionRankRoleAction.Add, result.Action);
    }

    [Fact]
    public void Brassican_promotion_with_an_unrecognized_role_is_still_rejected()
    {
        var result = PromotionDiscordActionRules.EvaluateRankRoleState(
            "Brassican",
            "Commander",
            ["Wily"]);

        Assert.Equal(DiscordPromotionRankRoleAction.Reject, result.Action);
    }

    [Fact]
    public void Legacy_recruit_without_a_rank_role_can_receive_the_new_role()
    {
        var result = PromotionDiscordActionRules.EvaluateRankRoleState(
            "Recruit",
            "Brassican",
            []);

        Assert.Equal(DiscordPromotionRankRoleAction.Add, result.Action);
    }

    [Fact]
    public void Existing_new_rank_role_is_idempotent()
    {
        var result = PromotionDiscordActionRules.EvaluateRankRoleState(
            "Captain",
            "General",
            ["General"]);

        Assert.Equal(DiscordPromotionRankRoleAction.AlreadyCorrect, result.Action);
    }

    [Fact]
    public void Multiple_rank_roles_are_rejected()
    {
        var result = PromotionDiscordActionRules.EvaluateRankRoleState(
            "Captain",
            "General",
            ["Captain", "Lieutenant"]);

        Assert.Equal(DiscordPromotionRankRoleAction.Reject, result.Action);
    }

    [Fact]
    public void Brassican_promotion_does_not_strip_a_conflicting_higher_rank()
    {
        var result = PromotionDiscordActionRules.EvaluateRankRoleState(
            "Brassican",
            "Commander",
            ["General"]);

        Assert.Equal(DiscordPromotionRankRoleAction.Reject, result.Action);
        Assert.Contains("General", result.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Brassican_promotion_does_not_apply_when_only_a_legacy_external_recruit_role_exists()
    {
        var result = PromotionDiscordActionRules.EvaluateRankRoleState(
            "Brassican",
            "Commander",
            ["Recruit"]);

        Assert.Equal(DiscordPromotionRankRoleAction.Reject, result.Action);
        Assert.Empty(result.ExistingRanks);
    }

    private static DiscordMemberGuessMatch CreateMatch(ulong userId, DiscordMemberMatchStrength strength) =>
        new(
            userId,
            $"User {userId}",
            $"<@{userId}>",
            strength == DiscordMemberMatchStrength.Exact ? 100 : 90,
            strength,
            "username",
            "Matteps",
            "Matteps",
            false);
}
