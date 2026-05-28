using SwedesClanTracker.Core;

namespace SwedesClanTracker.Core.Tests;

public class LifecycleMetadataMatcherTests
{
    [Fact]
    public void HasIntProperty_MatchesExactCandidateId_NotSubstring()
    {
        const string json = "{\"CandidateId\":23,\"Other\":239}";

        Assert.True(LifecycleMetadataMatcher.HasIntProperty(json, "CandidateId", 23));
        Assert.False(LifecycleMetadataMatcher.HasIntProperty(json, "CandidateId", 2));
        Assert.False(LifecycleMetadataMatcher.HasIntProperty(json, "CandidateId", 239));
    }

    [Fact]
    public void HasUlongProperty_MatchesExactDiscordMessageId()
    {
        const string json = "{\"DiscordMessageId\":1509078334670110861,\"ChannelId\":1362728523357491321}";

        Assert.True(LifecycleMetadataMatcher.HasUlongProperty(json, "DiscordMessageId", 1509078334670110861UL));
        Assert.False(LifecycleMetadataMatcher.HasUlongProperty(json, "DiscordMessageId", 150907833467011086UL));
    }

    [Fact]
    public void HasIntProperty_MatchesExactRequiredEventId_NotCollision()
    {
        const string json = "{\"RequiredEventId\":2017}";

        Assert.True(LifecycleMetadataMatcher.HasIntProperty(json, "RequiredEventId", 2017));
        Assert.False(LifecycleMetadataMatcher.HasIntProperty(json, "RequiredEventId", 201));
    }

    [Fact]
    public void HasStringProperty_MatchesExactLeaseKey()
    {
        const string json = "{\"Key\":\"promotion:23\"}";

        Assert.True(LifecycleMetadataMatcher.HasStringProperty(json, "Key", "promotion:23"));
        Assert.False(LifecycleMetadataMatcher.HasStringProperty(json, "Key", "promotion:2"));
    }
}
