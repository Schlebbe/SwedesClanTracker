using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using SwedesClanTracker.Core;
using SwedesClanTracker.RankTransition;

namespace SwedesClanTracker.RankTransition.Tests;

[CollectionDefinition("RankTransitionPostgres", DisableParallelization = true)]
public sealed class RankTransitionPostgresCollectionDefinition
{
}

/// <summary>
/// xUnit discovery-time gate for the isolated PostgreSQL rehearsal. The test
/// body is never entered when the connection setting is absent.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresIsolatedPostgresFactAttribute : FactAttribute
{
    public RequiresIsolatedPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RANK_TRANSITION_TEST_CONNECTION")))
        {
            Skip = "Set RANK_TRANSITION_TEST_CONNECTION to run the isolated PostgreSQL rehearsal.";
        }
    }
}

[Collection("RankTransitionPostgres")]
public sealed class PostgresRankTransitionIntegrationTests
{
    private const string ConnectionEnvironmentVariable = "RANK_TRANSITION_TEST_CONNECTION";

    private static string PgTool(string name)
    {
        var configured = Environment.GetEnvironmentVariable("RANK_TRANSITION_PG_BIN");
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = Environment.GetEnvironmentVariable("PG_BIN");
        }

        if (string.IsNullOrWhiteSpace(configured))
        {
            // Let ProcessStartInfo resolve the tool through PATH. This keeps
            // the archived rehearsal portable and avoids a machine-specific
            // PostgreSQL installation path.
            return OperatingSystem.IsWindows() ? $"{name}.exe" : name;
        }

        var candidates = OperatingSystem.IsWindows()
            ? new[] { $"{name}.exe", name }
            : new[] { name, $"{name}.exe" };
        return candidates
            .Select(candidate => Path.Combine(configured, candidate))
            .FirstOrDefault(File.Exists)
            ?? Path.Combine(configured, candidates[0]);
    }

    [RequiresIsolatedPostgresFact]
    public async Task Postgres_dump_restore_rehearsal_applies_verifies_retries_and_rolls_back_frozen_manifest()
    {
        var baseConnection = RequireIsolatedConnection();
        var sourceName = UniqueDatabaseName("rank_transition_dump_src");
        var restoreName = UniqueDatabaseName("rank_transition_dump_restore");
        var dumpPath = Path.Combine(Path.GetTempPath(), $"{sourceName}.dump");
        var manifestPath = Path.Combine(Path.GetTempPath(), $"{restoreName}.manifest.json");
        var journalPath = RankTransitionJournalStore.PathFor(manifestPath);

        await using var admin = await OpenAdminAsync(baseConnection);
        try
        {
            await CreateDatabaseAsync(admin, sourceName);
            await CreateDatabaseAsync(admin, restoreName);

            var now = DateTimeOffset.UtcNow;
            await using (var source = CreateContext(baseConnection, sourceName))
            {
                await source.Database.EnsureCreatedAsync();
                await SeedLegacyFixtureAsync(source, now);
            }

            var sourceToolConnection = WithDatabase(baseConnection, sourceName);
            var dump = await RunPostgresToolAsync(
                PgTool("pg_dump"),
                [
                    "--format=custom",
                    "--no-owner",
                    "--file", dumpPath,
                    "--dbname", sourceName
                ],
                sourceToolConnection);
            Assert.True(dump.ExitCode == 0, dump.Error);
            Assert.True(File.Exists(dumpPath));

            var restoreToolConnection = WithDatabase(baseConnection, restoreName);
            var restore = await RunPostgresToolAsync(
                PgTool("pg_restore"),
                [
                    "--no-owner",
                    "--exit-on-error",
                    "--dbname", restoreName,
                    dumpPath
                ],
                restoreToolConnection);
            Assert.True(restore.ExitCode == 0, restore.Error);

            var wom = new RecordingWom(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["LegacyOfficer"] = "Officer",
                    ["LegacyAstral"] = "General",
                    ["LegacyGeneral"] = "General"
                });
            var discord = new RecordingDiscord(
                [
                    Role("officer-role", "Officer", 1),
                    Role("astral-role", "Astral", 2),
                    Role("general-role", "General", 3)
                ]);

            await using (var restored = CreateContext(baseConnection, restoreName))
            {
                Assert.Equal(3, await restored.Players.CountAsync());
                var ignored = await restored.LifecycleEvents.AsNoTracking()
                    .SingleAsync(x => x.EventType == "WOM_RANK_MISMATCH_IGNORED");
                Assert.Equal("OPEN", ignored.Status);
                Assert.Contains("LegacyAstral", ignored.MetadataJson, StringComparison.Ordinal);

                var environment = await TransitionEnvironmentIdentityReader.ReadAsync(
                    restored,
                    new ConfigurationBuilder().Build());
                environment = environment with
                {
                    WiseOldManGroupId = 1,
                    DiscordGuildId = discord.GuildId
                };
                var planner = new RankTransitionPlanner(
                    restored,
                    wom,
                    discord,
                    new FixedClock(now),
                    environment);
                var manifest = await planner.PlanAsync(new RankTransitionPlannerOptions(
                    IncludeDiscord: true,
                    IncludeWom: true,
                    WomUpdatesManual: true,
                    DiscordRankRoleIds: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Officer"] = "officer-role",
                        ["Astral"] = "astral-role",
                        ["General"] = "general-role"
                    }));

                Assert.True(manifest.CompleteReadOnlyScope);
                Assert.DoesNotContain(manifest.Warnings, x => x.BlocksApply);
                Assert.True(manifest.WomUpdatesManual);
                Assert.NotEmpty(manifest.ManualWomChecks);
                Assert.DoesNotContain(manifest.Operations, x => x.Kind == TransitionOperationKind.WiseOldManRole);
                Assert.True(manifest.WomObservations.Single(x => x.Username == "LegacyAstral").IgnoredMismatch);

                // CurrentRank=General is the frozen legacy standing value and
                // maps to Colonel. The fresh evaluator's General result must
                // remain General in EligibleRank; it must not be remapped.
                var general = manifest.Players.Single(x => x.PlayerId == 9);
                Assert.Equal("Colonel", general.DatabaseCurrentRankTarget);
                Assert.Equal("General", general.DatabaseEligibleRankTarget);
                Assert.Equal("General", general.EvaluatedEligibleRank);
                Assert.Contains(manifest.Operations, x =>
                    x.Kind == TransitionOperationKind.DatabaseCurrentRank &&
                    x.TargetKey == "9" &&
                    x.Before == "General" &&
                    x.After == "Colonel");
                Assert.DoesNotContain(manifest.Operations, x =>
                    x.Kind == TransitionOperationKind.DatabaseEligibleRank &&
                    x.TargetKey == "9" &&
                    x.After == "Colonel");

                var frozen = await FreezeManifestAsync(manifest, manifestPath);
                var applier = new RankTransitionApplier(
                    restored,
                    wom,
                    discord,
                    clock: new FixedClock(now),
                    environment: environment);

                // Planning is read-only. Before the user reconciles the game
                // roster and lets the external WOM sync publish the frozen
                // roles, apply must stop before any tracker database write.
                var beforeManualWom = await restored.Players.AsNoTracking()
                    .OrderBy(x => x.Id)
                    .Select(x => new { x.Id, x.CurrentRank, x.EligibleRank })
                    .ToListAsync();
                var beforeManualApply = await applier.ApplyAsync(frozen, journalPath, Gates());
                Assert.False(beforeManualApply.Completed);
                Assert.Contains("Manual WOM reconciliation", beforeManualApply.Failure, StringComparison.OrdinalIgnoreCase);
                restored.ChangeTracker.Clear();
                var afterManualApply = await restored.Players.AsNoTracking()
                    .OrderBy(x => x.Id)
                    .Select(x => new { x.Id, x.CurrentRank, x.EligibleRank })
                    .ToListAsync();
                Assert.Equal(beforeManualWom.Count, afterManualApply.Count);
                for (var index = 0; index < beforeManualWom.Count; index++)
                {
                    Assert.Equal(beforeManualWom[index].Id, afterManualApply[index].Id);
                    Assert.Equal(beforeManualWom[index].CurrentRank, afterManualApply[index].CurrentRank);
                    Assert.Equal(beforeManualWom[index].EligibleRank, afterManualApply[index].EligibleRank);
                }
                Assert.Equal(0, wom.UpdateCalls);

                // Simulate the authoritative in-game update followed by the
                // external WOM sync; the fixture client refuses every write
                // so the transition can only pass through read-only checks.
                foreach (var check in frozen.ManualWomChecks)
                {
                    Assert.NotNull(check.RoleAfter);
                    wom.Roles[check.Username] = check.RoleAfter!;
                }

                var first = await applier.ApplyAsync(frozen, journalPath, Gates());
                Assert.True(first.Completed, first.Failure);
                restored.ChangeTracker.Clear();
                Assert.Equal("Brassican", (await restored.Players.AsNoTracking().SingleAsync(x => x.Id == 7)).CurrentRank);
                Assert.Equal("General", (await restored.Players.AsNoTracking().SingleAsync(x => x.Id == 8)).CurrentRank);
                Assert.Equal("Colonel", (await restored.Players.AsNoTracking().SingleAsync(x => x.Id == 9)).CurrentRank);
                Assert.Equal("General", (await restored.Players.AsNoTracking().SingleAsync(x => x.Id == 9)).EligibleRank);
                Assert.Equal("Colonel", wom.Roles["LegacyAstral"]);
                Assert.Equal("Colonel", wom.Roles["LegacyGeneral"]);
                Assert.Equal("Brassican", discord.RoleName("officer-role"));
                Assert.Equal("General", discord.RoleName("astral-role"));
                Assert.Equal("Colonel", discord.RoleName("general-role"));

                var verified = await new RankTransitionVerifier(restored, wom, discord)
                    .VerifyAsync(frozen, journalPath);
                Assert.True(verified.SafeToProceed, VerificationDetails(verified));

                var second = await applier.ApplyAsync(frozen, journalPath, Gates());
                Assert.True(second.Completed, second.Failure);
                restored.ChangeTracker.Clear();
                Assert.Equal("Colonel", (await restored.Players.AsNoTracking().SingleAsync(x => x.Id == 9)).CurrentRank);
                Assert.Equal("General", (await restored.Players.AsNoTracking().SingleAsync(x => x.Id == 9)).EligibleRank);
                Assert.Equal("Colonel", wom.Roles["LegacyGeneral"]);
                Assert.Equal("Colonel", discord.RoleName("general-role"));

                var rolledBack = await applier.RollbackAsync(frozen, journalPath, Gates());
                Assert.True(rolledBack.Completed, rolledBack.Failure);
                restored.ChangeTracker.Clear();
                Assert.Equal("Officer", (await restored.Players.AsNoTracking().SingleAsync(x => x.Id == 7)).CurrentRank);
                Assert.Equal("Astral", (await restored.Players.AsNoTracking().SingleAsync(x => x.Id == 8)).CurrentRank);
                Assert.Equal("General", (await restored.Players.AsNoTracking().SingleAsync(x => x.Id == 9)).CurrentRank);
                Assert.Equal("General", (await restored.Players.AsNoTracking().SingleAsync(x => x.Id == 9)).EligibleRank);
                Assert.Equal("Colonel", wom.Roles["LegacyAstral"]);
                Assert.Equal("Colonel", wom.Roles["LegacyGeneral"]);
                Assert.Equal("Brassican", wom.Roles["LegacyOfficer"]);
                Assert.Equal("Officer", discord.RoleName("officer-role"));
                Assert.Equal("Astral", discord.RoleName("astral-role"));
                Assert.Equal("General", discord.RoleName("general-role"));
                Assert.Equal(1, await restored.LifecycleEvents.CountAsync(x =>
                    x.EventType == "WOM_RANK_MISMATCH_IGNORED" && x.Status == "OPEN"));

                var rollbackVerification = await new RankTransitionVerifier(restored, wom, discord)
                    .VerifyAsync(frozen, journalPath);
                Assert.True(rollbackVerification.SafeToProceed, VerificationDetails(rollbackVerification));
            }
        }
        finally
        {
            DeleteTemporaryFile(dumpPath);
            DeleteTemporaryFile(manifestPath);
            DeleteJournalArtifacts(journalPath);
            NpgsqlConnection.ClearAllPools();
            await DropDatabaseAsync(admin, sourceName);
            await DropDatabaseAsync(admin, restoreName);
        }
    }

    [RequiresIsolatedPostgresFact]
    public async Task Postgres_transaction_failure_does_not_commit_earlier_rank_operations()
    {
        var baseConnection = RequireIsolatedConnection();
        var databaseName = UniqueDatabaseName("rank_transition_atomic");
        var manifestPath = Path.Combine(Path.GetTempPath(), $"{databaseName}.manifest.json");
        var journalPath = RankTransitionJournalStore.PathFor(manifestPath);
        await using var admin = await OpenAdminAsync(baseConnection);
        try
        {
            await CreateDatabaseAsync(admin, databaseName);
            await using (var db = CreateContext(baseConnection, databaseName))
            {
                await db.Database.EnsureCreatedAsync();
                db.Players.AddRange(
                    NewPlayer(1, "AtomicFirst", "Officer", "Officer", DateTimeOffset.UtcNow),
                    NewPlayer(2, "AtomicSecond", "Officer", "Officer", DateTimeOffset.UtcNow));
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlRawAsync(
                    """
                    CREATE OR REPLACE FUNCTION rank_transition_test_fail_second()
                    RETURNS trigger
                    LANGUAGE plpgsql
                    AS $$
                    BEGIN
                        IF NEW."Id" = 2 THEN
                            RAISE EXCEPTION 'rank transition integration failure';
                        END IF;
                        RETURN NEW;
                    END;
                    $$;
                    CREATE TRIGGER rank_transition_test_fail_second_trigger
                    BEFORE UPDATE OF "CurrentRank" ON "Players"
                    FOR EACH ROW EXECUTE FUNCTION rank_transition_test_fail_second();
                    """);

                var manifest = await FreezeManifestAsync(
                    new RankTransitionManifest
                    {
                        ManifestId = Guid.NewGuid().ToString("N"),
                        FrozenMappingVersion = FrozenRankMapping.Version,
                        FrozenMapping = FrozenRankMapping.Document(),
                        Operations =
                        [
                            Operation("1", "Officer", "Brassican"),
                            Operation("2", "Officer", "Brassican")
                        ]
                    },
                    manifestPath);
                var applier = new RankTransitionApplier(
                    db,
                    new RecordingWom(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
                    null);
                RankTransitionApplyReport? report = null;
                Exception? failure = null;
                try
                {
                    report = await applier.ApplyAsync(manifest, journalPath, Gates());
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                Assert.True(failure is not null || report is { Completed: false },
                    "The trigger should fail the transactional apply.");
                db.ChangeTracker.Clear();
                Assert.All(
                    await db.Players.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
                    player => Assert.Equal("Officer", player.CurrentRank));
                Assert.Empty(await db.LifecycleEvents.AsNoTracking()
                    .Where(x => x.EventType == "RANK_TRANSITION_AUDIT" || x.EventType == "RANK_TRANSITION_POLICY_MARKER")
                    .ToListAsync());

                var journal = await new RankTransitionJournalStore().LoadOrCreateAsync(journalPath, manifest);
                Assert.Empty(journal.Results);
            }
        }
        finally
        {
            DeleteTemporaryFile(manifestPath);
            DeleteJournalArtifacts(journalPath);
            NpgsqlConnection.ClearAllPools();
            await DropDatabaseAsync(admin, databaseName);
        }
    }

    [RequiresIsolatedPostgresFact]
    public async Task Postgres_approved_cutover_finalizes_one_rank_after_wom_without_pending_duplicate()
    {
        var baseConnection = RequireIsolatedConnection();
        var databaseName = UniqueDatabaseName("rank_transition_approved");
        var manifestPath = Path.Combine(Path.GetTempPath(), $"{databaseName}.manifest.json");
        var journalPath = RankTransitionJournalStore.PathFor(manifestPath);
        await using var admin = await OpenAdminAsync(baseConnection);
        try
        {
            await CreateDatabaseAsync(admin, databaseName);
            await using var db = CreateContext(baseConnection, databaseName);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            db.Players.Add(NewPlayer(1, "ApprovedMember", "Officer", "Officer", now));
            await db.SaveChangesAsync();

            var wom = new RecordingWom(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ApprovedMember"] = "Officer"
                });
            var manifest = await FreezeManifestAsync(
                new RankTransitionManifest
                {
                    ManifestId = Guid.NewGuid().ToString("N"),
                    FrozenMappingVersion = FrozenRankMapping.Version,
                    FrozenMapping = FrozenRankMapping.Document(),
                    ApprovedForCutover = true,
                    WomUpdatesManual = true,
                    ManualWomChecks =
                    [
                        new RankTransitionManualWomCheck(
                            RankTransitionPlanner.ComputeOperationId(
                                TransitionOperationKind.WiseOldManRole,
                                "ApprovedMember",
                                "Officer",
                                "Commander"),
                            "ApprovedMember",
                            "Officer",
                            "Commander",
                            true,
                            "Manual WOM fixture")
                    ],
                    WomObservations =
                    [
                        new RankTransitionWomObservation(
                            "ApprovedMember",
                            "Officer",
                            "Commander",
                            true,
                            false,
                            false,
                            "Approved cutover fixture")
                    ],
                    PromotionDecisions =
                    [
                        new PromotionDecision(
                            1,
                            "ApprovedMember",
                            "Brassican",
                            "Commander",
                            "Commander",
                            true,
                            false,
                            "Total level >= 2200; approved cutover target: Commander.",
                            ApprovedForCutover: true)
                    ],
                    Operations =
                    [
                        EligibleOperation("1", "Officer", "Commander"),
                        FinalizationOperation("1", "Officer", "Commander")
                    ]
                },
                manifestPath);

            // Simulate the user updating the authoritative game roster and
            // the external WOM sync publishing the final role. The fixture's
            // WOM client rejects writes, so apply can only read/verify it.
            wom.Roles["ApprovedMember"] = "Commander";
            var applier = new RankTransitionApplier(db, wom, null);
            var applied = await applier.ApplyAsync(manifest, journalPath, Gates());

            Assert.True(applied.Completed, applied.Failure);
            db.ChangeTracker.Clear();
            Assert.Equal("Commander", (await db.Players.AsNoTracking().SingleAsync()).CurrentRank);
            Assert.Equal("Commander", (await db.Players.AsNoTracking().SingleAsync()).EligibleRank);
            Assert.Equal("Commander", wom.Roles["ApprovedMember"]);
            Assert.Equal(0, wom.UpdateCalls);
            var candidates = await db.PromotionCandidates.AsNoTracking().ToListAsync();
            var candidate = Assert.Single(candidates);
            Assert.Equal(PromotionStatus.APPROVED, candidate.Status);
            Assert.Equal("Commander", candidate.NewRank);
            Assert.DoesNotContain(candidates, x => x.Status == PromotionStatus.PENDING);
            Assert.Contains(await db.LifecycleEvents.AsNoTracking().ToListAsync(), x =>
                x.EventType == "RANK_TRANSITION_PROMOTION_APPROVED" &&
                x.MetadataJson.Contains("\"NewRank\":\"Commander\"", StringComparison.Ordinal));
        }
        finally
        {
            DeleteTemporaryFile(manifestPath);
            DeleteJournalArtifacts(journalPath);
            NpgsqlConnection.ClearAllPools();
            await DropDatabaseAsync(admin, databaseName);
        }
    }

    [RequiresIsolatedPostgresFact]
    public async Task Postgres_approved_cutover_dump_restore_apply_retry_and_rollback_preserves_owned_history()
    {
        var baseConnection = RequireIsolatedConnection();
        var sourceName = UniqueDatabaseName("rank_transition_approved_src");
        var restoreName = UniqueDatabaseName("rank_transition_approved_restore");
        var dumpPath = Path.Combine(Path.GetTempPath(), $"{sourceName}.dump");
        var manifestPath = Path.Combine(Path.GetTempPath(), $"{restoreName}.manifest.json");
        var journalPath = RankTransitionJournalStore.PathFor(manifestPath);

        await using var admin = await OpenAdminAsync(baseConnection);
        try
        {
            await CreateDatabaseAsync(admin, sourceName);
            await CreateDatabaseAsync(admin, restoreName);
            var now = DateTimeOffset.UtcNow;
            await using (var source = CreateContext(baseConnection, sourceName))
            {
                await source.Database.EnsureCreatedAsync();
                source.Players.Add(NewPlayer(1, "ApprovedDumpMember", "Officer", "Officer", now));
                await source.SaveChangesAsync();
            }

            var dump = await RunPostgresToolAsync(
                PgTool("pg_dump"),
                ["--format=custom", "--no-owner", "--file", dumpPath, "--dbname", sourceName],
                WithDatabase(baseConnection, sourceName));
            Assert.True(dump.ExitCode == 0, dump.Error);
            Assert.True(File.Exists(dumpPath));

            var restore = await RunPostgresToolAsync(
                PgTool("pg_restore"),
                ["--no-owner", "--exit-on-error", "--dbname", restoreName, dumpPath],
                WithDatabase(baseConnection, restoreName));
            Assert.True(restore.ExitCode == 0, restore.Error);

            var wom = new RecordingWom(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ApprovedDumpMember"] = "Officer"
                });
            var discord = new RecordingDiscord(
                [RoleWithMember("officer-role", "Officer", 1, "approved-dump-member")],
                [new DiscordMemberObservation(
                    "approved-dump-member",
                    "ApprovedDumpMember",
                    null,
                    null,
                    ["officer-role"])]);

            await using var restored = CreateContext(baseConnection, restoreName);
            var manifest = await FreezeManifestAsync(
                new RankTransitionManifest
                {
                    ManifestId = Guid.NewGuid().ToString("N"),
                    FrozenMappingVersion = FrozenRankMapping.Version,
                    FrozenMapping = FrozenRankMapping.Document(),
                    ApprovedForCutover = true,
                    WomUpdatesManual = true,
                    ManualWomChecks =
                    [
                        new RankTransitionManualWomCheck(
                            RankTransitionPlanner.ComputeOperationId(
                                TransitionOperationKind.WiseOldManRole,
                                "ApprovedDumpMember",
                                "Officer",
                                "Commander"),
                            "ApprovedDumpMember",
                            "Officer",
                            "Commander",
                            true,
                            "Manual WOM dump/restore fixture")
                    ],
                    DiscordMemberRanksManual = true,
                    DiscordMembers =
                    [
                        new DiscordMemberObservation(
                            "approved-dump-member",
                            "ApprovedDumpMember",
                            null,
                            null,
                            ["officer-role"])
                    ],
                    DiscordRoles =
                    [RoleWithMember("officer-role", "Officer", 1, "approved-dump-member")],
                    Players =
                    [
                        new RankTransitionPlayerObservation(
                            1,
                            "ApprovedDumpMember",
                            "ACTIVE",
                            true,
                            "Officer",
                            "Officer",
                            "Officer",
                            "Officer",
                            null,
                            null,
                            null,
                            now,
                            "Commander",
                            "Total level >= 2200; approved cutover target: Commander.",
                            false,
                            null)
                    ],
                    WomObservations =
                    [
                        new RankTransitionWomObservation(
                            "ApprovedDumpMember",
                            "Officer",
                            "Commander",
                            true,
                            false,
                            false,
                            "Approved dump/restore cutover fixture")
                    ],
                    PromotionDecisions =
                    [
                        new PromotionDecision(
                            1,
                            "ApprovedDumpMember",
                            "Officer",
                            "Commander",
                            "Commander",
                            true,
                            false,
                            "Total level >= 2200; approved cutover target: Commander.",
                            ApprovedForCutover: true)
                    ],
                    Operations =
                    [
                        EligibleOperation("1", "Officer", "Commander"),
                        FinalizationOperation("1", "Officer", "Commander")
                    ]
                },
                manifestPath);

            // Manual game/WOM reconciliation occurs before apply; this
            // read-only fixture refuses every WOM write.
            wom.Roles["ApprovedDumpMember"] = "Commander";
            Assert.True(manifest.DiscordMemberRanksManual);
            Assert.True(manifest.WomUpdatesManual);
            Assert.Single(manifest.DiscordMembers);
            Assert.Empty(manifest.DiscordMemberRankChanges);
            Assert.DoesNotContain(manifest.Operations, x => x.Kind == TransitionOperationKind.DiscordMemberRankRole);

            var applier = new RankTransitionApplier(restored, wom, discord);
            var first = await applier.ApplyAsync(manifest, journalPath, Gates());
            Assert.True(first.Completed, first.Failure);
            restored.ChangeTracker.Clear();
            var firstPlayer = await restored.Players.AsNoTracking().SingleAsync();
            Assert.Equal("Commander", firstPlayer.CurrentRank);
            Assert.Equal("Commander", firstPlayer.EligibleRank);
            Assert.Equal("Commander", wom.Roles["ApprovedDumpMember"]);
            Assert.Equal(0, wom.UpdateCalls);
            Assert.DoesNotContain("Brassican", wom.Roles.Values, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(0, discord.MemberMutationCalls);

            var firstCandidate = Assert.Single(await restored.PromotionCandidates.AsNoTracking().ToListAsync());
            Assert.Equal(PromotionStatus.APPROVED, firstCandidate.Status);
            Assert.Equal("Commander", firstCandidate.NewRank);
            Assert.DoesNotContain(
                await restored.PromotionCandidates.AsNoTracking().ToListAsync(),
                x => x.Status == PromotionStatus.PENDING);
            var finalizationId = RankTransitionPlanner.ComputeOperationId(
                TransitionOperationKind.FinalizeApprovedPromotion,
                "1",
                "Officer",
                "Commander");
            Assert.Contains(
                first.Results,
                x => x.OperationId == finalizationId &&
                    x.Status == TransitionOperationStatus.Applied &&
                    x.RelatedEntityId == firstCandidate.Id);

            var second = await applier.ApplyAsync(manifest, journalPath, Gates());
            Assert.True(second.Completed, second.Failure);
            restored.ChangeTracker.Clear();
            Assert.Equal("Commander", (await restored.Players.AsNoTracking().SingleAsync()).CurrentRank);
            var retryCandidates = await restored.PromotionCandidates.AsNoTracking().ToListAsync();
            Assert.Single(retryCandidates);
            Assert.Equal(firstCandidate.Id, retryCandidates[0].Id);
            Assert.Equal(PromotionStatus.APPROVED, retryCandidates[0].Status);
            Assert.DoesNotContain("Brassican", wom.Roles.Values, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(0, discord.MemberMutationCalls);

            var rollback = await applier.RollbackAsync(manifest, journalPath, Gates());
            Assert.True(rollback.Completed, rollback.Failure);
            restored.ChangeTracker.Clear();
            Assert.Equal("Officer", (await restored.Players.AsNoTracking().SingleAsync()).CurrentRank);
            var rolledBackCandidate = Assert.Single(await restored.PromotionCandidates.AsNoTracking().ToListAsync());
            Assert.Equal(firstCandidate.Id, rolledBackCandidate.Id);
            Assert.Equal(PromotionStatus.DISMISSED, rolledBackCandidate.Status);
            Assert.Equal("Commander", wom.Roles["ApprovedDumpMember"]);
            Assert.DoesNotContain("Brassican", wom.Roles.Values, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(0, discord.MemberMutationCalls);
        }
        finally
        {
            DeleteTemporaryFile(dumpPath);
            DeleteTemporaryFile(manifestPath);
            DeleteJournalArtifacts(journalPath);
            NpgsqlConnection.ClearAllPools();
            await DropDatabaseAsync(admin, sourceName);
            await DropDatabaseAsync(admin, restoreName);
        }
    }

    [RequiresIsolatedPostgresFact]
    public async Task Postgres_rollback_refuses_to_choose_an_unowned_pending_candidate_by_rank()
    {
        var baseConnection = RequireIsolatedConnection();
        var databaseName = UniqueDatabaseName("rank_transition_provenance");
        var manifestPath = Path.Combine(Path.GetTempPath(), $"{databaseName}.manifest.json");
        var journalPath = RankTransitionJournalStore.PathFor(manifestPath);
        await using var admin = await OpenAdminAsync(baseConnection);
        try
        {
            await CreateDatabaseAsync(admin, databaseName);
            await using var db = CreateContext(baseConnection, databaseName);
            await db.Database.EnsureCreatedAsync();

            var now = DateTimeOffset.UtcNow;
            var snapshot = Snapshot(now);
            db.Players.Add(NewPlayer(1, "Provenance", "Brassican", "Brassican", now, snapshot));
            await db.SaveChangesAsync();
            // PostgreSQL normalizes timestamp precision on write. Hash the
            // persisted row, not the pre-save object, so planner preflight
            // observes the same bytes as the database.
            var persistedSnapshot = await db.PlayerSnapshots.AsNoTracking().SingleAsync();

            var operation = QueueOperation(
                "1",
                "General",
                RankTransitionPlanner.SnapshotHash(persistedSnapshot));
            var manifest = await FreezeManifestAsync(
                new RankTransitionManifest
                {
                    ManifestId = Guid.NewGuid().ToString("N"),
                    FrozenMappingVersion = FrozenRankMapping.Version,
                    FrozenMapping = FrozenRankMapping.Document(),
                    Operations = [operation]
                },
                manifestPath);
            var applier = new RankTransitionApplier(
                db,
                new RecordingWom(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
                null);

            var applied = await applier.ApplyAsync(manifest, journalPath, Gates());
            Assert.True(applied.Completed, applied.Failure);
            var ownedCandidate = await db.PromotionCandidates.SingleAsync();
            Assert.Equal(PromotionStatus.PENDING, ownedCandidate.Status);
            Assert.Equal("General", ownedCandidate.NewRank);
            Assert.Contains(
                (await new RankTransitionJournalStore().LoadOrCreateAsync(journalPath, manifest)).Results,
                result => result.OperationId == operation.OperationId &&
                          result.Status == TransitionOperationStatus.Applied &&
                          result.RelatedEntityId == ownedCandidate.Id);

            // A second row with the same target rank makes a rank-only lookup
            // unsafe. The audit and journal provenance are then deliberately
            // removed/corrupted to exercise the fail-closed rollback guard.
            db.PromotionCandidates.Add(new PromotionCandidate
            {
                PlayerId = 1,
                OldRank = "Brassican",
                NewRank = "General",
                Reason = "unrelated pending row",
                CandidateType = PromotionCandidateType.unknown_wom_role,
                Status = PromotionStatus.PENDING,
                CreatedAt = now.AddSeconds(1)
            });
            var audit = (await db.LifecycleEvents
                    .Where(x => x.EventType == "RANK_TRANSITION_AUDIT")
                    .ToListAsync())
                .Single(x => x.MetadataJson.Contains(operation.OperationId, StringComparison.Ordinal));
            db.LifecycleEvents.Remove(audit);
            await db.SaveChangesAsync();

            var journalStore = new RankTransitionJournalStore();
            var journal = await journalStore.LoadOrCreateAsync(journalPath, manifest);
            journal.Results = journal.Results
                .Select(result => result.OperationId == operation.OperationId
                    ? result with { RelatedEntityId = null }
                    : result)
                .ToArray();
            await journalStore.SaveAsync(journalPath, journal);

            var rollback = await applier.RollbackAsync(manifest, journalPath, Gates());

            Assert.False(rollback.Completed);
            Assert.Contains("ownership", rollback.Failure!, StringComparison.OrdinalIgnoreCase);
            Assert.All(
                await db.PromotionCandidates.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
                candidate => Assert.Equal(PromotionStatus.PENDING, candidate.Status));
            Assert.DoesNotContain(
                await db.LifecycleEvents.AsNoTracking().ToListAsync(),
                x => x.EventType == "RANK_TRANSITION_PROMOTION_ROLLED_BACK");
        }
        finally
        {
            DeleteTemporaryFile(manifestPath);
            DeleteJournalArtifacts(journalPath);
            NpgsqlConnection.ClearAllPools();
            await DropDatabaseAsync(admin, databaseName);
        }
    }

    [RequiresIsolatedPostgresFact]
    public async Task Postgres_cli_plan_uses_read_only_transaction_and_partial_apply_stops_before_writes()
    {
        var baseConnection = RequireIsolatedConnection();
        var databaseName = UniqueDatabaseName("rank_transition_cli");
        var manifestPath = Path.Combine(Path.GetTempPath(), $"{databaseName}.cli.json");
        var csvPath = RankTransitionMemberReport.CsvPathFor(manifestPath);
        var markdownPath = RankTransitionMemberReport.MarkdownPathFor(manifestPath);
        var journalPath = RankTransitionJournalStore.PathFor(manifestPath);
        await using var admin = await OpenAdminAsync(baseConnection);
        try
        {
            await CreateDatabaseAsync(admin, databaseName);
            await using (var db = CreateContext(baseConnection, databaseName))
            {
                await db.Database.EnsureCreatedAsync();
                var now = DateTimeOffset.UtcNow;
                var player = NewPlayer(1, "CliSmoke", "Officer", "Officer", now, Snapshot(now));
                db.Players.Add(player);
                db.PromotionCandidates.Add(new PromotionCandidate
                {
                    Player = player,
                    OldRank = "Officer",
                    NewRank = "General",
                    Reason = "CLI smoke sentinel",
                    CandidateType = PromotionCandidateType.unknown_wom_role,
                    Status = PromotionStatus.PENDING,
                    CreatedAt = now
                });
                db.LifecycleEvents.Add(new LifecycleEvent
                {
                    PlayerId = player.Id,
                    EventType = "CLI_SMOKE_SENTINEL",
                    MetadataJson = "{\"sentinel\":true}",
                    Status = "OPEN",
                    CreatedAt = now
                });
                await db.SaveChangesAsync();

                var before = await ReadDatabaseFingerprintAsync(db);
                var cliConnection = WithDatabase(baseConnection, databaseName);
                var plan = await RunRankTransitionCliAsync(
                    [
                        "plan",
                        "--no-discord",
                        "--no-wom",
                        "--approve-qualifying-promotions",
                        "--manifest", manifestPath
                    ],
                    cliConnection);

                Assert.Equal(0, plan.ExitCode);
                Assert.DoesNotContain(cliConnection.ConnectionString, plan.Output + plan.Error, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("PGPASSWORD", plan.Output + plan.Error, StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(manifestPath));
                Assert.True(File.Exists(csvPath));
                Assert.True(File.Exists(markdownPath), "The CLI plan must regenerate the Markdown member list from the frozen manifest.");
                var partial = await new RankTransitionManifestStore().LoadAndVerifyAsync(manifestPath);
                Assert.False(partial.CompleteReadOnlyScope);
                Assert.True(partial.ApprovedForCutover);
                Assert.Equal(RankTransitionManifest.CurrentSchemaVersion, partial.SchemaVersion);

                db.ChangeTracker.Clear();
                var afterPlan = await ReadDatabaseFingerprintAsync(db);
                AssertDatabaseFingerprintEqual(before, afterPlan);

                var apply = await RunRankTransitionCliAsync(
                    [
                        "apply",
                        "--manifest", manifestPath,
                        "--reviewed-by", "cli-smoke",
                        "--maintenance-confirmed",
                        "--backup-confirmed",
                        "--game-reconciled"
                    ],
                    cliConnection);

                Assert.Equal(3, apply.ExitCode);
                Assert.Contains("Diagnostic partial plans", apply.Error, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(cliConnection.ConnectionString, apply.Output + apply.Error, StringComparison.OrdinalIgnoreCase);
                Assert.False(File.Exists(journalPath), "Partial apply must stop before creating an operation journal.");

                db.ChangeTracker.Clear();
                var afterApply = await ReadDatabaseFingerprintAsync(db);
                AssertDatabaseFingerprintEqual(before, afterApply);
            }
        }
        finally
        {
            DeleteTemporaryFile(manifestPath);
            DeleteTemporaryFile(csvPath);
            DeleteTemporaryFile(markdownPath);
            DeleteJournalArtifacts(journalPath);
            NpgsqlConnection.ClearAllPools();
            await DropDatabaseAsync(admin, databaseName);
        }
    }

    [RequiresIsolatedPostgresFact]
    public async Task Postgres_rollback_recovers_committed_database_change_when_journal_is_missing()
    {
        var baseConnection = RequireIsolatedConnection();
        var databaseName = UniqueDatabaseName("rank_transition_recovery");
        var manifestPath = Path.Combine(Path.GetTempPath(), $"{databaseName}.manifest.json");
        var journalPath = RankTransitionJournalStore.PathFor(manifestPath);
        await using var admin = await OpenAdminAsync(baseConnection);
        try
        {
            await CreateDatabaseAsync(admin, databaseName);
            await using (var db = CreateContext(baseConnection, databaseName))
            {
                await db.Database.EnsureCreatedAsync();
                db.Players.Add(NewPlayer(1, "Recovery", "Officer", "Officer", DateTimeOffset.UtcNow));
                await db.SaveChangesAsync();

                var manifest = await FreezeManifestAsync(
                    new RankTransitionManifest
                    {
                        ManifestId = Guid.NewGuid().ToString("N"),
                        FrozenMappingVersion = FrozenRankMapping.Version,
                        FrozenMapping = FrozenRankMapping.Document(),
                        Operations = [Operation("1", "Officer", "Brassican")]
                    },
                    manifestPath);
                var applier = new RankTransitionApplier(
                    db,
                    new RecordingWom(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
                    null);
                var applied = await applier.ApplyAsync(manifest, journalPath, Gates());
                Assert.True(applied.Completed, applied.Failure);
                db.ChangeTracker.Clear();
                Assert.Equal("Brassican", (await db.Players.AsNoTracking().SingleAsync()).CurrentRank);
                Assert.Contains(await db.LifecycleEvents.AsNoTracking().ToListAsync(), eventRow =>
                    eventRow.EventType == "RANK_TRANSITION_AUDIT" &&
                    eventRow.MetadataJson.Contains("\"Status\":\"Applied\"", StringComparison.Ordinal));

                File.Delete(journalPath);
                Assert.False(File.Exists(journalPath));

                var rollback = await applier.RollbackAsync(manifest, journalPath, Gates());
                Assert.True(rollback.Completed, rollback.Failure);
                db.ChangeTracker.Clear();
                Assert.Equal("Officer", (await db.Players.AsNoTracking().SingleAsync()).CurrentRank);
                Assert.True(File.Exists(journalPath));
                Assert.Contains(
                    (await new RankTransitionJournalStore().LoadOrCreateAsync(journalPath, manifest)).Results,
                    result => result.Status == TransitionOperationStatus.RolledBack);
            }
        }
        finally
        {
            DeleteTemporaryFile(manifestPath);
            DeleteJournalArtifacts(journalPath);
            NpgsqlConnection.ClearAllPools();
            await DropDatabaseAsync(admin, databaseName);
        }
    }

    private static NpgsqlConnectionStringBuilder RequireIsolatedConnection()
    {
        var raw = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException($"{ConnectionEnvironmentVariable} is required.");
        }

        var connection = new NpgsqlConnectionStringBuilder(raw);
        if (!string.Equals(connection.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            connection.Port != 55439 ||
            !string.Equals(connection.Database, "postgres", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{ConnectionEnvironmentVariable} must target the isolated local PostgreSQL cluster at 127.0.0.1:55439/postgres.");
        }

        return connection;
    }

    private static NpgsqlConnectionStringBuilder WithDatabase(
        NpgsqlConnectionStringBuilder baseConnection,
        string databaseName) =>
        new(baseConnection.ConnectionString) { Database = databaseName };

    private static TrackerDbContext CreateContext(
        NpgsqlConnectionStringBuilder baseConnection,
        string databaseName) =>
        new(new DbContextOptionsBuilder<TrackerDbContext>()
            .UseNpgsql(WithDatabase(baseConnection, databaseName).ConnectionString)
            .Options);

    private static async Task<NpgsqlConnection> OpenAdminAsync(
        NpgsqlConnectionStringBuilder baseConnection)
    {
        var connection = new NpgsqlConnection(WithDatabase(baseConnection, "postgres").ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task CreateDatabaseAsync(NpgsqlConnection admin, string databaseName)
    {
        await using var command = admin.CreateCommand();
        command.CommandText = $"CREATE DATABASE {QuoteIdentifier(databaseName)}";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(NpgsqlConnection admin, string databaseName)
    {
        await using var command = admin.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS {QuoteIdentifier(databaseName)} WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    private static string QuoteIdentifier(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string UniqueDatabaseName(string prefix) =>
        $"{prefix}_{Guid.NewGuid():N}";

    private static async Task SeedLegacyFixtureAsync(TrackerDbContext db, DateTimeOffset now)
    {
        db.Players.AddRange(
            NewPlayer(7, "LegacyOfficer", "Officer", "Officer", now, Snapshot(now)),
            NewPlayer(8, "LegacyAstral", "Astral", "Astral", now, Snapshot(now, ehp: 1750)),
            NewPlayer(9, "LegacyGeneral", "General", "General", now, Snapshot(now, ehp: 1750)));
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = 8,
            EventType = "WOM_RANK_MISMATCH_IGNORED",
            MetadataJson = "{\"Username\":\"LegacyAstral\",\"WomRole\":\"General\",\"DatabaseRank\":\"Astral\"}",
            Status = "OPEN",
            CreatedAt = now
        });
        await db.SaveChangesAsync();
    }

    private static Player NewPlayer(
        int id,
        string username,
        string currentRank,
        string eligibleRank,
        DateTimeOffset now,
        PlayerSnapshot? snapshot = null)
    {
        var player = new Player
        {
            Id = id,
            Username = username,
            CurrentRank = currentRank,
            EligibleRank = eligibleRank,
            Status = PlayerStatus.ACTIVE,
            LastSeen = now,
            LastSynced = now
        };
        if (snapshot is not null)
        {
            snapshot.PlayerId = id;
            snapshot.Player = player;
            player.Snapshots.Add(snapshot);
        }
        return player;
    }

    private static PlayerSnapshot Snapshot(
        DateTimeOffset timestamp,
        double ehb = 0,
        double ehp = 0,
        int collections = 0,
        int petCount = 0) => new()
    {
        Timestamp = timestamp,
        Ehb = ehb,
        Ehp = ehp,
        Collections = collections,
        PetCount = petCount
    };

    private static async Task<RankTransitionManifest> FreezeManifestAsync(
        RankTransitionManifest manifest,
        string manifestPath)
    {
        var store = new RankTransitionManifestStore();
        await store.SaveAsync(manifestPath, manifest);
        return await store.LoadAndVerifyAsync(manifestPath);
    }

    private static RankTransitionOperation Operation(
        string playerId,
        string before,
        string after) =>
        new(
            RankTransitionPlanner.ComputeOperationId(
                TransitionOperationKind.DatabaseCurrentRank,
                playerId,
                before,
                after),
            TransitionOperationKind.DatabaseCurrentRank,
            playerId,
            nameof(Player.CurrentRank),
            before,
            after,
            IsExternal: false,
            IsActive: true,
            Protected: false,
            RequiresReview: false,
            SnapshotId: null,
            SnapshotHash: null,
            LastSyncedAtUtc: null,
            Reason: "postgres integration");

    private static RankTransitionOperation EligibleOperation(
        string playerId,
        string before,
        string after) =>
        new(
            RankTransitionPlanner.ComputeOperationId(
                TransitionOperationKind.DatabaseEligibleRank,
                playerId,
                before,
                after),
            TransitionOperationKind.DatabaseEligibleRank,
            playerId,
            nameof(Player.EligibleRank),
            before,
            after,
            IsExternal: false,
            IsActive: true,
            Protected: false,
            RequiresReview: false,
            SnapshotId: null,
            SnapshotHash: null,
            LastSyncedAtUtc: null,
            Reason: "postgres integration");

    private static RankTransitionOperation FinalizationOperation(
        string playerId,
        string before,
        string after) =>
        new(
            RankTransitionPlanner.ComputeOperationId(
                TransitionOperationKind.FinalizeApprovedPromotion,
                playerId,
                before,
                after),
            TransitionOperationKind.FinalizeApprovedPromotion,
            playerId,
            "PromotionApproval",
            before,
            after,
            IsExternal: false,
            IsActive: true,
            Protected: false,
            RequiresReview: false,
            SnapshotId: null,
            SnapshotHash: null,
            LastSyncedAtUtc: null,
            Reason: "postgres approved integration");

    private static RankTransitionOperation QueueOperation(
        string playerId,
        string after,
        string snapshotHash) =>
        new(
            RankTransitionPlanner.ComputeOperationId(
                TransitionOperationKind.QueuePromotionCandidate,
                playerId,
                null,
                after),
            TransitionOperationKind.QueuePromotionCandidate,
            playerId,
            "PromotionCandidate",
            null,
            after,
            IsExternal: false,
            IsActive: true,
            Protected: false,
            RequiresReview: true,
            SnapshotId: null,
            SnapshotHash: snapshotHash,
            LastSyncedAtUtc: null,
            Reason: "postgres integration");

    private static ApplyPrerequisites Gates() =>
        new("integration-test", true, true, true);

    private static DiscordRoleObservation Role(string roleId, string name, int position) =>
        new(
            roleId,
            name,
            null,
            "1",
            position,
            1,
            Protected: false,
            Notes: null,
            MemberIds: [$"member-{roleId}"],
            MembershipHash: DiscordMemberTransitionHelpers.ComputeRoleHash([$"member-{roleId}"]));

    private static DiscordRoleObservation RoleWithMember(
        string roleId,
        string name,
        int position,
        string memberId) =>
        new(
            roleId,
            name,
            null,
            "1",
            position,
            1,
            Protected: false,
            Notes: null,
            MemberIds: [memberId],
            MembershipHash: DiscordMemberTransitionHelpers.ComputeRoleHash([memberId]));

    private static string VerificationDetails(RankTransitionVerificationReport report) =>
        string.Join("; ", report.Items.Where(x => x.Status == TransitionOperationStatus.Drifted)
            .Select(x => $"{x.OperationId}: {x.Details}"));

    private static async Task<DatabaseFingerprint> ReadDatabaseFingerprintAsync(TrackerDbContext db)
    {
        var players = await db.Players.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        var snapshots = await db.PlayerSnapshots.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        var candidates = await db.PromotionCandidates.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        var lifecycleEvents = await db.LifecycleEvents.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        return new DatabaseFingerprint(
            players.Select(x => string.Join('|',
                x.Id,
                x.Username,
                x.CurrentRank,
                x.EligibleRank,
                x.Status,
                FormatTimestamp(x.LastSeen),
                FormatTimestamp(x.LastSynced),
                x.StoredPetCount,
                x.ManualPetOverride)).ToArray(),
            snapshots.Select(x => string.Join('|',
                x.Id,
                x.PlayerId,
                FormatTimestamp(x.Timestamp),
                x.TotalLevel,
                x.Ehb.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                x.Ehp.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                x.Collections,
                x.PetCount)).ToArray(),
            candidates.Select(x => string.Join('|',
                x.Id,
                x.PlayerId,
                x.OldRank,
                x.NewRank,
                x.Reason,
                x.CandidateType,
                x.WomRoleAtEvaluation,
                x.Status,
                FormatTimestamp(x.CreatedAt))).ToArray(),
            lifecycleEvents.Select(x => string.Join('|',
                x.Id,
                x.PlayerId,
                x.EventType,
                x.MetadataJson,
                x.Status,
                FormatTimestamp(x.CreatedAt))).ToArray());
    }

    private static void AssertDatabaseFingerprintEqual(
        DatabaseFingerprint expected,
        DatabaseFingerprint actual)
    {
        Assert.Equal(expected.Players, actual.Players);
        Assert.Equal(expected.Snapshots, actual.Snapshots);
        Assert.Equal(expected.Candidates, actual.Candidates);
        Assert.Equal(expected.LifecycleEvents, actual.LifecycleEvents);
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "";

    private static void DeleteTemporaryFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void DeleteJournalArtifacts(string journalPath)
    {
        DeleteTemporaryFile(journalPath);
        DeleteTemporaryFile($"{journalPath}.lock");
    }

    private static async Task<CliRunResult> RunRankTransitionCliAsync(
        IReadOnlyList<string> arguments,
        NpgsqlConnectionStringBuilder connection)
    {
        var toolAssemblyPath = typeof(RankTransitionPlanner).Assembly.Location;
        Assert.True(File.Exists(toolAssemblyPath), $"Rank transition tool assembly not found: {toolAssemblyPath}");
        Assert.True(
            File.Exists(Path.ChangeExtension(toolAssemblyPath, ".runtimeconfig.json")),
            "Rank transition tool runtimeconfig was not copied beside the compiled tool.");
        Assert.True(
            File.Exists(Path.ChangeExtension(toolAssemblyPath, ".deps.json")),
            "Rank transition tool deps file was not copied beside the compiled tool.");

        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(toolAssemblyPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(toolAssemblyPath);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        // The child receives only the runtime configuration key needed by the
        // CLI. Test-harness connection variables and PG credential fields are
        // removed so they cannot leak into the child process or its output.
        foreach (var key in new[]
                 {
                     ConnectionEnvironmentVariable,
                     "RANK_TRANSITION_PG_BIN",
                     "ConnectionStrings__DefaultConnection",
                     "PGHOST",
                     "PGPORT",
                     "PGUSER",
                     "PGPASSWORD",
                     "PGDATABASE"
                 })
        {
            start.Environment.Remove(key);
        }
        start.Environment["ConnectionStrings__DefaultConnection"] = connection.ConnectionString;

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the rank transition CLI process.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CliRunResult(process.ExitCode, await outputTask, await errorTask);
    }

    private sealed record DatabaseFingerprint(
        IReadOnlyList<string> Players,
        IReadOnlyList<string> Snapshots,
        IReadOnlyList<string> Candidates,
        IReadOnlyList<string> LifecycleEvents);

    private sealed record CliRunResult(int ExitCode, string Output, string Error);

    private static async Task<(int ExitCode, string Output, string Error)> RunPostgresToolAsync(
        string executable,
        IReadOnlyList<string> arguments,
        NpgsqlConnectionStringBuilder connection)
    {
        if (Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            Assert.True(File.Exists(executable), $"PostgreSQL tool not found: {executable}");
        }
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // Keep credentials out of argv and output. pg_dump/pg_restore receive
        // only the database name in --dbname and the connection fields here.
        start.Environment["PGHOST"] = connection.Host;
        start.Environment["PGPORT"] = connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["PGUSER"] = connection.Username;
        start.Environment["PGPASSWORD"] = connection.Password ?? "";
        start.Environment["PGDATABASE"] = connection.Database;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await outputTask, await errorTask);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : ITransitionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class RecordingWom(IReadOnlyDictionary<string, string> initialRoles) : IWiseOldManClient
    {
        public Dictionary<string, string> Roles { get; } =
            new(initialRoles, StringComparer.OrdinalIgnoreCase);
        public int UpdateCalls { get; private set; }

        public Task<string?> GetMemberRoleAsync(string username, CancellationToken ct) =>
            Task.FromResult(Roles.TryGetValue(username, out var role) ? role : null);

        public Task<string?> GetMemberRoleFreshAsync(string username, CancellationToken ct) =>
            GetMemberRoleAsync(username, ct);

        public Task<IReadOnlyDictionary<string, string>> GetMemberRolesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string>(Roles, StringComparer.OrdinalIgnoreCase));

        public Task<bool> IsImpAccountAsync(string username, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<WomRoleUpdateResult> UpdateMemberRoleAsync(
            string username,
            string role,
            CancellationToken ct,
            bool invalidateCache = true)
        {
            UpdateCalls++;
            throw new InvalidOperationException("The PostgreSQL transition fixture is manual-WOM/read-only; WOM writes must be performed outside the tool.");
        }

        public Task<(bool Success, string Details)> RemoveMemberAsync(string username, CancellationToken ct) =>
            Task.FromResult((true, "ok"));

        public Task InvalidateCacheAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingDiscord : IDiscordRoleGateway
    {
        private readonly Dictionary<string, DiscordRoleObservation> roles;
        private readonly Dictionary<string, DiscordMemberObservation> members;

        public RecordingDiscord(
            IEnumerable<DiscordRoleObservation> initialRoles,
            IEnumerable<DiscordMemberObservation>? initialMembers = null)
        {
            roles = initialRoles.ToDictionary(x => x.RoleId, StringComparer.Ordinal);
            members = (initialMembers ?? Array.Empty<DiscordMemberObservation>())
                .ToDictionary(x => x.UserId, StringComparer.Ordinal);
        }

        public string GuildId => "guild";
        public int MemberMutationCalls { get; private set; }

        public Task<DiscordGuildObservation?> GetGuildObservationAsync(CancellationToken ct) =>
            Task.FromResult<DiscordGuildObservation?>(new DiscordGuildObservation(
                GuildId,
                roles.Values.OrderBy(x => x.Position).ToArray(),
                MembersFullyObserved: true,
                Notes: null,
                Members: members.Values.OrderBy(x => x.UserId, StringComparer.Ordinal).ToArray()));

        public Task<DiscordMemberObservation?> GetMemberObservationAsync(string userId, CancellationToken ct) =>
            Task.FromResult(members.GetValueOrDefault(userId));

        public Task<DiscordRankMembershipMutationResult> ApplyRankMembershipAsync(
            DiscordRankMembershipMutationRequest request,
            CancellationToken ct)
        {
            MemberMutationCalls++;
            throw new InvalidOperationException("The manual-mode PostgreSQL fixture must not mutate Discord member roles.");
        }

        public Task<DiscordRoleMutationResult> RenameRoleAsync(
            string roleId,
            string expectedName,
            string targetName,
            CancellationToken ct)
        {
            if (!roles.TryGetValue(roleId, out var role))
            {
                return Task.FromResult(new DiscordRoleMutationResult(false, 404, "missing", null));
            }
            if (!string.Equals(role.NameBefore, expectedName, StringComparison.Ordinal))
            {
                return Task.FromResult(new DiscordRoleMutationResult(false, 409, "drift", role));
            }

            var updated = role with { NameBefore = targetName };
            roles[roleId] = updated;
            return Task.FromResult(new DiscordRoleMutationResult(true, 200, "ok", updated));
        }

        public string RoleName(string roleId) => roles[roleId].NameBefore;
    }
}
