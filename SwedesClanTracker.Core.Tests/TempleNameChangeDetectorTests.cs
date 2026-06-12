using SwedesClanTracker.Core;

namespace SwedesClanTracker.Core.Tests;

public class TempleNameChangeDetectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 12, 18, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromHours(6);

    [Fact]
    public void Detect_CreatesRequirementForTwoCardPattern()
    {
        var result = TempleNameChangeDetector.Detect(Input(
            oldCandidates:
            [
                Old("Chrille", womMissingEventId: 10, womMissingCreatedAt: Now.AddMinutes(-5))
            ],
            womOnlyCandidates:
            [
                New("vrll", requiredEventId: 20)
            ]));

        Assert.NotNull(result);
        Assert.Equal("Chrille", result.PreviousUsername);
        Assert.Equal("vrll", result.NewUsername);
        Assert.Equal(10, result.WomMissingEventId);
        Assert.Null(result.TempleMissingEventId);
        Assert.Equal(20, result.WomOnlyEventId);
    }

    [Fact]
    public void Detect_CreatesRequirementForThreeCardPattern()
    {
        var result = TempleNameChangeDetector.Detect(Input(
            oldCandidates:
            [
                Old(
                    "Chrille",
                    womMissingEventId: 10,
                    womMissingCreatedAt: Now.AddMinutes(-10),
                    templeMissingEventId: 11,
                    templeMissingCreatedAt: Now.AddMinutes(-2))
            ],
            womOnlyCandidates:
            [
                New("vrll", requiredEventId: 20)
            ]));

        Assert.NotNull(result);
        Assert.Equal(10, result.WomMissingEventId);
        Assert.Equal(11, result.TempleMissingEventId);
    }

    [Fact]
    public void Detect_DoesNotCollapseMultipleOldCandidates()
    {
        var result = TempleNameChangeDetector.Detect(Input(
            oldCandidates:
            [
                Old("Chrille", womMissingEventId: 10, womMissingCreatedAt: Now.AddMinutes(-5)),
                Old("Other Astral", womMissingEventId: 11, womMissingCreatedAt: Now.AddMinutes(-4))
            ],
            womOnlyCandidates:
            [
                New("vrll", requiredEventId: 20)
            ]));

        Assert.Null(result);
    }

    [Fact]
    public void Detect_DoesNotCollapseMultipleNewCandidates()
    {
        var result = TempleNameChangeDetector.Detect(Input(
            oldCandidates:
            [
                Old("Chrille", womMissingEventId: 10, womMissingCreatedAt: Now.AddMinutes(-5))
            ],
            womOnlyCandidates:
            [
                New("vrll", requiredEventId: 20),
                New("second", requiredEventId: 21)
            ]));

        Assert.Null(result);
    }

    [Fact]
    public void Detect_DoesNotCollapseIncompatibleRanks()
    {
        var result = TempleNameChangeDetector.Detect(Input(
            oldCandidates:
            [
                Old("Chrille", currentRank: "Captain", womMissingEventId: 10, womMissingCreatedAt: Now.AddMinutes(-5))
            ],
            womOnlyCandidates:
            [
                New("vrll", womRole: "astral", requiredEventId: 20)
            ]));

        Assert.Null(result);
    }

    [Fact]
    public void Detect_DoesNotCollapseWhenMergeReviewAlreadyExists()
    {
        var result = TempleNameChangeDetector.Detect(Input(
            oldCandidates:
            [
                Old("Chrille", womMissingEventId: 10, womMissingCreatedAt: Now.AddMinutes(-5))
            ],
            womOnlyCandidates:
            [
                New("vrll", requiredEventId: 20)
            ],
            openMerges:
            [
                new TempleNameChangeOpenMerge("Chrille", "vrll")
            ]));

        Assert.Null(result);
    }

    [Fact]
    public void Detect_DoesNotImmediatelyRecreateDeclinedPair()
    {
        var result = TempleNameChangeDetector.Detect(Input(
            oldCandidates:
            [
                Old("Chrille", womMissingEventId: 10, womMissingCreatedAt: Now.AddMinutes(-5))
            ],
            womOnlyCandidates:
            [
                New("vrll", requiredEventId: 20)
            ],
            handledPairs:
            [
                new TempleNameChangeHandledPair("Chrille", "vrll", "decline", Now)
            ]));

        Assert.Null(result);
    }

    private static TempleNameChangeDetectionInput Input(
        IReadOnlyList<TempleNameChangeOldCandidate> oldCandidates,
        IReadOnlyList<TempleNameChangeWomOnlyCandidate> womOnlyCandidates,
        IReadOnlyList<TempleNameChangeOpenMerge>? openMerges = null,
        IReadOnlyList<TempleNameChangeHandledPair>? handledPairs = null)
    {
        return new TempleNameChangeDetectionInput(
            Now,
            Window,
            oldCandidates,
            womOnlyCandidates,
            openMerges ?? [],
            handledPairs ?? []);
    }

    private static TempleNameChangeOldCandidate Old(
        string username,
        string currentRank = "Astral",
        int? womMissingEventId = null,
        DateTimeOffset? womMissingCreatedAt = null,
        int? templeMissingEventId = null,
        DateTimeOffset? templeMissingCreatedAt = null)
    {
        return new TempleNameChangeOldCandidate(
            PlayerId: username.GetHashCode(),
            Username: username,
            CurrentRank: currentRank,
            WomMissingEventId: womMissingEventId,
            WomMissingCreatedAt: womMissingCreatedAt,
            TempleMissingEventId: templeMissingEventId,
            TempleMissingCreatedAt: templeMissingCreatedAt);
    }

    private static TempleNameChangeWomOnlyCandidate New(
        string username,
        string womRole = "astral",
        int requiredEventId = 20,
        DateTimeOffset? createdAt = null)
    {
        return new TempleNameChangeWomOnlyCandidate(
            requiredEventId,
            username,
            womRole,
            createdAt ?? Now.AddMinutes(-3));
    }
}
