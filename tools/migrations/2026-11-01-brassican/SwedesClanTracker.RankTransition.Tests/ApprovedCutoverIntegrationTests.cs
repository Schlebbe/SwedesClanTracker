using Microsoft.EntityFrameworkCore;
using SwedesClanTracker.Core;
using SwedesClanTracker.RankTransition;

namespace SwedesClanTracker.RankTransition.Tests;

/// <summary>
/// In-memory end-to-end coverage for the explicitly approved promotion path.
/// The fakes model the frozen WOM and Discord observations closely enough to
/// exercise preflight, external verification, durable finalization, retry,
/// and owned rollback without contacting either service.
/// </summary>
public sealed class ApprovedCutoverIntegrationTests
{
    [Fact]
    public async Task Explicit_approval_cuts_Officer_to_Commander_without_a_transient_Brassican_WOM_role()
    {
        await using var scenario = await Scenario.CreateAsync();

        Assert.True(scenario.Manifest.ApprovedForCutover);
        Assert.Contains(scenario.Manifest.PromotionDecisions, x =>
            x.PlayerId == scenario.Player.Id &&
            x.ApprovedForCutover &&
            x.EvaluatedEligibleRank == "Commander");
        var playerObservation = scenario.Manifest.Players.Single();
        Assert.Equal("Officer", playerObservation.DatabaseCurrentRankBefore);
        Assert.Equal("Commander", playerObservation.DatabaseCurrentRankTarget);
        Assert.Equal("Commander", scenario.Manifest.WomObservations.Single().RoleTarget);
        Assert.Contains(scenario.Manifest.Operations, x =>
            x.Kind == TransitionOperationKind.WiseOldManRole &&
            x.Before == "Officer" &&
            x.After == "Commander");
        Assert.DoesNotContain(scenario.Manifest.Operations, x =>
            x.Kind == TransitionOperationKind.WiseOldManRole &&
            x.After == "Brassican");

        var applied = await scenario.ApplyAsync();

        Assert.True(applied.Completed, applied.Failure);
        scenario.Db.ChangeTracker.Clear();
        var player = await scenario.Db.Players.AsNoTracking().SingleAsync();
        Assert.Equal("Commander", player.CurrentRank);
        Assert.Equal("Commander", player.EligibleRank);
        Assert.Equal("Commander", scenario.Wom.Roles[scenario.Player.Username]);
        Assert.Equal(new[] { "200", "900" }, scenario.Discord.Member.RoleIds.OrderBy(x => x, StringComparer.Ordinal));
        Assert.DoesNotContain("Brassican", scenario.Wom.Roles.Values, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Approved_history_is_created_only_after_all_external_targets_verify_and_never_queues_pending()
    {
        await using var scenario = await Scenario.CreateAsync();
        var applied = await scenario.ApplyAsync();

        Assert.True(applied.Completed, applied.Failure);
        var candidates = await scenario.Db.PromotionCandidates.AsNoTracking().ToListAsync();
        var approved = Assert.Single(candidates);
        Assert.Equal(PromotionStatus.APPROVED, approved.Status);
        Assert.Equal("Commander", approved.NewRank);
        Assert.DoesNotContain(candidates, x => x.Status == PromotionStatus.PENDING);
        Assert.Contains(await scenario.Db.LifecycleEvents.AsNoTracking().ToListAsync(), x =>
            x.EventType == "RANK_TRANSITION_PROMOTION_APPROVED" &&
            x.MetadataJson.Contains("\"NewRank\":\"Commander\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failed_external_verification_does_not_create_approved_history_or_change_current_rank()
    {
        await using var scenario = await Scenario.CreateAsync(discordFailureCount: 1);

        var first = await scenario.ApplyAsync();

        Assert.False(first.Completed);
        scenario.Db.ChangeTracker.Clear();
        var player = await scenario.Db.Players.AsNoTracking().SingleAsync();
        Assert.Equal("Officer", player.CurrentRank);
        Assert.Equal("Commander", player.EligibleRank);
        Assert.Empty(await scenario.Db.PromotionCandidates.AsNoTracking().ToListAsync());
        Assert.DoesNotContain(await scenario.Db.LifecycleEvents.AsNoTracking().ToListAsync(), x =>
            x.EventType == "RANK_TRANSITION_PROMOTION_APPROVED");
        // WOM is allowed to have completed before the later Discord gate
        // failed; the irreversible database approval still did not happen.
        Assert.Equal("Commander", scenario.Wom.Roles[scenario.Player.Username]);
    }

    [Fact]
    public async Task Retry_after_owned_external_failure_is_idempotent_and_creates_one_approved_history()
    {
        await using var scenario = await Scenario.CreateAsync(discordFailureCount: 1);

        var first = await scenario.ApplyAsync();
        Assert.False(first.Completed);

        var second = await scenario.ApplyAsync();

        Assert.True(second.Completed, second.Failure);
        scenario.Db.ChangeTracker.Clear();
        var candidates = await scenario.Db.PromotionCandidates.AsNoTracking().ToListAsync();
        var approved = Assert.Single(candidates);
        Assert.Equal(PromotionStatus.APPROVED, approved.Status);
        Assert.Equal("Commander", (await scenario.Db.Players.AsNoTracking().SingleAsync()).CurrentRank);
        Assert.Equal(1, scenario.Discord.SuccessfulMemberMutations);
        Assert.Equal("Commander", scenario.Wom.Roles[scenario.Player.Username]);
    }

    [Fact]
    public async Task Rollback_restores_the_exact_owned_approved_row_and_leaves_an_unrelated_pending_row_untouched()
    {
        await using var scenario = await Scenario.CreateAsync();
        var applied = await scenario.ApplyAsync();
        Assert.True(applied.Completed, applied.Failure);

        var decoy = new PromotionCandidate
        {
            PlayerId = scenario.Player.Id,
            OldRank = "Brassican",
            NewRank = "Commander",
            Reason = "unrelated pending approval",
            CandidateType = PromotionCandidateType.needs_wom_rank_update,
            Status = PromotionStatus.PENDING,
            CreatedAt = scenario.Now.AddMinutes(1)
        };
        scenario.Db.PromotionCandidates.Add(decoy);
        await scenario.Db.SaveChangesAsync();
        var owned = await scenario.Db.PromotionCandidates.AsNoTracking()
            .SingleAsync(x => x.Status == PromotionStatus.APPROVED);

        var rollback = await scenario.RollbackAsync();

        Assert.True(rollback.Completed, rollback.Failure);
        scenario.Db.ChangeTracker.Clear();
        var player = await scenario.Db.Players.AsNoTracking().SingleAsync();
        Assert.Equal("Officer", player.CurrentRank);
        Assert.Equal("Officer", player.EligibleRank);
        Assert.Equal("Officer", scenario.Wom.Roles[scenario.Player.Username]);
        Assert.Equal(new[] { "100", "900" }, scenario.Discord.Member.RoleIds.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(PromotionStatus.DISMISSED, (await scenario.Db.PromotionCandidates.AsNoTracking().SingleAsync(x => x.Id == owned.Id)).Status);
        Assert.Equal(PromotionStatus.PENDING, (await scenario.Db.PromotionCandidates.AsNoTracking().SingleAsync(x => x.Id == decoy.Id)).Status);
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly string manifestPath;
        private readonly string journalPath;

        private Scenario(
            TrackerDbContext db,
            Player player,
            RecordingWom wom,
            RecordingDiscord discord,
            RankTransitionManifest manifest,
            DateTimeOffset now,
            string manifestPath,
            string journalPath)
        {
            Db = db;
            Player = player;
            Wom = wom;
            Discord = discord;
            Manifest = manifest;
            Now = now;
            this.manifestPath = manifestPath;
            this.journalPath = journalPath;
        }

        public TrackerDbContext Db { get; }
        public Player Player { get; }
        public RecordingWom Wom { get; }
        public RecordingDiscord Discord { get; }
        public RankTransitionManifest Manifest { get; }
        public DateTimeOffset Now { get; }

        public static async Task<Scenario> CreateAsync(int discordFailureCount = 0)
        {
            var now = DateTimeOffset.UtcNow;
            var db = new TrackerDbContext(new DbContextOptionsBuilder<TrackerDbContext>()
                .UseInMemoryDatabase($"approved-cutover-{Guid.NewGuid():N}")
                .Options);
            var snapshot = new PlayerSnapshot
            {
                Id = 10,
                PlayerId = 1,
                Timestamp = now,
                TotalLevel = 2200,
                Ehb = 0,
                Ehp = 0,
                Collections = 0,
                PetCount = 0
            };
            var player = new Player
            {
                Id = 1,
                Username = "Arvld",
                CurrentRank = "Officer",
                EligibleRank = "Officer",
                Status = PlayerStatus.ACTIVE,
                LastSeen = now,
                LastSynced = now,
                Snapshots = [snapshot]
            };
            snapshot.Player = player;
            db.Players.Add(player);
            await db.SaveChangesAsync();

            var wom = new RecordingWom(player.Username, "Officer");
            var discord = RecordingDiscord.Create(discordFailureCount);
            var environment = new TransitionEnvironmentIdentity("test-db", 7173, discord.GuildId);
            var planner = new RankTransitionPlanner(
                db,
                wom,
                discord,
                new FixedClock(now),
                environment);
            var manifest = await planner.PlanAsync(new RankTransitionPlannerOptions(
                IncludeDiscord: true,
                IncludeWom: true,
                DiscordRankRoleIds: RankRoleIds(),
                ApproveQualifyingPromotions: true));

            Assert.DoesNotContain(manifest.Warnings, warning => warning.BlocksApply);
            Assert.Contains(manifest.Operations, operation =>
                operation.Kind == TransitionOperationKind.DiscordMemberRankRole);
            Assert.Contains(manifest.Operations, operation =>
                operation.Kind == TransitionOperationKind.FinalizeApprovedPromotion);

            var manifestPath = Path.Combine(Path.GetTempPath(), $"approved-cutover-{Guid.NewGuid():N}.json");
            var journalPath = RankTransitionJournalStore.PathFor(manifestPath);
            var store = new RankTransitionManifestStore();
            await store.SaveAsync(manifestPath, manifest);
            var frozen = await store.LoadAndVerifyAsync(manifestPath);
            return new Scenario(db, player, wom, discord, frozen, now, manifestPath, journalPath);
        }

        public Task<RankTransitionApplyReport> ApplyAsync() =>
            new RankTransitionApplier(
                Db,
                Wom,
                Discord,
                clock: new FixedClock(Now),
                environment: new TransitionEnvironmentIdentity("test-db", 7173, Discord.GuildId))
                .ApplyAsync(Manifest, journalPath, Gates());

        public Task<RankTransitionApplyReport> RollbackAsync() =>
            new RankTransitionApplier(
                Db,
                Wom,
                Discord,
                clock: new FixedClock(Now),
                environment: new TransitionEnvironmentIdentity("test-db", 7173, Discord.GuildId))
                .RollbackAsync(Manifest, journalPath, Gates());

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            DeleteIfPresent(manifestPath);
            DeleteIfPresent(journalPath);
            DeleteIfPresent($"{journalPath}.lock");
        }

        private static ApplyPrerequisites Gates() => new("approved-cutover-test", true, true, true);

        private static IReadOnlyDictionary<string, string> RankRoleIds() =>
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // The transition planner consumes the frozen source keys for
                // the three role renames and the canonical key for Commander.
                ["Officer"] = "100",
                ["Commander"] = "200",
                ["Astral"] = "300",
                ["General"] = "400"
            };

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : ITransitionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class RecordingWom(string username, string initialRole) : IWiseOldManClient
    {
        public Dictionary<string, string> Roles { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            [username] = initialRole
        };

        public Task<string?> GetMemberRoleAsync(string requestedUsername, CancellationToken ct) =>
            Task.FromResult(Roles.TryGetValue(requestedUsername, out var role) ? role : null);

        public Task<string?> GetMemberRoleFreshAsync(string requestedUsername, CancellationToken ct) =>
            GetMemberRoleAsync(requestedUsername, ct);

        public Task<IReadOnlyDictionary<string, string>> GetMemberRolesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(Roles, StringComparer.OrdinalIgnoreCase));

        public Task<bool> IsImpAccountAsync(string requestedUsername, CancellationToken ct) => Task.FromResult(false);

        public Task<WomRoleUpdateResult> UpdateMemberRoleAsync(
            string requestedUsername,
            string role,
            CancellationToken ct,
            bool invalidateCache = true)
        {
            if (!Roles.ContainsKey(requestedUsername))
            {
                return Task.FromResult(new WomRoleUpdateResult(false, 404, "missing", null, null, requestedUsername));
            }

            Roles[requestedUsername] = role;
            return Task.FromResult(new WomRoleUpdateResult(true, 200, "ok", role, 1, requestedUsername));
        }

        public Task<(bool Success, string Details)> RemoveMemberAsync(string requestedUsername, CancellationToken ct) =>
            Task.FromResult((true, "ok"));

        public Task InvalidateCacheAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingDiscord : IDiscordRoleGateway
    {
        private readonly int configuredFailures;
        private int failuresRemaining;
        private readonly Dictionary<string, DiscordRoleObservation> roles;

        private RecordingDiscord(int configuredFailures, IReadOnlyList<DiscordRoleObservation> initialRoles, DiscordMemberObservation member)
        {
            this.configuredFailures = configuredFailures;
            failuresRemaining = configuredFailures;
            roles = initialRoles.ToDictionary(x => x.RoleId, StringComparer.Ordinal);
            Member = member;
        }

        public string GuildId => "guild";
        public DiscordMemberObservation Member { get; private set; }
        public int SuccessfulMemberMutations { get; private set; }

        public static RecordingDiscord Create(int configuredFailures)
        {
            var member = new DiscordMemberObservation("42", "Arvld", "Arvld", null, ["100", "900"]);
            var roles = new[]
            {
                Role("100", "Brassican", ["42"], position: 1),
                Role("200", "Commander", [], position: 2),
                Role("300", "General", [], position: 3),
                Role("400", "Colonel", [], position: 4),
                Role("900", "Raider", ["42"], position: 5)
            };
            return new RecordingDiscord(configuredFailures, roles, member);
        }

        public Task<DiscordGuildObservation?> GetGuildObservationAsync(CancellationToken ct)
        {
            var observedRoles = roles.Values
                .OrderBy(x => x.Position)
                .ToArray();
            var members = new[] { Member };
            return Task.FromResult<DiscordGuildObservation?>(new DiscordGuildObservation(
                GuildId,
                observedRoles,
                MembersFullyObserved: true,
                Notes: null,
                Members: members,
                MemberHash: DiscordMemberTransitionHelpers.ComputeMemberHash(members)));
        }

        public Task<DiscordMemberObservation?> GetMemberObservationAsync(string userId, CancellationToken ct) =>
            Task.FromResult<DiscordMemberObservation?>(
                string.Equals(userId, Member.UserId, StringComparison.Ordinal) ? Member : null);

        public Task<DiscordRoleMutationResult> RenameRoleAsync(
            string roleId,
            string expectedName,
            string targetName,
            CancellationToken ct) =>
            Task.FromResult(new DiscordRoleMutationResult(false, 405, "role rename is not part of this fixture", null));

        public Task<DiscordRankMembershipMutationResult> ApplyRankMembershipAsync(
            DiscordRankMembershipMutationRequest request,
            CancellationToken ct)
        {
            if (!string.Equals(request.DiscordUserId, Member.UserId, StringComparison.Ordinal))
            {
                return Task.FromResult(Failure(404, "member missing"));
            }

            if (!string.Equals(Member.EffectiveRoleHash, request.ExpectedBeforeRoleHash, StringComparison.OrdinalIgnoreCase) &&
                !request.ResumeOwnedIntent)
            {
                return Task.FromResult(Failure(409, "member drift"));
            }

            if (failuresRemaining > 0)
            {
                failuresRemaining--;
                return Task.FromResult(Failure(503, $"simulated Discord failure ({configuredFailures})"));
            }

            var before = Member;
            var afterRoles = request.ExpectedAfterRoleIds
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            Member = Member with
            {
                RoleIds = afterRoles,
                RoleHash = DiscordMemberTransitionHelpers.ComputeRoleHash(afterRoles)
            };
            SuccessfulMemberMutations++;
            RebuildRoleMemberships();
            return Task.FromResult(new DiscordRankMembershipMutationResult(
                true,
                200,
                "ok",
                before,
                Member,
                afterRoles.Except(before.RoleIds, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                before.RoleIds.Except(afterRoles, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                AlreadyApplied: before.EffectiveRoleHash == request.ExpectedAfterRoleHash,
                Partial: false,
                OwnedIntent: true,
                OwnedIntentId: request.OwnedIntentId));
        }

        private DiscordRankMembershipMutationResult Failure(int status, string details) =>
            new(
                false,
                status,
                details,
                Member,
                Member,
                [],
                [],
                AlreadyApplied: false,
                Partial: false,
                OwnedIntent: true,
                OwnedIntentId: null);

        private void RebuildRoleMemberships()
        {
            foreach (var (roleId, role) in roles.ToArray())
            {
                var memberIds = Member.RoleIds.Contains(roleId, StringComparer.Ordinal)
                    ? new[] { Member.UserId }
                    : [];
                roles[roleId] = role with
                {
                    MembershipCount = memberIds.Length,
                    MemberIds = memberIds,
                    MembershipHash = DiscordMemberTransitionHelpers.ComputeRoleHash(memberIds)
                };
            }
        }

        private static DiscordRoleObservation Role(string id, string name, IReadOnlyList<string> members, int position) =>
            new(
                id,
                name,
                null,
                "0",
                position,
                members.Count,
                Protected: false,
                Notes: null,
                MemberIds: members,
                MembershipHash: DiscordMemberTransitionHelpers.ComputeRoleHash(members));
    }
}
