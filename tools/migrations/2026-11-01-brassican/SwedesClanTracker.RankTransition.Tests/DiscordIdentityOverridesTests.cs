using SwedesClanTracker.RankTransition;

namespace SwedesClanTracker.RankTransition.Tests;

public sealed class DiscordIdentityOverridesTests
{
    [Fact]
    public void Parses_reviewed_object_and_allows_shared_discord_accounts()
    {
        var overrides = DiscordIdentityOverrides.Parse(
            "{\"7\":\"123\",\"8\":\"123\"}",
            new HashSet<string>(["123"], StringComparer.Ordinal));

        Assert.Equal("123", overrides.PlayerToDiscordUserId[7]);
        Assert.Equal(new[] { "123" }, overrides.SharedDiscordUserIds);
    }

    [Fact]
    public void Parses_array_shape_for_human_review_files()
    {
        var overrides = DiscordIdentityOverrides.Parse(
            "[{\"playerId\":7,\"discordUserId\":\"123\"},{\"playerId\":8,\"discordUserId\":\"456\"}]",
            new HashSet<string>(["123", "456"], StringComparer.Ordinal));

        Assert.Equal(2, overrides.PlayerToDiscordUserId.Count);
        Assert.Equal("456", overrides.PlayerToDiscordUserId[8]);
        Assert.Empty(overrides.SharedDiscordUserIds);
    }

    [Fact]
    public void Rejects_duplicate_player_ids_even_when_json_object_properties_repeat()
    {
        var exception = Assert.Throws<InvalidDataException>(() => DiscordIdentityOverrides.Parse(
            "{\"7\":\"123\",\"7\":\"456\"}"));

        Assert.Contains("duplicate PlayerId", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_array_duplicate_player_ids_and_unknown_discord_targets()
    {
        var duplicate = Assert.Throws<InvalidDataException>(() => DiscordIdentityOverrides.Parse(
            "[{\"playerId\":7,\"discordUserId\":\"123\"},{\"playerId\":7,\"discordUserId\":\"456\"}]"));
        Assert.Contains("duplicate PlayerId", duplicate.Message, StringComparison.OrdinalIgnoreCase);

        var missing = Assert.Throws<InvalidDataException>(() => DiscordIdentityOverrides.Parse(
            "{\"7\":\"999\"}",
            new HashSet<string>(["123"], StringComparer.Ordinal)));
        Assert.Contains("not present", missing.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"0\":\"123\"}")]
    [InlineData("{\"7\":\"not-a-discord-id\"}")]
    [InlineData("{\"7\":\"\"}")]
    public void Rejects_invalid_ids(string json)
    {
        Assert.Throws<InvalidDataException>(() => DiscordIdentityOverrides.Parse(json));
    }
}
