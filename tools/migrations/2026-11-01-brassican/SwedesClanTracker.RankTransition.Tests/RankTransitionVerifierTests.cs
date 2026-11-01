using Microsoft.EntityFrameworkCore;
using SwedesClanTracker.Core;
using SwedesClanTracker.RankTransition;

namespace SwedesClanTracker.RankTransition.Tests;

public sealed class RankTransitionVerifierTests
{
    [Fact]
    public async Task Verify_keeps_a_newly_queued_canonical_general_as_general()
    {
        await using var db = new TrackerDbContext(new DbContextOptionsBuilder<TrackerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var player = new Player
        {
            Id = 1,
            Username = "CanonicalGeneral",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = PlayerStatus.ACTIVE,
            LastSeen = DateTimeOffset.UtcNow
        };
        db.Players.Add(player);
        db.PromotionCandidates.Add(new PromotionCandidate
        {
            Id = 42,
            PlayerId = player.Id,
            Player = player,
            OldRank = "Brassican",
            NewRank = "General",
            Reason = "current evaluator",
            CandidateType = PromotionCandidateType.unknown_wom_role,
            Status = PromotionStatus.PENDING,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var operation = new RankTransitionOperation(
            RankTransitionPlanner.ComputeOperationId(
                TransitionOperationKind.QueuePromotionCandidate,
                "1",
                null,
                "General"),
            TransitionOperationKind.QueuePromotionCandidate,
            "1",
            "PromotionCandidate",
            null,
            "General",
            IsExternal: false,
            IsActive: true,
            Protected: false,
            RequiresReview: true,
            SnapshotId: null,
            SnapshotHash: null,
            LastSyncedAtUtc: null,
            Reason: "test");
        var manifest = new RankTransitionManifest
        {
            ManifestId = Guid.NewGuid().ToString("N"),
            FrozenMappingVersion = FrozenRankMapping.Version,
            FrozenMapping = FrozenRankMapping.Document(),
            Players =
            [
                new RankTransitionPlayerObservation(
                    player.Id,
                    player.Username,
                    player.Status.ToString(),
                    true,
                    "Brassican",
                    "Brassican",
                    "Brassican",
                    "General",
                    null,
                    null,
                    null,
                    null,
                    "General",
                    "test",
                    false,
                    null)
            ],
            Operations = [operation]
        };
        manifest.IntegritySha256 = RankTransitionJson.ComputeIntegrity(manifest);
        var journalPath = Path.Combine(Path.GetTempPath(), $"rank-transition-{Guid.NewGuid():N}.journal.json");
        try
        {
            var journal = new RankTransitionJournal
            {
                ManifestId = manifest.ManifestId,
                ManifestIntegritySha256 = manifest.IntegritySha256,
                Results =
                [
                    new RankTransitionOperationResult(
                        operation.OperationId,
                        TransitionOperationStatus.Applied,
                        DateTimeOffset.UtcNow,
                        null,
                        "General",
                        "test",
                        RelatedEntityId: 42)
                ]
            };
            await new RankTransitionJournalStore().SaveAsync(journalPath, journal);
            var report = await new RankTransitionVerifier(db, new EmptyWom(), null)
                .VerifyAsync(manifest, journalPath);

            var item = Assert.Single(report.Items);
            Assert.Equal(TransitionOperationStatus.AlreadyApplied, item.Status);
            Assert.Equal("General", item.ObservedValue);
            Assert.True(report.SafeToProceed);
        }
        finally
        {
            if (File.Exists(journalPath)) File.Delete(journalPath);
            if (File.Exists($"{journalPath}.lock")) File.Delete($"{journalPath}.lock");
        }
    }

    [Fact]
    public async Task Verify_rejects_a_role_with_name_correct_but_changed_permissions_or_members()
    {
        await using var db = new TrackerDbContext(new DbContextOptionsBuilder<TrackerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var operation = new RankTransitionOperation(
            RankTransitionPlanner.ComputeOperationId(
                TransitionOperationKind.DiscordRoleRename,
                "role-1",
                "General",
                "Colonel"),
            TransitionOperationKind.DiscordRoleRename,
            "role-1",
            "Name",
            "General",
            "Colonel",
            IsExternal: true,
            IsActive: true,
            Protected: false,
            RequiresReview: true,
            SnapshotId: null,
            SnapshotHash: null,
            LastSyncedAtUtc: null,
            Reason: "test");
        var manifest = new RankTransitionManifest
        {
            ManifestId = Guid.NewGuid().ToString("N"),
            FrozenMappingVersion = FrozenRankMapping.Version,
            FrozenMapping = FrozenRankMapping.Document(),
            DiscordRoles =
            [
                new DiscordRoleObservation(
                    "role-1",
                    "General",
                    "Colonel",
                    "1",
                    4,
                    2,
                    false,
                    null,
                    ["member-a", "member-b"],
                    "members-hash")
            ],
            Operations = [operation]
        };

        var actual = new DiscordRoleObservation(
            "role-1",
            "Colonel",
            null,
            "2",
            4,
            2,
            false,
            null,
            ["member-a", "member-c"],
            "members-hash");
        var report = await new RankTransitionVerifier(db, new EmptyWom(), new FixedDiscord(actual))
            .VerifyAsync(manifest);

        var item = Assert.Single(report.Items);
        Assert.Equal(TransitionOperationStatus.Drifted, item.Status);
        Assert.False(report.SafeToProceed);
        Assert.Contains("permissions", item.Details, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class EmptyWom : IWiseOldManClient
    {
        public Task<string?> GetMemberRoleAsync(string username, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<string?> GetMemberRoleFreshAsync(string username, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<IReadOnlyDictionary<string, string>> GetMemberRolesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task<bool> IsImpAccountAsync(string username, CancellationToken ct) => Task.FromResult(false);
        public Task<WomRoleUpdateResult> UpdateMemberRoleAsync(string username, string role, CancellationToken ct, bool invalidateCache = true) =>
            Task.FromResult(new WomRoleUpdateResult(true, 200, "ok", role, null, null));
        public Task<(bool Success, string Details)> RemoveMemberAsync(string username, CancellationToken ct) =>
            Task.FromResult((true, "ok"));
        public Task InvalidateCacheAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FixedDiscord(DiscordRoleObservation role) : IDiscordRoleGateway
    {
        public Task<DiscordGuildObservation?> GetGuildObservationAsync(CancellationToken ct) =>
            Task.FromResult<DiscordGuildObservation?>(new DiscordGuildObservation(
                "guild",
                [role],
                MembersFullyObserved: true,
                Notes: null));

        public Task<DiscordRoleMutationResult> RenameRoleAsync(
            string roleId,
            string expectedName,
            string targetName,
            CancellationToken ct) =>
            Task.FromResult(new DiscordRoleMutationResult(true, 200, "ok", role));
    }
}
