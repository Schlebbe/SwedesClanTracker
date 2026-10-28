using Microsoft.EntityFrameworkCore;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.Core.Tests;

public sealed class PromotionApprovalServiceTests
{
    [Fact]
    public async Task Database_only_approval_does_not_call_wom()
    {
        await using var db = CreateDbContext();
        var (player, candidate) = AddPromotion(db, PromotionCandidateType.wom_already_at_new_rank, "Officer");
        var wom = new FakeWiseOldManClient();
        var service = new PromotionApprovalService(db, wom);

        var result = await service.ApproveAsync(candidate, player, "tester", "test", CancellationToken.None);

        Assert.True(result.Approved);
        Assert.Equal(0, wom.GetMemberRoleCalls);
        Assert.Equal(0, wom.UpdateMemberRoleCalls);
        Assert.Equal(PromotionStatus.APPROVED, candidate.Status);
        Assert.Equal("Officer", player.CurrentRank);
        await db.SaveChangesAsync();
        Assert.Contains(db.LifecycleEvents, x =>
            x.EventType == "WOM_ROLE_UPDATE_APPLIED" &&
            x.MetadataJson.Contains("\"Skipped\":true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Known_wom_update_approval_sends_direct_put_without_role_lookup()
    {
        await using var db = CreateDbContext();
        var (player, candidate) = AddPromotion(db, PromotionCandidateType.needs_wom_rank_update, "Member");
        var wom = new FakeWiseOldManClient
        {
            UpdateResult = new WomRoleUpdateResult(true, 200, "updated", "Officer", 123, "Gynofobi")
        };
        var service = new PromotionApprovalService(db, wom);

        var result = await service.ApproveAsync(candidate, player, "tester", "test", CancellationToken.None);

        Assert.True(result.Approved);
        Assert.Equal(0, wom.GetMemberRoleCalls);
        Assert.Equal(1, wom.UpdateMemberRoleCalls);
        Assert.Equal(PromotionStatus.APPROVED, candidate.Status);
        Assert.Equal("Officer", player.CurrentRank);
    }

    [Fact]
    public async Task Unknown_candidate_is_classified_lazily_as_database_only()
    {
        await using var db = CreateDbContext();
        var (player, candidate) = AddPromotion(db, PromotionCandidateType.unknown_wom_role, null);
        var wom = new FakeWiseOldManClient { Role = "Officer" };
        var service = new PromotionApprovalService(db, wom);

        var result = await service.ApproveAsync(candidate, player, "tester", "test", CancellationToken.None);

        Assert.True(result.Approved);
        Assert.Equal(1, wom.GetMemberRoleCalls);
        Assert.Equal(0, wom.UpdateMemberRoleCalls);
        Assert.Equal(PromotionCandidateType.wom_already_at_new_rank, candidate.CandidateType);
        Assert.Equal("Officer", candidate.WomRoleAtEvaluation);
    }

    [Fact]
    public async Task Failed_wom_update_leaves_candidate_and_database_rank_unchanged()
    {
        await using var db = CreateDbContext();
        var (player, candidate) = AddPromotion(db, PromotionCandidateType.needs_wom_rank_update, "Member");
        var wom = new FakeWiseOldManClient
        {
            UpdateResult = new WomRoleUpdateResult(false, 0, "timeout", null, null, null)
        };
        var service = new PromotionApprovalService(db, wom);

        var result = await service.ApproveAsync(candidate, player, "tester", "test", CancellationToken.None);

        Assert.False(result.Approved);
        Assert.True(result.Retryable);
        Assert.Equal(0, wom.GetMemberRoleCalls);
        Assert.Equal(1, wom.UpdateMemberRoleCalls);
        Assert.Equal(PromotionStatus.PENDING, candidate.Status);
        Assert.Equal("Recruit", player.CurrentRank);
    }

    [Fact]
    public async Task Unknown_special_wom_role_stays_pending_without_role_update()
    {
        await using var db = CreateDbContext();
        var (player, candidate) = AddPromotion(db, PromotionCandidateType.unknown_wom_role, null);
        var wom = new FakeWiseOldManClient { Role = "Wily" };
        var service = new PromotionApprovalService(db, wom);

        var result = await service.ApproveAsync(candidate, player, "tester", "test", CancellationToken.None);

        Assert.False(result.Approved);
        Assert.False(result.Retryable);
        Assert.Equal(1, wom.GetMemberRoleCalls);
        Assert.Equal(0, wom.UpdateMemberRoleCalls);
        Assert.Equal(PromotionStatus.PENDING, candidate.Status);
        Assert.Equal(PromotionCandidateType.unknown_wom_role, candidate.CandidateType);
    }

    private static (Player Player, PromotionCandidate Candidate) AddPromotion(
        TrackerDbContext db,
        PromotionCandidateType candidateType,
        string? womRole)
    {
        var player = new Player
        {
            Username = "Gynofobi",
            CurrentRank = "Recruit",
            EligibleRank = "Officer",
            Status = PlayerStatus.ACTIVE,
            LastSeen = DateTimeOffset.UtcNow
        };
        var candidate = new PromotionCandidate
        {
            Player = player,
            PlayerId = player.Id,
            OldRank = "Recruit",
            NewRank = "Officer",
            Reason = "Officer via: Total level >= 2100",
            CandidateType = candidateType,
            WomRoleAtEvaluation = womRole,
            Status = PromotionStatus.PENDING,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Players.Add(player);
        db.PromotionCandidates.Add(candidate);
        db.SaveChanges();
        return (player, candidate);
    }

    private static TrackerDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<TrackerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TrackerDbContext(options);
    }

    private sealed class FakeWiseOldManClient : IWiseOldManClient
    {
        public string? Role { get; init; }
        public WomRoleUpdateResult UpdateResult { get; init; } = new(true, 200, "updated", "Officer", null, null);
        public int GetMemberRoleCalls { get; private set; }
        public int UpdateMemberRoleCalls { get; private set; }

        public Task<string?> GetMemberRoleAsync(string username, CancellationToken ct)
        {
            GetMemberRoleCalls++;
            return Task.FromResult(Role);
        }

        public Task<IReadOnlyDictionary<string, string>> GetMemberRolesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task<bool> IsImpAccountAsync(string username, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<WomRoleUpdateResult> UpdateMemberRoleAsync(
            string username,
            string role,
            CancellationToken ct,
            bool invalidateCache = true)
        {
            UpdateMemberRoleCalls++;
            return Task.FromResult(UpdateResult);
        }

        public Task<(bool Success, string Details)> RemoveMemberAsync(string username, CancellationToken ct) =>
            Task.FromResult((true, "ok"));

        public Task InvalidateCacheAsync(CancellationToken ct) =>
            Task.CompletedTask;
    }
}
