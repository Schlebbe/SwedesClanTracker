using SwedesClanTracker.RankTransition;

namespace SwedesClanTracker.RankTransition.Tests;

public sealed class FrozenRankMappingTests
{
    [Theory]
    [InlineData("Recruit", "Brassican")]
    [InlineData("Officer", "Brassican")]
    [InlineData("Astral", "General")]
    [InlineData("General", "Colonel")]
    [InlineData("Colonel", "Colonel")]
    public void Legacy_mapping_is_frozen_and_non_recursive(string input, string expected)
    {
        Assert.Equal(expected, FrozenRankMapping.Map(input));
    }

    [Fact]
    public void Recruit_is_protected_externally_but_database_can_be_normalized()
    {
        Assert.True(FrozenRankMapping.IsSpecialOrUnknownWomRole("Recruit"));
        Assert.Equal("Brassican", FrozenRankMapping.Map("Recruit"));
    }

    [Fact]
    public void Legacy_normal_roles_are_recognized_before_new_core_special_role_classifier()
    {
        Assert.False(FrozenRankMapping.IsSpecialOrUnknownWomRole("Officer"));
        Assert.False(FrozenRankMapping.IsSpecialOrUnknownWomRole("Astral"));
        Assert.False(FrozenRankMapping.IsSpecialOrUnknownWomRole("General"));
    }
}
