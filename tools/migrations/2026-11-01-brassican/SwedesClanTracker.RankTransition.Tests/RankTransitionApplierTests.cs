using Microsoft.EntityFrameworkCore;
using SwedesClanTracker.Core;
using SwedesClanTracker.RankTransition;

namespace SwedesClanTracker.RankTransition.Tests;

public sealed class RankTransitionApplierTests
{
    [Fact]
    public async Task Apply_is_resumable_and_second_run_sees_already_target_without_remapping()
    {
        await using var db = CreateDb();
        var player = new Player { Id = 1, Username = "Retry", CurrentRank = "Officer", EligibleRank = "Officer", Status = PlayerStatus.ACTIVE, LastSeen = DateTimeOffset.UtcNow };
        db.Players.Add(player);
        await db.SaveChangesAsync();
        var operation = Operation(TransitionOperationKind.DatabaseCurrentRank, "1", "CurrentRank", "Officer", "Brassican");
        var manifest = Manifest(operation);
        var path = Path.Combine(Path.GetTempPath(), $"rank-transition-{Guid.NewGuid():N}.json.journal.json");
        try
        {
            var applier = new RankTransitionApplier(db, new FakeWom(), null);
            var first = await applier.ApplyAsync(manifest, path, Gates());
            var second = await applier.ApplyAsync(manifest, path, Gates());

            Assert.True(first.Completed);
            Assert.True(second.Completed);
            Assert.Equal("Brassican", player.CurrentRank);
            Assert.Equal(TransitionOperationStatus.Applied, first.Results.Single(x => x.OperationId == operation.OperationId).Status);
            Assert.Contains(second.Results, x => x.OperationId == operation.OperationId && x.Status == TransitionOperationStatus.Applied);
            Assert.Contains(db.LifecycleEvents, x => x.EventType == "RANK_TRANSITION_POLICY_MARKER");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Apply_recovers_committed_approved_history_when_finalizer_checkpoint_is_missing()
    {
        await using var db = CreateDb();
        db.Players.Add(new Player
        {
            Id = 1,
            Username = "FinalizerRecovery",
            CurrentRank = "Officer",
            EligibleRank = "Officer",
            Status = PlayerStatus.ACTIVE,
            LastSeen = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var operation = Operation(
            TransitionOperationKind.FinalizeApprovedPromotion,
            "1",
            "CurrentRank",
            "Officer",
            "Commander");
        var manifest = new RankTransitionManifest
        {
            ManifestId = Guid.NewGuid().ToString("N"),
            FrozenMappingVersion = FrozenRankMapping.Version,
            FrozenMapping = FrozenRankMapping.Document(),
            ApprovedForCutover = true,
            Operations = [operation],
            Warnings = []
        };
        manifest.IntegritySha256 = RankTransitionJson.ComputeIntegrity(manifest);
        var path = Path.Combine(Path.GetTempPath(), $"rank-transition-{Guid.NewGuid():N}.json.journal.json");
        try
        {
            var applier = new RankTransitionApplier(db, new FakeWom(), null);
            var first = await applier.ApplyAsync(manifest, path, Gates());
            Assert.True(first.Completed, first.Failure);
            var owned = await db.PromotionCandidates.AsNoTracking().SingleAsync();
            Assert.Equal(PromotionStatus.APPROVED, owned.Status);

            var journalStore = new RankTransitionJournalStore();
            var journal = await journalStore.LoadOrCreateAsync(path, manifest);
            journal.Results = journal.Results
                .Where(result => result.OperationId != operation.OperationId)
                .ToArray();
            await journalStore.SaveAsync(path, journal);

            var resumed = await applier.ApplyAsync(manifest, path, Gates());

            Assert.True(resumed.Completed, resumed.Failure);
            Assert.Contains(resumed.Results, result =>
                result.OperationId == operation.OperationId &&
                result.Status == TransitionOperationStatus.AlreadyApplied &&
                result.RelatedEntityId == owned.Id);
            Assert.Single(await db.PromotionCandidates.AsNoTracking().ToListAsync());
            Assert.Equal(PromotionStatus.APPROVED,
                (await db.PromotionCandidates.AsNoTracking().SingleAsync()).Status);
            Assert.Equal("Commander", (await db.Players.AsNoTracking().SingleAsync()).CurrentRank);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists($"{path}.lock")) File.Delete($"{path}.lock");
        }
    }

    [Fact]
    public async Task Apply_fails_closed_on_drift_and_does_not_partially_commit_other_rank_changes()
    {
        await using var db = CreateDb();
        db.Players.AddRange(
            new Player { Id = 1, Username = "First", CurrentRank = "Officer", EligibleRank = "Officer", Status = PlayerStatus.ACTIVE, LastSeen = DateTimeOffset.UtcNow },
            new Player { Id = 2, Username = "Drifted", CurrentRank = "Commander", EligibleRank = "Commander", Status = PlayerStatus.ACTIVE, LastSeen = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var operations = new[]
        {
            Operation(TransitionOperationKind.DatabaseCurrentRank, "1", "CurrentRank", "Officer", "Brassican"),
            Operation(TransitionOperationKind.DatabaseCurrentRank, "2", "CurrentRank", "Officer", "Brassican")
        };
        var manifest = Manifest(operations);
        var path = Path.Combine(Path.GetTempPath(), $"rank-transition-{Guid.NewGuid():N}.json.journal.json");
        try
        {
            var report = await new RankTransitionApplier(db, new FakeWom(), null).ApplyAsync(manifest, path, Gates());

            Assert.False(report.Completed);
            Assert.Contains("drifted", report.Failure!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Officer", (await db.Players.FindAsync(1))!.CurrentRank);
            Assert.Equal("Commander", (await db.Players.FindAsync(2))!.CurrentRank);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Rollback_reconciles_external_target_when_apply_lost_checkpoint_after_remote_success()
    {
        await using var db = CreateDb();
        var operation = Operation(TransitionOperationKind.DiscordRoleRename, "role-1", "Name", "General", "Colonel", external: true);
        var gateway = new FaultingDiscordGateway("General", throwAfterFirstRename: true);
        var manifest = Manifest(operation);
        var path = Path.Combine(Path.GetTempPath(), $"rank-transition-{Guid.NewGuid():N}.json.journal.json");
        try
        {
            var applier = new RankTransitionApplier(db, new FakeWom(), gateway);
            var apply = await applier.ApplyAsync(manifest, path, Gates());
            Assert.False(apply.Completed);
            Assert.Equal("Colonel", gateway.Name);

            // Simulate a process loss after the remote mutation but before the
            // final durable result checkpoint: retain only write-ahead intent.
            var journalStore = new RankTransitionJournalStore();
            var journal = await journalStore.LoadOrCreateAsync(path, manifest);
            journal.Results = journal.Results
                .Select(x => x with { Status = TransitionOperationStatus.Planned, Details = "recovered write-ahead intent" })
                .ToArray();
            await journalStore.SaveAsync(path, journal);
            var resumed = await applier.ApplyAsync(manifest, path, Gates());
            Assert.True(resumed.Completed);
            Assert.Contains(resumed.Results, x => x.OperationId == operation.OperationId && x.Status == TransitionOperationStatus.Applied);

            var rollback = await applier.RollbackAsync(manifest, path, Gates());

            Assert.True(rollback.Completed);
            Assert.Equal("General", gateway.Name);
            Assert.Contains(rollback.Results, x => x.OperationId == operation.OperationId && x.Status == TransitionOperationStatus.RolledBack);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Rollback_dismisses_the_exact_generated_candidate_and_not_a_same_rank_decoy()
    {
        await using var db = CreateDb();
        var snapshot = new PlayerSnapshot
        {
            Id = 7,
            PlayerId = 1,
            Timestamp = DateTimeOffset.UtcNow,
            Ehp = 1750
        };
        var player = new Player
        {
            Id = 1,
            Username = "CandidateOwner",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = PlayerStatus.ACTIVE,
            LastSeen = snapshot.Timestamp,
            LastSynced = snapshot.Timestamp,
            Snapshots = [snapshot]
        };
        snapshot.Player = player;
        db.Players.Add(player);
        await db.SaveChangesAsync();

        var operation = Operation(TransitionOperationKind.QueuePromotionCandidate, "1", "PromotionCandidate", null!, "General") with
        {
            SnapshotId = snapshot.Id,
            SnapshotHash = RankTransitionPlanner.SnapshotHash(snapshot)
        };
        var manifest = Manifest(operation);
        var path = Path.Combine(Path.GetTempPath(), $"rank-transition-{Guid.NewGuid():N}.json.journal.json");
        try
        {
            var applier = new RankTransitionApplier(db, new FakeWom(), null);
            var applied = await applier.ApplyAsync(manifest, path, Gates());
            Assert.True(applied.Completed, applied.Failure);
            var generated = await db.PromotionCandidates.SingleAsync();

            var decoy = new PromotionCandidate
            {
                PlayerId = player.Id,
                Player = player,
                OldRank = "Brassican",
                NewRank = "General",
                Reason = "unrelated pending candidate",
                Status = PromotionStatus.PENDING,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.PromotionCandidates.Add(decoy);
            await db.SaveChangesAsync();

            var rollback = await applier.RollbackAsync(manifest, path, Gates());

            Assert.True(rollback.Completed, rollback.Failure);
            db.ChangeTracker.Clear();
            Assert.Equal(PromotionStatus.DISMISSED, (await db.PromotionCandidates.AsNoTracking().SingleAsync(x => x.Id == generated.Id)).Status);
            Assert.Equal(PromotionStatus.PENDING, (await db.PromotionCandidates.AsNoTracking().SingleAsync(x => x.Id == decoy.Id)).Status);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Verify_after_rollback_uses_exact_generated_candidate_when_old_same_rank_row_is_restored()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new PlayerSnapshot
        {
            Id = 8,
            PlayerId = 1,
            Timestamp = now,
            Ehp = 2500
        };
        var player = new Player
        {
            Id = 1,
            Username = "SameRankHistory",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = PlayerStatus.ACTIVE,
            LastSeen = now,
            LastSynced = now,
            Snapshots = [snapshot]
        };
        snapshot.Player = player;
        db.Players.Add(player);
        var oldPending = new PromotionCandidate
        {
            PlayerId = player.Id,
            Player = player,
            OldRank = "Brassican",
            NewRank = "Brigadier",
            Reason = "old policy candidate",
            CandidateType = PromotionCandidateType.unknown_wom_role,
            Status = PromotionStatus.PENDING,
            CreatedAt = now
        };
        db.PromotionCandidates.Add(oldPending);
        await db.SaveChangesAsync();

        var supersede = Operation(
            TransitionOperationKind.SupersedePromotionCandidate,
            oldPending.Id.ToString(),
            "Status",
            PromotionStatus.PENDING.ToString(),
            PromotionStatus.DISMISSED.ToString());
        var queue = Operation(
            TransitionOperationKind.QueuePromotionCandidate,
            "1",
            "PromotionCandidate",
            null!,
            "Brigadier") with
        {
            SnapshotId = snapshot.Id,
            SnapshotHash = RankTransitionPlanner.SnapshotHash(snapshot)
        };
        var manifest = Manifest(supersede, queue);
        var path = Path.Combine(Path.GetTempPath(), $"rank-transition-{Guid.NewGuid():N}.json.journal.json");
        try
        {
            var applier = new RankTransitionApplier(db, new FakeWom(), null);
            var applied = await applier.ApplyAsync(manifest, path, Gates());
            Assert.True(applied.Completed, applied.Failure);
            var allCandidates = await db.PromotionCandidates.ToListAsync();
            Assert.True(
                allCandidates.Any(x => x.Status == PromotionStatus.PENDING),
                $"Results: {string.Join(", ", applied.Results.Select(x => $"{x.OperationId}:{x.Status}:{x.Details}"))}; candidates: {string.Join(", ", allCandidates.Select(x => $"{x.Id}:{x.Status}:{x.NewRank}"))}");
            var generated = allCandidates.Single(x => x.Status == PromotionStatus.PENDING);
            Assert.NotEqual(oldPending.Id, generated.Id);

            var rollback = await applier.RollbackAsync(manifest, path, Gates());
            Assert.True(rollback.Completed, rollback.Failure);
            db.ChangeTracker.Clear();
            Assert.Equal(PromotionStatus.PENDING, (await db.PromotionCandidates.AsNoTracking().SingleAsync(x => x.Id == oldPending.Id)).Status);
            Assert.Equal(PromotionStatus.DISMISSED, (await db.PromotionCandidates.AsNoTracking().SingleAsync(x => x.Id == generated.Id)).Status);

            var verification = await new RankTransitionVerifier(db, new FakeWom(), null)
                .VerifyAsync(manifest, path);
            Assert.True(verification.SafeToProceed, string.Join("; ", verification.Items.Select(x => x.Details)));
            Assert.Contains(verification.Items, x =>
                x.OperationId == queue.OperationId &&
                x.Status == TransitionOperationStatus.AlreadyRolledBack);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Apply_rejects_a_different_manifest_after_the_policy_marker_without_writing()
    {
        await using var db = CreateDb();
        db.Players.Add(new Player
        {
            Id = 1,
            Username = "MarkerOwner",
            CurrentRank = "Officer",
            EligibleRank = "Officer",
            Status = PlayerStatus.ACTIVE,
            LastSeen = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var firstManifest = Manifest(Operation(TransitionOperationKind.DatabaseCurrentRank, "1", "CurrentRank", "Officer", "Brassican"));
        var firstPath = Path.Combine(Path.GetTempPath(), $"rank-transition-{Guid.NewGuid():N}.journal.json");
        var secondPath = Path.Combine(Path.GetTempPath(), $"rank-transition-{Guid.NewGuid():N}.journal.json");
        try
        {
            var applier = new RankTransitionApplier(db, new FakeWom(), null);
            var first = await applier.ApplyAsync(firstManifest, firstPath, Gates());
            Assert.True(first.Completed, first.Failure);

            var secondManifest = Manifest(Operation(TransitionOperationKind.DatabaseCurrentRank, "1", "CurrentRank", "Brassican", "Commander"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                applier.ApplyAsync(secondManifest, secondPath, Gates()));

            db.ChangeTracker.Clear();
            Assert.Equal("Brassican", (await db.Players.AsNoTracking().SingleAsync()).CurrentRank);
        }
        finally
        {
            if (File.Exists(firstPath)) File.Delete(firstPath);
            if (File.Exists(secondPath)) File.Delete(secondPath);
            if (File.Exists($"{firstPath}.lock")) File.Delete($"{firstPath}.lock");
            if (File.Exists($"{secondPath}.lock")) File.Delete($"{secondPath}.lock");
        }
    }

    [Fact]
    public async Task Apply_resume_rejects_deleted_owned_candidate_even_when_an_unowned_same_rank_row_exists()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new PlayerSnapshot
        {
            Id = 9,
            PlayerId = 1,
            Timestamp = now,
            Ehp = 1750
        };
        var player = new Player
        {
            Id = 1,
            Username = "CandidateDrift",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = PlayerStatus.ACTIVE,
            LastSeen = now,
            LastSynced = now,
            Snapshots = [snapshot]
        };
        snapshot.Player = player;
        db.Players.Add(player);
        await db.SaveChangesAsync();

        var queue = Operation(TransitionOperationKind.QueuePromotionCandidate, "1", "PromotionCandidate", null!, "General") with
        {
            SnapshotId = snapshot.Id,
            SnapshotHash = RankTransitionPlanner.SnapshotHash(snapshot)
        };
        var external = Operation(TransitionOperationKind.WiseOldManRole, "ExternalMember", "Role", "Officer", "Brassican", external: true);
        var manifest = Manifest(queue, external);
        var path = Path.Combine(Path.GetTempPath(), $"rank-transition-{Guid.NewGuid():N}.journal.json");
        var wom = new BlockingWom();
        try
        {
            var applier = new RankTransitionApplier(db, wom, null);
            var first = await applier.ApplyAsync(manifest, path, Gates());
            Assert.False(first.Completed);
            var generated = await db.PromotionCandidates.SingleAsync();

            db.PromotionCandidates.Remove(generated);
            db.PromotionCandidates.Add(new PromotionCandidate
            {
                PlayerId = player.Id,
                Player = player,
                OldRank = "Brassican",
                NewRank = "General",
                Reason = "unowned replacement",
                CandidateType = PromotionCandidateType.unknown_wom_role,
                Status = PromotionStatus.PENDING,
                CreatedAt = now.AddSeconds(1)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var resumed = await applier.ApplyAsync(manifest, path, Gates());

            Assert.False(resumed.Completed);
            Assert.Contains("Exact generated promotion candidate", resumed.Failure!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, wom.UpdateCount);
            Assert.Equal(2, wom.FreshReadCount);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists($"{path}.lock")) File.Delete($"{path}.lock");
        }
    }

    [Fact]
    public void Apply_requires_all_human_review_gates()
    {
        var invalidGates = Gates() with { VerifiedBackupConfirmed = false };
        var ex = Assert.Throws<InvalidOperationException>(() => invalidGates.Validate(Manifest()));
        Assert.Contains("backup", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ApplyPrerequisites Gates() => new("tester", true, true, true);

    private static RankTransitionManifest Manifest(params RankTransitionOperation[] operations)
    {
        var manifest = new RankTransitionManifest
        {
            ManifestId = Guid.NewGuid().ToString("N"),
            FrozenMappingVersion = FrozenRankMapping.Version,
            FrozenMapping = FrozenRankMapping.Document(),
            Operations = operations,
            Warnings = []
        };
        manifest.IntegritySha256 = RankTransitionJson.ComputeIntegrity(manifest);
        return manifest;
    }

    private static RankTransitionOperation Operation(TransitionOperationKind kind, string target, string property, string before, string after, bool external = false) =>
        new(
            RankTransitionPlanner.ComputeOperationId(kind, target, before, after),
            kind,
            target,
            property,
            before,
            after,
            external,
            true,
            false,
            true,
            null,
            null,
            null,
            "test");

    private static TrackerDbContext CreateDb() => new(new DbContextOptionsBuilder<TrackerDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    private sealed class FakeWom : IWiseOldManClient
    {
        public Task<string?> GetMemberRoleAsync(string username, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<string?> GetMemberRoleFreshAsync(string username, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<IReadOnlyDictionary<string, string>> GetMemberRolesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task<bool> IsImpAccountAsync(string username, CancellationToken ct) => Task.FromResult(false);
        public Task<WomRoleUpdateResult> UpdateMemberRoleAsync(string username, string role, CancellationToken ct, bool invalidateCache = true) => Task.FromResult(new WomRoleUpdateResult(true, 200, "ok", role, null, null));
        public Task<(bool Success, string Details)> RemoveMemberAsync(string username, CancellationToken ct) => Task.FromResult((true, "ok"));
        public Task InvalidateCacheAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class BlockingWom : IWiseOldManClient
    {
        public int FreshReadCount { get; private set; }
        public int UpdateCount { get; private set; }

        public Task<string?> GetMemberRoleAsync(string username, CancellationToken ct) =>
            GetMemberRoleFreshAsync(username, ct);

        public Task<string?> GetMemberRoleFreshAsync(string username, CancellationToken ct)
        {
            FreshReadCount++;
            if (FreshReadCount == 1) return Task.FromResult<string?>("Officer");
            throw new HttpRequestException("simulated external read interruption");
        }

        public Task<IReadOnlyDictionary<string, string>> GetMemberRolesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task<bool> IsImpAccountAsync(string username, CancellationToken ct) => Task.FromResult(false);

        public Task<WomRoleUpdateResult> UpdateMemberRoleAsync(
            string username,
            string role,
            CancellationToken ct,
            bool invalidateCache = true)
        {
            UpdateCount++;
            return Task.FromResult(new WomRoleUpdateResult(true, 200, "ok", role, null, null));
        }

        public Task<(bool Success, string Details)> RemoveMemberAsync(string username, CancellationToken ct) =>
            Task.FromResult((true, "ok"));

        public Task InvalidateCacheAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FaultingDiscordGateway(string name, bool throwAfterFirstRename) : IDiscordRoleGateway
    {
        private bool _thrown;
        public string Name { get; private set; } = name;

        public Task<DiscordGuildObservation?> GetGuildObservationAsync(CancellationToken ct) =>
            Task.FromResult<DiscordGuildObservation?>(new DiscordGuildObservation("guild", [new DiscordRoleObservation("role-1", Name, null, "1", 1, 2, false, null)], true, null));

        public Task<DiscordRoleMutationResult> RenameRoleAsync(string roleId, string expectedName, string targetName, CancellationToken ct)
        {
            if (!string.Equals(Name, expectedName, StringComparison.Ordinal)) return Task.FromResult(new DiscordRoleMutationResult(false, 409, "drift", null));
            Name = targetName;
            if (throwAfterFirstRename && !_thrown)
            {
                _thrown = true;
                throw new HttpRequestException("connection lost after remote success");
            }
            return Task.FromResult(new DiscordRoleMutationResult(true, 200, "ok", null));
        }
    }
}
