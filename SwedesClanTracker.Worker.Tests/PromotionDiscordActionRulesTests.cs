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
            "Captain",
            "Astral",
            ["Captain"]);

        Assert.Equal(DiscordPromotionRankRoleAction.Replace, result.Action);
        Assert.True(result.CanApply);
    }

    [Fact]
    public void Unexpected_rank_role_is_rejected_without_an_override()
    {
        var result = PromotionDiscordActionRules.EvaluateRankRoleState(
            "Captain",
            "Astral",
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
            "Astral",
            []);

        Assert.Equal(DiscordPromotionRankRoleAction.Reject, result.Action);
    }

    [Fact]
    public void Recruit_without_a_rank_role_can_receive_the_new_role()
    {
        var result = PromotionDiscordActionRules.EvaluateRankRoleState(
            "Recruit",
            "Officer",
            []);

        Assert.Equal(DiscordPromotionRankRoleAction.Add, result.Action);
    }

    [Fact]
    public void Existing_new_rank_role_is_idempotent()
    {
        var result = PromotionDiscordActionRules.EvaluateRankRoleState(
            "Captain",
            "Astral",
            ["Astral"]);

        Assert.Equal(DiscordPromotionRankRoleAction.AlreadyCorrect, result.Action);
    }

    [Fact]
    public void Multiple_rank_roles_are_rejected()
    {
        var result = PromotionDiscordActionRules.EvaluateRankRoleState(
            "Captain",
            "Astral",
            ["Captain", "Lieutenant"]);

        Assert.Equal(DiscordPromotionRankRoleAction.Reject, result.Action);
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
