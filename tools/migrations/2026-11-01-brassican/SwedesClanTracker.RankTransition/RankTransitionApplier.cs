using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.RankTransition;

public sealed record ApplyPrerequisites(
    string? ReviewedBy,
    bool MaintenanceWindowConfirmed,
    bool VerifiedBackupConfirmed,
    bool ManualGameReconciliationConfirmed,
    bool ExplicitApply = true)
{
    public void Validate(RankTransitionManifest manifest)
    {
        if (manifest.SchemaVersion != RankTransitionManifest.CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"Unsupported rank transition manifest schema version '{manifest.SchemaVersion}'.");
        }
        if (!string.Equals(
                manifest.CutoverApprovalPolicyVersion,
                RankTransitionManifest.ApprovedCutoverPolicyVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The manifest cutover approval policy is unsupported; create a fresh schema-v2 plan.");
        }
        if (string.IsNullOrWhiteSpace(manifest.IntegritySha256) ||
            !string.Equals(manifest.IntegritySha256, RankTransitionJson.ComputeIntegrity(manifest), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Apply requires the reviewed manifest integrity digest; load the manifest through RankTransitionManifestStore first.");
        }
        RankTransitionManifestStore.ValidateFrozenMapping(manifest.FrozenMapping);
        if (!string.Equals(manifest.FrozenMappingVersion, FrozenRankMapping.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported frozen mapping version '{manifest.FrozenMappingVersion}'.");
        }
        if (!ExplicitApply) throw new InvalidOperationException("Apply requires an explicit apply command.");
        if (!manifest.CompleteReadOnlyScope) throw new InvalidOperationException("Diagnostic partial plans cannot be applied; create a full read-only plan first.");
        if (manifest.Operations.Any(x => x.Kind == TransitionOperationKind.FinalizeApprovedPromotion) && !manifest.ApprovedForCutover)
        {
            throw new InvalidOperationException("Approved promotion finalization operations require an explicitly approved schema-v2 cutover manifest.");
        }
        if (manifest.ApprovedForCutover && manifest.Operations.Any(x => x.Kind == TransitionOperationKind.QueuePromotionCandidate))
        {
            throw new InvalidOperationException("An approved cutover manifest cannot also queue a transient pending promotion candidate.");
        }
        if (manifest.DiscordMemberRanksManual && manifest.Operations.Any(x => x.Kind == TransitionOperationKind.DiscordMemberRankRole))
        {
            throw new InvalidOperationException("A manual Discord-member manifest cannot contain Discord member mutation operations; reconcile the frozen roster manually.");
        }
        if (manifest.WomUpdatesManual && manifest.Operations.Any(x => x.Kind == TransitionOperationKind.WiseOldManRole))
        {
            throw new InvalidOperationException("A manual-WOM manifest cannot contain Wise Old Man mutation operations; reconcile WOM from the authoritative in-game roster outside the tool.");
        }
        if (!manifest.ApprovedForCutover && manifest.PromotionDecisions.Any(x => x.ApprovedForCutover))
        {
            throw new InvalidOperationException("Approved promotion decisions require the explicit approved cutover manifest flag.");
        }
        if (string.IsNullOrWhiteSpace(ReviewedBy)) throw new InvalidOperationException("A reviewed-by attestation is required.");
        if (!MaintenanceWindowConfirmed) throw new InvalidOperationException("Maintenance-window confirmation is required; the tool never stops services itself.");
        if (!VerifiedBackupConfirmed) throw new InvalidOperationException("Verified-backup confirmation is required.");
        if (!ManualGameReconciliationConfirmed) throw new InvalidOperationException("Manual game/config reconciliation confirmation is required.");
        var blocking = manifest.Warnings.Where(x => x.BlocksApply).ToArray();
        if (blocking.Length > 0)
        {
            throw new InvalidOperationException(
                "Manifest has blocking read/observation warnings: " +
                string.Join("; ", blocking.Select(x => $"{x.Code}: {x.Details}")));
        }
    }

}

public sealed record RankTransitionApplyReport(
    string ManifestId,
    IReadOnlyList<RankTransitionOperationResult> Results,
    bool Completed,
    string? Failure = null);

public sealed class TransitionDriftException(string message) : InvalidOperationException(message);

public sealed class RankTransitionApplier(
    TrackerDbContext db,
    IWiseOldManClient wiseOldManClient,
    IDiscordRoleGateway? discordRoleGateway,
    RankTransitionJournalStore? journalStore = null,
    ITransitionClock? clock = null,
    TransitionEnvironmentIdentity? environment = null)
{
    private readonly RankTransitionJournalStore _journalStore = journalStore ?? new RankTransitionJournalStore();
    private readonly ITransitionClock _clock = clock ?? new SystemTransitionClock();
    private readonly TransitionEnvironmentIdentity? _environment = environment;

    public async Task<RankTransitionApplyReport> ApplyAsync(
        RankTransitionManifest manifest,
        string journalPath,
        ApplyPrerequisites prerequisites,
        CancellationToken ct = default)
    {
        prerequisites.Validate(manifest);
        ValidateEnvironment(manifest);
        await using var executionLock = AcquireExecutionLock(journalPath);
        var journal = await _journalStore.LoadOrCreateAsync(journalPath, manifest, ct);
        var completed = new List<RankTransitionOperationResult>();

        try
        {
            await ValidatePolicyMarkerOwnershipAsync(manifest, ct);
            await PreflightAsync(manifest, journal, ct);
            await ApplyDatabaseOperationsAsync(manifest, journal, completed, ct);
            await PersistResultsAsync(journalPath, journal, completed, ct);
            completed.Clear();

            foreach (var operation in manifest.Operations.Where(x => x.IsExternal))
            {
                ct.ThrowIfCancellationRequested();
                var prior = FindResult(journal, operation.OperationId);
                if (IsTerminalApplyResult(prior)) continue;

                // Observe the exact frozen-before value first. An already
                // targeted role/member may pre-date this run and must remain
                // unowned; only after an exact-before observation do we write
                // the durable intent immediately before the remote mutation.
                var allowOwnedPartialResume = operation.Kind == TransitionOperationKind.DiscordMemberRankRole &&
                                               prior?.ExternalIntentOwned == true;
                var observedBefore = await ReadExternalStateAsync(operation, manifest, allowOwnedPartialResume, ct);
                if (Equivalent(observedBefore, operation.After))
                {
                    var alreadyApplied = AlreadyApplied(operation, observedBefore, "External state already has the frozen target; no mutation was issued.");
                    if (prior?.ExternalIntentOwned == true)
                    {
                        alreadyApplied = alreadyApplied with
                        {
                            Status = TransitionOperationStatus.Applied,
                            Details = "Recovered the frozen target after a durable write-ahead intent; treated as applied and verified.",
                            ExternalIntentOwned = true
                        };
                    }
                    await RecordAuditAsync(operation, alreadyApplied, manifest.ManifestId, ct);
                    await db.SaveChangesAsync(ct);
                    await AddAndPersistResultAsync(journalPath, journal, alreadyApplied, ct);
                    continue;
                }
                if (!allowOwnedPartialResume)
                {
                    EnsureOriginalOrTarget(operation, observedBefore, $"External state for {operation.TargetKey}");
                }
                if (!Equivalent(observedBefore, operation.Before) && !allowOwnedPartialResume)
                {
                    throw new TransitionDriftException($"External state for {operation.TargetKey} drifted before the write-ahead intent was recorded.");
                }

                // This intent owns the exact operation. If the process loses
                // the final checkpoint after a successful HTTP mutation, a
                // later apply/rollback can reconcile the target safely.
                var intent = Result(operation, TransitionOperationStatus.Planned, observedBefore, observedBefore, "Write-ahead intent recorded after exact frozen-before observation and before external mutation.", externalIntentOwned: true);
                await AddAndPersistResultAsync(journalPath, journal, intent, ct);

                RankTransitionOperationResult result;
                try
                {
                    result = operation.Kind switch
                    {
                        TransitionOperationKind.WiseOldManRole => await ApplyWomOperationAsync(operation, ct),
                        TransitionOperationKind.DiscordRoleRename => await ApplyDiscordOperationAsync(operation, manifest, ct),
                        TransitionOperationKind.DiscordMemberRankRole => await ApplyDiscordMemberOperationAsync(operation, manifest, allowOwnedPartialResume, ct),
                        _ => throw new InvalidOperationException($"Unsupported external operation kind {operation.Kind}.")
                    };
                    if (prior?.ExternalIntentOwned == true && result.Status == TransitionOperationStatus.AlreadyApplied)
                    {
                        result = result with
                        {
                            Status = TransitionOperationStatus.Applied,
                            Details = "Recovered a target observed after a prior write-ahead intent; treated as applied and verified."
                        };
                    }
                }
                catch (TransitionDriftException ex)
                {
                    // The durable intent was already written before entering
                    // this try block. A post-write verification drift must
                    // retain ownership so rollback can reconcile an
                    // acknowledged remote mutation instead of treating it as
                    // unowned external state.
                    result = Failure(operation, TransitionOperationStatus.Drifted, ex.Message, externalIntentOwned: true);
                    await RecordAuditAsync(operation, result, manifest.ManifestId, ct);
                    await db.SaveChangesAsync(ct);
                    await AddAndPersistResultAsync(journalPath, journal, result, ct);
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or WiseOldManRateLimitException or TimeoutException)
                {
                    result = Failure(operation, TransitionOperationStatus.Failed, $"Retryable external failure ({ex.GetType().Name}); the write-ahead intent remains owned for reconciliation.", externalIntentOwned: true);
                    await RecordAuditAsync(operation, result, manifest.ManifestId, ct);
                    await db.SaveChangesAsync(ct);
                    await AddAndPersistResultAsync(journalPath, journal, result, ct);
                    throw;
                }

                await RecordAuditAsync(operation, result, manifest.ManifestId, ct);
                await db.SaveChangesAsync(ct);
                await AddAndPersistResultAsync(journalPath, journal, result, ct);
            }

            await ApplyFinalizationOperationsAsync(
                manifest,
                journal,
                journalPath,
                prerequisites.ReviewedBy!,
                ct);

            return new RankTransitionApplyReport(manifest.ManifestId, journal.Results, true);
        }
        catch (Exception ex) when (ex is TransitionDriftException or HttpRequestException or WiseOldManRateLimitException or TimeoutException)
        {
            return new RankTransitionApplyReport(manifest.ManifestId, journal.Results, false, ex.Message);
        }
    }

    public async Task<RankTransitionApplyReport> RollbackAsync(
        RankTransitionManifest manifest,
        string journalPath,
        ApplyPrerequisites prerequisites,
        CancellationToken ct = default)
    {
        prerequisites.Validate(manifest);
        ValidateEnvironment(manifest);
        await using var executionLock = AcquireExecutionLock(journalPath);
        var journal = await _journalStore.LoadOrCreateAsync(journalPath, manifest, ct);

        try
        {
            if (manifest.WomUpdatesManual)
            {
                await EnsureManualWomChecksAsync(manifest, ct);
            }
            await RollbackDatabaseOperationsAsync(manifest, journal, journalPath, ct);
            foreach (var operation in manifest.Operations.Where(x => x.IsExternal).Reverse())
            {
                ct.ThrowIfCancellationRequested();
                var prior = FindResult(journal, operation.OperationId);
                RankTransitionOperationResult result;
                var auditOwnsOperation = await HasAppliedAuditAsync(operation, manifest.ManifestId, ct);
                var journalOwnsOperation = prior?.Status == TransitionOperationStatus.Applied ||
                                           prior?.ExternalIntentOwned == true;
                if (prior?.Status == TransitionOperationStatus.AlreadyApplied ||
                    (!journalOwnsOperation && !auditOwnsOperation))
                {
                    // An AlreadyApplied checkpoint may mean the target existed
                    // before this run. Never reverse that unowned state.
                    result = await ObserveExternalRollbackWithoutMutationAsync(operation, ct);
                    if (result.Status == TransitionOperationStatus.AlreadyRolledBack)
                    {
                        await AddAndPersistResultAsync(journalPath, journal, result, ct);
                        continue;
                    }
                    result = result with { Status = TransitionOperationStatus.Drifted, Details = "Target was recorded as already applied without ownership; refusing to reverse it automatically." };
                }
                else
                {
                    // Reconcile even when the journal is missing. This covers
                    // the crash window after an external success but before
                    // the durable checkpoint was written.
                    result = operation.Kind switch
                    {
                        TransitionOperationKind.WiseOldManRole => await RollbackWomOperationAsync(operation, ct),
                        TransitionOperationKind.DiscordRoleRename => await RollbackDiscordOperationAsync(operation, manifest, ct),
                        TransitionOperationKind.DiscordMemberRankRole => await RollbackDiscordMemberOperationAsync(
                            operation,
                            manifest,
                            prior?.ExternalIntentOwned == true,
                            ct),
                        _ => throw new InvalidOperationException($"Unsupported external operation kind {operation.Kind}.")
                    };
                }
                await RecordAuditAsync(operation, result, manifest.ManifestId, ct);
                await db.SaveChangesAsync(ct);
                await AddAndPersistResultAsync(journalPath, journal, result, ct);
                if (result.Status == TransitionOperationStatus.Drifted)
                {
                    throw new TransitionDriftException(result.Details);
                }
            }

            return new RankTransitionApplyReport(manifest.ManifestId, journal.Results, true);
        }
        catch (TransitionDriftException ex)
        {
            return new RankTransitionApplyReport(manifest.ManifestId, journal.Results, false, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or WiseOldManRateLimitException or TimeoutException)
        {
            return new RankTransitionApplyReport(manifest.ManifestId, journal.Results, false, ex.Message);
        }
    }

    private async Task<RankTransitionOperationResult> ObserveExternalRollbackWithoutMutationAsync(
        RankTransitionOperation operation,
        CancellationToken ct)
    {
        var actual = operation.Kind switch
        {
            TransitionOperationKind.WiseOldManRole => await ReadFreshWomRoleAsync(operation.TargetKey, ct),
            TransitionOperationKind.DiscordMemberRankRole => (await ReadDiscordMemberAsync(operation.TargetKey, ct)).EffectiveRoleHash,
            TransitionOperationKind.DiscordRoleRename => await ReadDiscordRoleNameAsync(operation.TargetKey, ct),
            _ => null
        };
        if (Equivalent(actual, operation.Before)) return AlreadyRolledBack(operation, actual, "External state is already at the frozen before value.");
        return Result(operation, TransitionOperationStatus.Drifted, actual, actual, $"External state is '{actual ?? "<missing>"}', not the frozen before value.");
    }

    private async Task ApplyDatabaseOperationsAsync(
        RankTransitionManifest manifest,
        RankTransitionJournal journal,
        List<RankTransitionOperationResult> results,
        CancellationToken ct)
    {
        var operations = manifest.Operations
            .Where(x => !x.IsExternal)
            .Where(x => x.Kind != TransitionOperationKind.FinalizeApprovedPromotion)
            .Where(x => !IsTerminalApplyResult(FindResult(journal, x.OperationId)))
            .ToArray();

        await using var transaction = await BeginTransactionIfRelationalAsync(ct);
        try
        {
            foreach (var operation in operations)
            {
                ct.ThrowIfCancellationRequested();
                var result = await ApplyDatabaseOperationAsync(operation, manifest, ct);
                results.Add(result);
            await RecordAuditAsync(operation, result, manifest.ManifestId, ct);
            }
            await AddPolicyMarkerToCurrentTransactionAsync(manifest, ct);
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        catch
        {
            // Relational providers roll back the transaction. InMemory is
            // still used for fast unit tests, so restore tracked changes there
            // as a conservative compensation path.
            if (transaction is null) await RestoreTrackedChangesAsync(ct);
            throw;
        }
    }

    // Read-only, all-target preflight. This runs before the first database
    // write so a later external drift cannot leave a partial transition.
    private async Task PreflightAsync(
        RankTransitionManifest manifest,
        RankTransitionJournal journal,
        CancellationToken ct)
    {
        // Manual-WOM reconciliation is a read-only gate and must complete
        // before any database transaction can write tracker state.
        if (manifest.WomUpdatesManual)
        {
            await EnsureManualWomChecksAsync(manifest, ct);
        }

        foreach (var operation in manifest.Operations.Where(x => ShouldPreflight(FindResult(journal, x.OperationId))))
        {
            ct.ThrowIfCancellationRequested();
            if (!operation.IsExternal) await PreflightDatabaseOperationAsync(operation, manifest, FindResult(journal, operation.OperationId), ct);
        }

        foreach (var operation in manifest.Operations.Where(x => x.Kind == TransitionOperationKind.WiseOldManRole && ShouldPreflight(FindResult(journal, x.OperationId))))
        {
            var actual = await ReadFreshWomRoleAsync(operation.TargetKey, ct);
            EnsureCheckpointState(operation, actual, FindResult(journal, operation.OperationId), $"WOM role for {operation.TargetKey}");
        }

        var discordOperations = manifest.Operations
            .Where(x => x.Kind == TransitionOperationKind.DiscordRoleRename && ShouldPreflight(FindResult(journal, x.OperationId)))
            .ToArray();
        if (discordOperations.Length > 0)
        {
            if (discordRoleGateway is null) throw new InvalidOperationException("Discord role operations exist but the Discord gateway is not configured.");
            var observation = await discordRoleGateway.GetGuildObservationAsync(ct)
                ?? throw new TransitionDriftException("Discord observation is unavailable during apply preflight.");
            if (!observation.MembersFullyObserved)
            {
                throw new TransitionDriftException("Discord membership observation is partial during apply preflight.");
            }
            foreach (var operation in discordOperations)
            {
                var role = observation.Roles.FirstOrDefault(x => string.Equals(x.RoleId, operation.TargetKey, StringComparison.Ordinal));
                EnsureCheckpointState(operation, role?.NameBefore, FindResult(journal, operation.OperationId), $"Discord role {operation.TargetKey}");
                if (role is not null)
                {
                    VerifyDiscordMetadataForPreflight(operation, role, manifest, journal);
                }
            }
        }

        var memberOperations = manifest.Operations
            .Where(x => x.Kind == TransitionOperationKind.DiscordMemberRankRole && ShouldPreflight(FindResult(journal, x.OperationId)))
            .ToArray();
        foreach (var operation in memberOperations)
        {
            var member = await ReadDiscordMemberAsync(operation.TargetKey, ct);
            var plan = FindMemberPlan(manifest, operation);
            var checkpoint = FindResult(journal, operation.OperationId);
            if (checkpoint?.Status is TransitionOperationStatus.Applied or TransitionOperationStatus.AlreadyApplied)
            {
                VerifyDiscordMemberState(member, plan, expectedAfter: true);
            }
            else if (string.Equals(member.EffectiveRoleHash, plan.ExpectedBeforeRoleHash, StringComparison.OrdinalIgnoreCase))
            {
                VerifyDiscordMemberState(member, plan, expectedAfter: false);
            }
            else if (string.Equals(member.EffectiveRoleHash, plan.ExpectedAfterRoleHash, StringComparison.OrdinalIgnoreCase))
            {
                VerifyDiscordMemberState(member, plan, expectedAfter: true);
            }
            else if (checkpoint?.ExternalIntentOwned == true && IsResumableOwnedPartial(member, plan))
            {
                // A Discord mutation can be acknowledged by the API and then
                // lose the local checkpoint, or can be interrupted after one
                // of the managed role edits.  Only a durable, owned intent
                // may reopen this narrow partial union state; an unowned
                // partial membership remains drift and blocks apply.
            }
            else
            {
                throw new TransitionDriftException($"Discord member {member.UserId} role membership drifted before apply.");
            }
        }
    }

    private async Task EnsureManualWomChecksAsync(
        RankTransitionManifest manifest,
        CancellationToken ct)
    {
        if (manifest.ManualWomChecks.Count == 0) return;

        var womRoles = await ReadFreshWomRosterAsync(
            manifest.ManualWomChecks.Select(x => x.Username),
            ct);
        var mismatches = new List<string>();
        foreach (var check in manifest.ManualWomChecks)
        {
            ct.ThrowIfCancellationRequested();
            var actual = womRoles.TryGetValue(FrozenRankMapping.Normalize(check.Username), out var role)
                ? role
                : null;
            if (!Equivalent(actual, check.RoleAfter))
            {
                mismatches.Add(
                    $"{check.Username}: expected final WOM role '{check.RoleAfter ?? "<missing>"}' " +
                    $"after manual in-game reconciliation, observed '{actual ?? "<missing>"}'");
            }
        }

        if (mismatches.Count > 0)
        {
            throw new TransitionDriftException(
                "Manual WOM reconciliation is incomplete; no tracker database writes were made. " +
                string.Join("; ", mismatches));
        }
    }

    private async Task<IReadOnlyDictionary<string, string?>> ReadFreshWomRosterAsync(
        IEnumerable<string> usernames,
        CancellationToken ct)
    {
        var names = usernames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(FrozenRankMapping.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0)
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            // The first fresh member read forces the Core client to refresh
            // its full roster cache. Read that roster once for all checks;
            // never issue one full CSV refresh per player.
            await wiseOldManClient.GetMemberRoleFreshAsync(names[0], ct);
            var roles = await wiseOldManClient.GetMemberRolesAsync(ct);
            return NormalizeWomRoster(roles);
        }
        catch (NotSupportedException)
        {
            // Legacy test/library clients may not implement the explicit
            // fresh lookup. Preserve the existing safe fallback: invalidate
            // the client boundary, then consume one complete roster fetch.
            await wiseOldManClient.InvalidateCacheAsync(ct);
            var roles = await wiseOldManClient.GetMemberRolesAsync(ct);
            return NormalizeWomRoster(roles);
        }
    }

    private static IReadOnlyDictionary<string, string?> NormalizeWomRoster(
        IReadOnlyDictionary<string, string> roles) =>
        roles
            .GroupBy(x => FrozenRankMapping.Normalize(x.Key), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                x => x.Key,
                x => x.Count() == 1 ? x.Single().Value : null,
                StringComparer.OrdinalIgnoreCase);

    private async Task PreflightDatabaseOperationAsync(
        RankTransitionOperation operation,
        RankTransitionManifest manifest,
        RankTransitionOperationResult? checkpoint,
        CancellationToken ct)
    {
        if (operation.Kind is TransitionOperationKind.DatabaseCurrentRank or TransitionOperationKind.DatabaseEligibleRank)
        {
            var player = await FindPlayerAsync(operation.TargetKey, ct)
                ?? throw new TransitionDriftException($"Player {operation.TargetKey} no longer exists.");
            await EnsurePlayerObservationStillMatchesAsync(player, operation, manifest, ct);
            var actual = operation.Kind == TransitionOperationKind.DatabaseCurrentRank ? player.CurrentRank : player.EligibleRank;
            EnsureCheckpointState(operation, actual, checkpoint, $"Database rank for player {player.Id}");
            return;
        }

        if (operation.Kind == TransitionOperationKind.QueuePromotionCandidate)
        {
            var player = await FindPlayerAsync(operation.TargetKey, ct)
                ?? throw new TransitionDriftException($"Player {operation.TargetKey} no longer exists.");
            await EnsurePlayerObservationStillMatchesAsync(player, operation, manifest, ct);
            if (player.Status != PlayerStatus.ACTIVE) throw new TransitionDriftException($"Player {player.Id} is no longer active; promotion queue drifted.");
            if (checkpoint?.Status is TransitionOperationStatus.Applied or TransitionOperationStatus.AlreadyApplied)
            {
                var exactCandidateId = checkpoint.RelatedEntityId ??
                                       await ReadAppliedAuditEntityIdAsync(operation, manifest.ManifestId, ct);
                if (!exactCandidateId.HasValue)
                {
                    throw new TransitionDriftException($"Exact generated promotion candidate ownership for player {player.Id} is missing from the applied checkpoint/audit.");
                }
                var exactCandidate = await db.PromotionCandidates.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == exactCandidateId.Value && x.PlayerId == player.Id, ct);
                if (exactCandidate is null ||
                    exactCandidate.Status != PromotionStatus.PENDING ||
                    !Equivalent(FrozenRankMapping.Normalize(exactCandidate.NewRank), operation.After))
                {
                    throw new TransitionDriftException($"Exact generated promotion candidate {exactCandidateId.Value} is missing, no longer pending, or no longer has the frozen target.");
                }
                return;
            }
            var candidates = await db.PromotionCandidates.AsNoTracking()
                .Where(x => x.PlayerId == player.Id && x.Status == PromotionStatus.PENDING)
                .Select(x => x.NewRank)
                .ToListAsync(ct);
            if (candidates.Any(x => Equivalent(FrozenRankMapping.Normalize(x), operation.After))) return;
            var snapshot = await db.PlayerSnapshots.AsNoTracking()
                .Where(x => x.PlayerId == player.Id)
                .OrderByDescending(x => x.Timestamp)
                .ThenByDescending(x => x.Id)
                .FirstOrDefaultAsync(ct);
            if (snapshot is null || !string.Equals(RankTransitionPlanner.SnapshotHash(snapshot), operation.SnapshotHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new TransitionDriftException($"Latest snapshot for player {player.Id} changed since planning; promotion queue drifted.");
            }
            return;
        }

        if (operation.Kind == TransitionOperationKind.FinalizeApprovedPromotion)
        {
            var player = await FindPlayerAsync(operation.TargetKey, ct)
                ?? throw new TransitionDriftException($"Player {operation.TargetKey} no longer exists.");
            await EnsurePlayerObservationStillMatchesAsync(player, operation, manifest, ct);
            var exactCandidateId = checkpoint?.RelatedEntityId ??
                                   await ReadAppliedAuditEntityIdAsync(operation, manifest.ManifestId, ct);
            var actual = player.CurrentRank;
            if (checkpoint?.Status is TransitionOperationStatus.Applied or TransitionOperationStatus.AlreadyApplied)
            {
                if (!exactCandidateId.HasValue)
                {
                    throw new TransitionDriftException($"Exact approved promotion history for player {player.Id} is missing from the applied checkpoint/audit.");
                }
                var candidate = await db.PromotionCandidates.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == exactCandidateId.Value && x.PlayerId == player.Id, ct);
                if (!Equivalent(actual, operation.After) ||
                    candidate is null ||
                    candidate.Status != PromotionStatus.APPROVED ||
                    !Equivalent(candidate.NewRank, operation.After))
                {
                    throw new TransitionDriftException($"Exact approved cutover state for player {player.Id} is missing or drifted.");
                }
                return;
            }
            if (Equivalent(actual, operation.After))
            {
                if (!exactCandidateId.HasValue)
                {
                    throw new TransitionDriftException($"Player {player.Id} already has the final cutover rank without owned approved history.");
                }
                var candidate = await db.PromotionCandidates.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == exactCandidateId.Value && x.PlayerId == player.Id, ct);
                if (candidate is null || candidate.Status != PromotionStatus.APPROVED || !Equivalent(candidate.NewRank, operation.After))
                {
                    throw new TransitionDriftException($"Player {player.Id} has a final rank without the exact owned approved history.");
                }
                return;
            }
            if (!Equivalent(actual, operation.Before))
            {
                throw new TransitionDriftException($"Player {player.Id} current rank drifted before approved cutover finalization.");
            }
            return;
        }

        if (operation.Kind == TransitionOperationKind.SupersedePromotionCandidate)
        {
            if (!int.TryParse(operation.TargetKey, out var candidateId)) throw new TransitionDriftException($"Invalid promotion candidate key '{operation.TargetKey}'.");
            var candidate = await db.PromotionCandidates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == candidateId, ct)
                ?? throw new TransitionDriftException($"Promotion candidate {candidateId} no longer exists.");
            var player = await db.Players.FirstOrDefaultAsync(x => x.Id == candidate.PlayerId, ct);
            if (player is not null) await EnsurePlayerObservationStillMatchesAsync(player, operation, manifest, ct);
            EnsureCheckpointState(operation, candidate.Status.ToString(), checkpoint, $"Promotion candidate {candidateId}");
        }
    }

    private static void EnsureCheckpointState(
        RankTransitionOperation operation,
        string? actual,
        RankTransitionOperationResult? checkpoint,
        string label)
    {
        if (checkpoint?.Status is TransitionOperationStatus.Applied or TransitionOperationStatus.AlreadyApplied)
        {
            if (!Equivalent(actual, operation.After))
            {
                throw new TransitionDriftException($"{label} no longer has the frozen applied target '{operation.After ?? "<missing>"}'; observed '{actual ?? "<missing>"}'.");
            }
            return;
        }
        EnsureOriginalOrTarget(operation, actual, label);
    }

    private static void EnsureOriginalOrTarget(RankTransitionOperation operation, string? actual, string label)
    {
        if (!Equivalent(actual, operation.Before) && !Equivalent(actual, operation.After))
        {
            throw new TransitionDriftException($"{label} drifted from frozen before '{operation.Before ?? "<missing>"}' to '{actual ?? "<missing>"}'.");
        }
    }

    private static void VerifyDiscordMetadata(
        RankTransitionOperation operation,
        DiscordRoleObservation actual,
        RankTransitionManifest manifest)
    {
        var frozen = manifest.DiscordRoles.FirstOrDefault(x => string.Equals(x.RoleId, operation.TargetKey, StringComparison.Ordinal));
        if (frozen is null) return;
        VerifyDiscordImmutableRoleMetadata(frozen, actual, operation.TargetKey);
        if (frozen.MembershipCount != actual.MembershipCount ||
            !string.Equals(frozen.MembershipHash, actual.MembershipHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new TransitionDriftException($"Discord role {operation.TargetKey} membership count/hash changed since planning.");
        }
        if ((frozen.MemberIds is null) != (actual.MemberIds is null) ||
            frozen.MemberIds is not null && !frozen.MemberIds.SequenceEqual(actual.MemberIds!, StringComparer.Ordinal))
        {
            throw new TransitionDriftException($"Discord role {operation.TargetKey} member IDs changed since planning.");
        }
    }

    private static void VerifyDiscordMetadataForPreflight(
        RankTransitionOperation operation,
        DiscordRoleObservation actual,
        RankTransitionManifest manifest,
        RankTransitionJournal journal)
    {
        var frozen = manifest.DiscordRoles.FirstOrDefault(x =>
            string.Equals(x.RoleId, operation.TargetKey, StringComparison.Ordinal));
        if (frozen is null) return;
        if (manifest.DiscordMemberRankChanges.Count == 0)
        {
            VerifyDiscordMetadata(operation, actual, manifest);
            return;
        }

        VerifyDiscordImmutableRoleMetadata(frozen, actual, operation.TargetKey);
        if (frozen.MemberIds is null)
        {
            if (manifest.DiscordMemberRankChanges.Any(x =>
                    x.OriginalRoleIds.Contains(frozen.RoleId, StringComparer.Ordinal) ||
                    x.ExpectedAfterRoleIds.Contains(frozen.RoleId, StringComparer.Ordinal)))
            {
                throw new TransitionDriftException($"Discord role {frozen.RoleId} lacks frozen member IDs needed for retry verification.");
            }
            if (frozen.MembershipCount != actual.MembershipCount ||
                !string.Equals(frozen.MembershipHash, actual.MembershipHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new TransitionDriftException($"Discord role {frozen.RoleId} membership changed since planning.");
            }
            return;
        }

        var expected = frozen.MemberIds.ToHashSet(StringComparer.Ordinal);
        var partialOwned = false;
        foreach (var plan in manifest.DiscordMemberRankChanges)
        {
            var memberOperation = manifest.Operations.FirstOrDefault(x =>
                x.Kind == TransitionOperationKind.DiscordMemberRankRole &&
                string.Equals(x.TargetKey, plan.DiscordUserId, StringComparison.Ordinal));
            var result = memberOperation is null ? null : FindResult(journal, memberOperation.OperationId);
            if (memberOperation is null || plan.IsNoOp ||
                result?.Status is TransitionOperationStatus.RolledBack or TransitionOperationStatus.AlreadyRolledBack)
            {
                continue;
            }
            var isAfter = result?.Status is TransitionOperationStatus.Applied or TransitionOperationStatus.AlreadyApplied;
            if (isAfter)
            {
                if (plan.OriginalRoleIds.Contains(frozen.RoleId, StringComparer.Ordinal) &&
                    !plan.ExpectedAfterRoleIds.Contains(frozen.RoleId, StringComparer.Ordinal)) expected.Remove(plan.DiscordUserId);
                if (!plan.OriginalRoleIds.Contains(frozen.RoleId, StringComparer.Ordinal) &&
                    plan.ExpectedAfterRoleIds.Contains(frozen.RoleId, StringComparer.Ordinal)) expected.Add(plan.DiscordUserId);
            }
            else if (result?.ExternalIntentOwned == true)
            {
                // The member preflight below owns the exact partial-state
                // decision. Do not falsely reject an already-applied role
                // rename merely because the member request was interrupted.
                partialOwned = true;
            }
        }

        if (partialOwned) return;
        var actualIds = actual.MemberIds?.OrderBy(x => x, StringComparer.Ordinal).ToArray()
            ?? throw new TransitionDriftException($"Discord role {frozen.RoleId} member IDs are unavailable during retry preflight.");
        var expectedIds = expected.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (!expectedIds.SequenceEqual(actualIds, StringComparer.Ordinal) ||
            actual.MembershipCount != expectedIds.Length ||
            !string.Equals(
                DiscordMemberTransitionHelpers.ComputeRoleHash(expectedIds),
                actual.MembershipHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new TransitionDriftException($"Discord role {frozen.RoleId} member IDs changed during retry preflight.");
        }
    }

    private static void VerifyDiscordImmutableRoleMetadata(
        DiscordRoleObservation frozen,
        DiscordRoleObservation actual,
        string roleId)
    {
        if (!string.Equals(frozen.Permissions, actual.Permissions, StringComparison.Ordinal) ||
            frozen.Position != actual.Position ||
            frozen.Protected != actual.Protected)
        {
            throw new TransitionDriftException($"Discord role {roleId} permissions, protection, or hierarchy position changed since planning.");
        }
    }

    private async Task EnsurePolicyMarkerAsync(RankTransitionManifest manifest, CancellationToken ct)
    {
        await using var transaction = await BeginTransactionIfRelationalAsync(ct);
        await AddPolicyMarkerToCurrentTransactionAsync(manifest, ct);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    private async Task AddPolicyMarkerToCurrentTransactionAsync(RankTransitionManifest manifest, CancellationToken ct)
    {
        var markers = await db.LifecycleEvents
            .AsNoTracking()
            .Where(x => x.EventType == "RANK_TRANSITION_POLICY_MARKER" && x.MetadataJson.Contains(FrozenRankMapping.Version))
            .Select(x => x.MetadataJson)
            .ToListAsync(ct);
        if (markers.Count > 0)
        {
            if (markers.Any(x => !string.Equals(ReadMetadataValue(x, "ManifestId"), manifest.ManifestId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("A different manifest already owns the frozen rank transition policy marker; resume that manifest instead.");
            }
            return;
        }
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = 0,
            EventType = "RANK_TRANSITION_POLICY_MARKER",
            MetadataJson = JsonSerializer.Serialize(new
            {
                ManifestId = manifest.ManifestId,
                MappingVersion = manifest.FrozenMappingVersion,
                DatabaseIdentity = manifest.Environment.DatabaseIdentity,
                WiseOldManGroupId = manifest.Environment.WiseOldManGroupId,
                DiscordGuildId = manifest.Environment.DiscordGuildId,
                ApprovedForCutover = manifest.ApprovedForCutover,
                DiscordMemberRanksManual = manifest.DiscordMemberRanksManual,
                WomUpdatesManual = manifest.WomUpdatesManual,
                StartedAt = _clock.UtcNow
            }),
            Status = "OPEN",
            CreatedAt = _clock.UtcNow
        });
    }

    private async Task ValidatePolicyMarkerOwnershipAsync(RankTransitionManifest manifest, CancellationToken ct)
    {
        var markers = await db.LifecycleEvents
            .AsNoTracking()
            .Where(x => x.EventType == "RANK_TRANSITION_POLICY_MARKER" && x.MetadataJson.Contains(FrozenRankMapping.Version))
            .Select(x => x.MetadataJson)
            .ToListAsync(ct);
        if (markers.Any(x => !string.Equals(ReadMetadataValue(x, "ManifestId"), manifest.ManifestId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("A different manifest already owns the frozen rank transition policy marker; resume that manifest instead.");
        }
    }

    private async Task RestoreTrackedChangesAsync(CancellationToken ct)
    {
        foreach (var entry in db.ChangeTracker.Entries().ToArray())
        {
            if (entry.State == EntityState.Added)
            {
                entry.State = EntityState.Detached;
                continue;
            }
            if (entry.State == EntityState.Modified || entry.State == EntityState.Deleted)
            {
                entry.CurrentValues.SetValues(entry.OriginalValues);
                entry.State = EntityState.Unchanged;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<RankTransitionOperationResult> ApplyDatabaseOperationAsync(
        RankTransitionOperation operation,
        RankTransitionManifest manifest,
        CancellationToken ct)
    {
        switch (operation.Kind)
        {
            case TransitionOperationKind.DatabaseCurrentRank:
            case TransitionOperationKind.DatabaseEligibleRank:
            {
                var player = await FindPlayerAsync(operation.TargetKey, ct)
                    ?? throw new TransitionDriftException($"Player {operation.TargetKey} no longer exists.");
                await EnsurePlayerObservationStillMatchesAsync(player, operation, manifest, ct);
                var property = operation.Kind == TransitionOperationKind.DatabaseCurrentRank
                    ? nameof(Player.CurrentRank)
                    : nameof(Player.EligibleRank);
                var actual = property == nameof(Player.CurrentRank) ? player.CurrentRank : player.EligibleRank;
                if (Equivalent(actual, operation.After)) return AlreadyApplied(operation, actual, "Database already has the frozen target.");
                if (!Equivalent(actual, operation.Before))
                {
                    throw new TransitionDriftException($"{property} for player {player.Id} drifted from '{operation.Before}' to '{actual}'.");
                }
                if (property == nameof(Player.CurrentRank)) player.CurrentRank = operation.After!;
                else player.EligibleRank = operation.After!;
                return Applied(operation, actual, operation.After!, "Database rank normalized.");
            }

            case TransitionOperationKind.QueuePromotionCandidate:
            {
                var player = await FindPlayerAsync(operation.TargetKey, ct)
                    ?? throw new TransitionDriftException($"Player {operation.TargetKey} no longer exists.");
                await EnsurePlayerObservationStillMatchesAsync(player, operation, manifest, ct);
                if (player.Status != PlayerStatus.ACTIVE)
                {
                    return Skipped(operation, player.CurrentRank, "Player is no longer active; promotion was not queued.");
                }

                var snapshot = await db.PlayerSnapshots
                    .AsNoTracking()
                    .Where(x => x.PlayerId == player.Id)
                    .OrderByDescending(x => x.Timestamp)
                    .ThenByDescending(x => x.Id)
                    .FirstOrDefaultAsync(ct);
                if (snapshot is null || !string.IsNullOrWhiteSpace(operation.SnapshotHash) &&
                    !string.Equals(RankTransitionPlanner.SnapshotHash(snapshot), operation.SnapshotHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new TransitionDriftException($"Latest snapshot for player {player.Id} changed since planning; promotion candidate was not queued.");
                }

                var existing = await db.PromotionCandidates
                    .Where(x => x.PlayerId == player.Id)
                    .ToListAsync(ct);
                var trackedCandidates = db.ChangeTracker.Entries<PromotionCandidate>()
                    .ToDictionary(x => x.Entity.Id, x => x.Entity);
                var hasEquivalentPending = existing.Any(candidate =>
                {
                    var current = trackedCandidates.TryGetValue(candidate.Id, out var tracked)
                        ? tracked
                        : candidate;
                    return current.Status == PromotionStatus.PENDING &&
                           Equivalent(FrozenRankMapping.Normalize(current.NewRank), operation.After);
                }) || trackedCandidates.Values.Any(candidate =>
                    candidate.PlayerId == player.Id &&
                    candidate.Status == PromotionStatus.PENDING &&
                    Equivalent(FrozenRankMapping.Normalize(candidate.NewRank), operation.After));
                if (hasEquivalentPending)
                {
                    return AlreadyApplied(operation, PromotionStatus.PENDING.ToString(), "Equivalent pending promotion already exists.");
                }

                // Use the frozen observation captured in the manifest. Never
                // remap the new candidate rank, and never infer a role from a
                // mutable/live retry state.
                var womObservation = manifest.WomObservations.FirstOrDefault(x =>
                    string.Equals(FrozenRankMapping.Normalize(x.Username), FrozenRankMapping.Normalize(player.Username), StringComparison.OrdinalIgnoreCase));
                var womRole = womObservation?.RoleTarget;
                var reason = manifest.PromotionDecisions
                    .FirstOrDefault(x => x.PlayerId == player.Id && Equivalent(x.EvaluatedEligibleRank, operation.After))?.Reason
                    ?? operation.Reason;
                var candidate = new PromotionCandidate
                {
                    PlayerId = player.Id,
                    Player = player,
                    OldRank = player.CurrentRank,
                    NewRank = operation.After!,
                    Reason = reason.Length > 2048 ? reason[..2048] : reason,
                    CandidateType = RankRules.ClassifyPromotionCandidate(operation.After!, womRole),
                    WomRoleAtEvaluation = string.IsNullOrWhiteSpace(womRole) ? null : FrozenRankMapping.Normalize(womRole),
                    Status = PromotionStatus.PENDING,
                    CreatedAt = _clock.UtcNow
                };
                db.PromotionCandidates.Add(candidate);
                // Flush inside the surrounding transaction so the generated
                // candidate ID is captured in the durable audit before the
                // transaction commits. Rollback must never choose a different
                // pending candidate merely because its rank matches.
                await db.SaveChangesAsync(ct);
                return Applied(operation, null, operation.After, "Pending promotion candidate queued for normal approval.", candidate.Id);
            }

            case TransitionOperationKind.SupersedePromotionCandidate:
            {
                if (!int.TryParse(operation.TargetKey, out var candidateId))
                    throw new TransitionDriftException($"Invalid promotion candidate key '{operation.TargetKey}'.");
                var candidate = await db.PromotionCandidates.FirstOrDefaultAsync(x => x.Id == candidateId, ct)
                    ?? throw new TransitionDriftException($"Promotion candidate {candidateId} no longer exists.");
                var candidatePlayer = await db.Players.FirstOrDefaultAsync(x => x.Id == candidate.PlayerId, ct);
                if (candidatePlayer is not null) await EnsurePlayerObservationStillMatchesAsync(candidatePlayer, operation, manifest, ct);
                var actual = candidate.Status.ToString();
                if (Equivalent(actual, operation.After)) return AlreadyApplied(operation, actual, "Candidate was already superseded.");
                if (!Equivalent(actual, operation.Before)) throw new TransitionDriftException($"Promotion candidate {candidateId} drifted to {actual}.");
                candidate.Status = PromotionStatus.DISMISSED;
                db.LifecycleEvents.Add(new LifecycleEvent
                {
                    PlayerId = candidate.PlayerId,
                    EventType = "RANK_TRANSITION_CANDIDATE_SUPERSEDED",
                    MetadataJson = JsonSerializer.Serialize(new
                    {
                        ManifestId = manifest.ManifestId,
                        OperationId = operation.OperationId,
                        CandidateId = candidateId,
                        Before = operation.Before,
                        After = operation.After,
                        Reason = operation.Reason
                    }),
                    Status = "DONE",
                    CreatedAt = _clock.UtcNow
                });
                return Applied(operation, actual, operation.After, "Old-policy candidate superseded; history preserved.");
            }

            default:
                throw new InvalidOperationException($"Unsupported database operation kind {operation.Kind}.");
        }
    }

    private async Task ApplyFinalizationOperationsAsync(
        RankTransitionManifest manifest,
        RankTransitionJournal journal,
        string journalPath,
        string reviewedBy,
        CancellationToken ct)
    {
        var operations = manifest.Operations
            .Where(x => x.Kind == TransitionOperationKind.FinalizeApprovedPromotion)
            .Where(x => !IsTerminalApplyResult(FindResult(journal, x.OperationId)))
            .ToArray();
        if (operations.Length == 0) return;

        // Refresh every required external target immediately before the
        // irreversible approved-history transaction. This also covers
        // no-op member plans, which have no external checkpoint of their own.
        await VerifyApprovedExternalTargetsAsync(manifest, ct);

        await using var transaction = await BeginTransactionIfRelationalAsync(ct);
        var results = new List<RankTransitionOperationResult>(operations.Length);
        try
        {
            foreach (var operation in operations)
            {
                ct.ThrowIfCancellationRequested();
                var result = await ApplyFinalizationOperationAsync(
                    operation,
                    manifest,
                    journal,
                    reviewedBy,
                    ct);
                results.Add(result);
                await RecordAuditAsync(operation, result, manifest.ManifestId, ct);
            }

            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        catch
        {
            if (transaction is null) await RestoreTrackedChangesAsync(ct);
            throw;
        }

        foreach (var result in results)
        {
            await AddAndPersistResultAsync(journalPath, journal, result, ct);
        }
    }

    private async Task VerifyApprovedExternalTargetsAsync(
        RankTransitionManifest manifest,
        CancellationToken ct)
    {
        if ((manifest.DiscordMemberRanksManual || manifest.DiscordMemberRankChanges.Count > 0) &&
            manifest.DiscordRoles.Count == 0)
        {
            throw new TransitionDriftException("Approved cutover has no frozen Discord role metadata to verify.");
        }
        var approvedDecisions = manifest.PromotionDecisions
            .Where(x => x.ApprovedForCutover && x.IsActive)
            .ToArray();
        var freshManualWomRoles = manifest.WomUpdatesManual
            ? await ReadFreshWomRosterAsync(approvedDecisions.Select(x => x.Username), ct)
            : null;
        foreach (var decision in approvedDecisions)
        {
            var wom = manifest.WomObservations.FirstOrDefault(x =>
                string.Equals(
                    FrozenRankMapping.Normalize(x.Username),
                    FrozenRankMapping.Normalize(decision.Username),
                    StringComparison.OrdinalIgnoreCase));
            if (wom is null || !wom.FreshObservation || wom.Protected || string.IsNullOrWhiteSpace(wom.RoleTarget))
            {
                throw new TransitionDriftException(
                    $"Approved cutover WOM target for {decision.Username} is missing, protected, or not fresh.");
            }

            var actual = freshManualWomRoles is null
                ? await ReadFreshWomRoleAsync(decision.Username, ct)
                : freshManualWomRoles.TryGetValue(FrozenRankMapping.Normalize(decision.Username), out var role)
                    ? role
                    : null;
            if (!Equivalent(actual, wom.RoleTarget))
            {
                throw new TransitionDriftException(
                    $"WOM role for approved cutover player {decision.Username} drifted from final target '{wom.RoleTarget}' to '{actual ?? "<missing>"}'.");
            }
        }

        if (manifest.DiscordMemberRanksManual)
        {
            // Manual mode intentionally does not require the suggested
            // member target. It only freezes and rechecks the exact roster
            // observed during planning before approving DB history.
            if (manifest.DiscordMembers.Count == 0)
            {
                throw new TransitionDriftException("Manual Discord-member mode has no frozen member roster.");
            }
        }
        else
        {
            foreach (var plan in manifest.DiscordMemberRankChanges)
            {
                var member = await ReadDiscordMemberAsync(plan.DiscordUserId, ct);
                // Includes IsNoOp plans: they have no mutation/checkpoint, so
                // this is their only just-in-time finalization guard.
                VerifyDiscordMemberState(member, plan, expectedAfter: true);
            }
        }

        if (!manifest.DiscordMemberRanksManual &&
            manifest.DiscordMemberRankChanges.Count == 0 &&
            !manifest.Operations.Any(x => x.Kind == TransitionOperationKind.DiscordRoleRename))
        {
            return;
        }

        if (discordRoleGateway is null)
        {
            throw new TransitionDriftException("Discord is required to verify approved cutover targets, but the gateway is unavailable.");
        }

        var guild = await discordRoleGateway.GetGuildObservationAsync(ct)
            ?? throw new TransitionDriftException("Discord guild observation is unavailable before approved cutover finalization.");
        if (!guild.MembersFullyObserved)
        {
            throw new TransitionDriftException("Discord membership observation is partial before approved cutover finalization.");
        }

        foreach (var frozen in manifest.DiscordRoles)
        {
            var actual = guild.Roles.FirstOrDefault(x => string.Equals(x.RoleId, frozen.RoleId, StringComparison.Ordinal))
                ?? throw new TransitionDriftException($"Discord role {frozen.RoleId} is missing before approved cutover finalization.");
            var rename = manifest.Operations.FirstOrDefault(x =>
                x.Kind == TransitionOperationKind.DiscordRoleRename &&
                string.Equals(x.TargetKey, frozen.RoleId, StringComparison.Ordinal));
            var expectedName = rename?.After ?? frozen.NameTarget ?? frozen.NameBefore;
            if (!Equivalent(actual.NameBefore, expectedName))
            {
                throw new TransitionDriftException(
                    $"Discord role {frozen.RoleId} name drifted from final target '{expectedName}' to '{actual.NameBefore}'.");
            }
            VerifyDiscordMetadataAfterMemberChanges(frozen, actual, manifest);
        }

        if (manifest.DiscordMemberRanksManual)
        {
            VerifyManualDiscordRoster(guild, manifest);
        }
    }

    private static void VerifyManualDiscordRoster(
        DiscordGuildObservation guild,
        RankTransitionManifest manifest)
    {
        var expected = manifest.DiscordMembers
            .GroupBy(x => x.UserId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var actual = (guild.Members ?? [])
            .GroupBy(x => x.UserId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        if (expected.Count != actual.Count || expected.Keys.Any(x => !actual.ContainsKey(x)))
        {
            throw new TransitionDriftException("Discord member roster changed since the manual cutover plan.");
        }
        foreach (var (userId, frozen) in expected)
        {
            var live = actual[userId];
            if (!string.Equals(frozen.EffectiveRoleHash, live.EffectiveRoleHash, StringComparison.OrdinalIgnoreCase) ||
                !DiscordMemberTransitionHelpers.CanonicalRoleIds(frozen.RoleIds)
                    .ToHashSet(StringComparer.Ordinal)
                    .SetEquals(live.RoleIds))
            {
                throw new TransitionDriftException($"Discord member {userId} identity or roles changed before manual cutover finalization.");
            }
        }
    }

    private static void VerifyDiscordMetadataAfterMemberChanges(
        DiscordRoleObservation frozen,
        DiscordRoleObservation actual,
        RankTransitionManifest manifest)
    {
        if (!string.Equals(frozen.Permissions, actual.Permissions, StringComparison.Ordinal) ||
            frozen.Position != actual.Position ||
            frozen.Protected != actual.Protected)
        {
            throw new TransitionDriftException(
                $"Discord role {frozen.RoleId} permissions, protection, or hierarchy position changed since planning.");
        }

        var expectedMemberIds = frozen.MemberIds is null
            ? null
            : manifest.DiscordMemberRanksManual
                ? frozen.MemberIds.OrderBy(x => x, StringComparer.Ordinal).ToArray()
                : ExpectedRoleMemberIdsAfter(frozen, manifest);
        if (expectedMemberIds is null)
        {
            if (manifest.DiscordMemberRankChanges.Any(x =>
                    x.OriginalRoleIds.Contains(frozen.RoleId, StringComparer.Ordinal) ||
                    x.ExpectedAfterRoleIds.Contains(frozen.RoleId, StringComparer.Ordinal)))
            {
                throw new TransitionDriftException(
                    $"Discord role {frozen.RoleId} lacks frozen member IDs needed to verify approved membership changes.");
            }
            if (frozen.MembershipCount != actual.MembershipCount ||
                !string.Equals(frozen.MembershipHash, actual.MembershipHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new TransitionDriftException($"Discord role {frozen.RoleId} membership changed since planning.");
            }
            return;
        }

        var actualMemberIds = actual.MemberIds?.OrderBy(x => x, StringComparer.Ordinal).ToArray()
            ?? throw new TransitionDriftException($"Discord role {frozen.RoleId} member IDs are unavailable during finalization.");
        if (!expectedMemberIds.SequenceEqual(actualMemberIds, StringComparer.Ordinal) ||
            actual.MembershipCount != expectedMemberIds.Count ||
            !string.Equals(
                DiscordMemberTransitionHelpers.ComputeRoleHash(expectedMemberIds),
                actual.MembershipHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new TransitionDriftException($"Discord role {frozen.RoleId} member IDs changed before approved cutover finalization.");
        }
    }

    private static IReadOnlyList<string> ExpectedRoleMemberIdsAfter(
        DiscordRoleObservation frozen,
        RankTransitionManifest manifest)
    {
        var members = frozen.MemberIds!.ToHashSet(StringComparer.Ordinal);
        foreach (var plan in manifest.DiscordMemberRankChanges)
        {
            var wasMember = plan.OriginalRoleIds.Contains(frozen.RoleId, StringComparer.Ordinal);
            var isMember = plan.ExpectedAfterRoleIds.Contains(frozen.RoleId, StringComparer.Ordinal);
            if (wasMember && !isMember) members.Remove(plan.DiscordUserId);
            if (!wasMember && isMember) members.Add(plan.DiscordUserId);
        }
        return members.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    private async Task<RankTransitionOperationResult> ApplyFinalizationOperationAsync(
        RankTransitionOperation operation,
        RankTransitionManifest manifest,
        RankTransitionJournal journal,
        string reviewedBy,
        CancellationToken ct)
    {
        var player = await FindPlayerAsync(operation.TargetKey, ct)
            ?? throw new TransitionDriftException($"Player {operation.TargetKey} no longer exists.");
        await EnsurePlayerObservationStillMatchesAsync(player, operation, manifest, ct);
        if (player.Status != PlayerStatus.ACTIVE)
        {
            throw new TransitionDriftException($"Player {player.Id} is no longer active; approved cutover finalization is held.");
        }

        var checkpoint = FindResult(journal, operation.OperationId);
        var ownedCandidateId = checkpoint?.RelatedEntityId ??
                               await ReadAppliedAuditEntityIdAsync(operation, manifest.ManifestId, ct);
        // A relational finalization transaction can commit the approved row
        // and audit before the process loses the final journal checkpoint.
        // The exact candidate ID recovered from that manifest+operation audit
        // is durable ownership, so reconcile it just like an applied
        // checkpoint. Never infer ownership from the player's rank alone.
        if (checkpoint?.Status is TransitionOperationStatus.Applied or TransitionOperationStatus.AlreadyApplied ||
            ownedCandidateId.HasValue)
        {
            if (!ownedCandidateId.HasValue)
            {
                throw new TransitionDriftException($"Owned approved promotion history for player {player.Id} is missing from the checkpoint/audit.");
            }

            var existing = await db.PromotionCandidates.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == ownedCandidateId.Value && x.PlayerId == player.Id, ct);
            if (existing is null ||
                existing.Status != PromotionStatus.APPROVED ||
                !Equivalent(existing.NewRank, operation.After))
            {
                throw new TransitionDriftException($"Exact approved promotion history {ownedCandidateId.Value} is missing, no longer approved, or has the wrong final rank.");
            }
            if (!Equivalent(player.CurrentRank, operation.After))
            {
                throw new TransitionDriftException($"Player {player.Id} no longer has the frozen approved cutover rank '{operation.After}'.");
            }
            return AlreadyApplied(operation, player.CurrentRank, "Owned approved cutover promotion and final rank already exist.") with
            {
                RelatedEntityId = ownedCandidateId.Value
            };
        }

        var actual = player.CurrentRank;
        if (Equivalent(actual, operation.After))
        {
            throw new TransitionDriftException($"Player {player.Id} already has the final cutover rank but no owned approved history; refusing to claim an unrelated approval.");
        }
        if (!Equivalent(actual, operation.Before))
        {
            throw new TransitionDriftException($"Player {player.Id} current rank drifted from frozen '{operation.Before}' to '{actual}'.");
        }

        var decision = manifest.PromotionDecisions.FirstOrDefault(x =>
            x.PlayerId == player.Id && x.ApprovedForCutover);
        var womTarget = manifest.WomObservations.FirstOrDefault(x =>
            string.Equals(FrozenRankMapping.Normalize(x.Username), FrozenRankMapping.Normalize(player.Username), StringComparison.OrdinalIgnoreCase))?.RoleTarget;
        var reason = decision?.Reason ?? operation.Reason;
        var candidate = new PromotionCandidate
        {
            PlayerId = player.Id,
            Player = player,
            OldRank = operation.Before ?? player.CurrentRank,
            NewRank = operation.After ?? throw new TransitionDriftException("Approved cutover operation has no final rank."),
            Reason = reason.Length > 2048 ? reason[..2048] : reason,
            CandidateType = RankRules.ClassifyPromotionCandidate(operation.After!, womTarget),
            WomRoleAtEvaluation = string.IsNullOrWhiteSpace(womTarget) ? null : FrozenRankMapping.Normalize(womTarget),
            Status = PromotionStatus.APPROVED,
            CreatedAt = _clock.UtcNow
        };
        db.PromotionCandidates.Add(candidate);
        player.CurrentRank = operation.After!;
        await db.SaveChangesAsync(ct);

        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = player.Id,
            EventType = "RANK_TRANSITION_PROMOTION_APPROVED",
            MetadataJson = JsonSerializer.Serialize(new
            {
                ManifestId = manifest.ManifestId,
                OperationId = operation.OperationId,
                CandidateId = candidate.Id,
                Player = player.Username,
                OldRank = operation.Before,
                MappedStanding = decision?.CurrentRank,
                NewRank = operation.After,
                WomRole = womTarget,
                SnapshotId = operation.SnapshotId,
                SnapshotHash = operation.SnapshotHash,
                Reason = reason,
                HandledBy = reviewedBy,
                Source = "rank-transition-approved-cutover"
            }),
            Status = "DONE",
            CreatedAt = _clock.UtcNow
        });

        return Applied(
            operation,
            actual,
            player.CurrentRank,
            $"Approved cutover promotion finalized by {reviewedBy}; exact approved history was recorded.",
            candidate.Id);
    }

    private async Task<RankTransitionOperationResult> ApplyWomOperationAsync(
        RankTransitionOperation operation,
        CancellationToken ct)
    {
        var username = operation.TargetKey;
        var actual = await ReadFreshWomRoleAsync(username, ct);
        if (Equivalent(actual, operation.After)) return AlreadyApplied(operation, actual, "WOM already has the frozen target role.");
        if (!Equivalent(actual, operation.Before))
        {
            throw new TransitionDriftException($"WOM role for {username} drifted from '{operation.Before}' to '{actual ?? "<missing>"}'.");
        }

        var update = await wiseOldManClient.UpdateMemberRoleAsync(username, operation.After!, ct);
        if (!update.Success)
        {
            throw new HttpRequestException($"WOM role update failed for {username}: {update.Details}");
        }
        var verified = await ReadFreshWomRoleAsync(username, ct);
        if (!Equivalent(verified, operation.After))
        {
            throw new TransitionDriftException($"WOM acknowledged the update for {username}, but verification observed '{verified ?? "<missing>"}'.");
        }
        return Applied(operation, actual, verified, "WOM role updated and verified.");
    }

    private async Task<string?> ReadExternalStateAsync(
        RankTransitionOperation operation,
        RankTransitionManifest manifest,
        bool allowOwnedPartialResume,
        CancellationToken ct)
    {
        if (operation.Kind == TransitionOperationKind.WiseOldManRole)
        {
            return await ReadFreshWomRoleAsync(operation.TargetKey, ct);
        }
        if (operation.Kind == TransitionOperationKind.DiscordMemberRankRole)
        {
            var member = await ReadDiscordMemberAsync(operation.TargetKey, ct);
            var plan = FindMemberPlan(manifest, operation);
            if (!string.Equals(member.EffectiveRoleHash, plan.ExpectedBeforeRoleHash, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(member.EffectiveRoleHash, plan.ExpectedAfterRoleHash, StringComparison.OrdinalIgnoreCase) &&
                !(allowOwnedPartialResume && IsResumableOwnedPartial(member, plan)))
            {
                throw new TransitionDriftException($"Discord member {member.UserId} role membership drifted before the write-ahead intent.");
            }
            if (string.Equals(member.EffectiveRoleHash, plan.ExpectedBeforeRoleHash, StringComparison.OrdinalIgnoreCase))
            {
                VerifyDiscordMemberState(member, plan, expectedAfter: false);
            }
            return member.EffectiveRoleHash;
        }
        if (operation.Kind != TransitionOperationKind.DiscordRoleRename)
        {
            throw new InvalidOperationException($"Unsupported external operation kind {operation.Kind}.");
        }
        if (discordRoleGateway is null) throw new InvalidOperationException("Discord role operations exist but the Discord gateway is not configured.");
        var observation = await discordRoleGateway.GetGuildObservationAsync(ct)
            ?? throw new TransitionDriftException("Discord observation is unavailable before the write-ahead intent.");
        if (!observation.MembersFullyObserved)
        {
            throw new TransitionDriftException("Discord membership observation is partial; no role mutation is permitted.");
        }
        var role = observation.Roles.FirstOrDefault(x => string.Equals(x.RoleId, operation.TargetKey, StringComparison.Ordinal))
            ?? throw new TransitionDriftException($"Discord role {operation.TargetKey} is missing before the write-ahead intent.");
        VerifyDiscordMetadata(operation, role, manifest);
        return role.NameBefore;
    }

    private async Task<RankTransitionOperationResult> ApplyDiscordOperationAsync(
        RankTransitionOperation operation,
        RankTransitionManifest manifest,
        CancellationToken ct)
    {
        if (discordRoleGateway is null) throw new InvalidOperationException("Discord gateway is not configured.");
        var observed = await discordRoleGateway.GetGuildObservationAsync(ct)
            ?? throw new HttpRequestException("Discord guild observation is unavailable.");
        if (!observed.MembersFullyObserved)
        {
            throw new TransitionDriftException("Discord membership observation is partial; rollback is held.");
        }
        var role = observed.Roles.FirstOrDefault(x => string.Equals(x.RoleId, operation.TargetKey, StringComparison.Ordinal));
        var actual = role?.NameBefore;
        if (Equivalent(actual, operation.After)) return AlreadyApplied(operation, actual, "Discord role already has the frozen target name.");
        if (!Equivalent(actual, operation.Before))
        {
            throw new TransitionDriftException($"Discord role {operation.TargetKey} drifted from '{operation.Before}' to '{actual ?? "<missing>"}'.");
        }
        if (role is null) throw new TransitionDriftException($"Discord role {operation.TargetKey} is missing.");
        VerifyDiscordMetadata(operation, role, manifest);

        var update = await discordRoleGateway.RenameRoleAsync(operation.TargetKey, operation.Before!, operation.After!, ct);
        if (!update.Success) throw new HttpRequestException($"Discord role update failed: {update.Details}");
        var verifiedSnapshot = await discordRoleGateway.GetGuildObservationAsync(ct)
            ?? throw new HttpRequestException("Discord verification observation is unavailable.");
        var verifiedRole = verifiedSnapshot.Roles.FirstOrDefault(x => string.Equals(x.RoleId, operation.TargetKey, StringComparison.Ordinal));
        if (verifiedRole is null || !Equivalent(verifiedRole.NameBefore, operation.After))
        {
            throw new TransitionDriftException($"Discord acknowledged role rename, but verification observed '{verifiedRole?.NameBefore ?? "<missing>"}'.");
        }
        VerifyDiscordMetadata(operation, verifiedRole, manifest);
        return Applied(operation, actual, verifiedRole.NameBefore, "Discord role renamed by ID and verified; memberships/permissions were not changed.");
    }

    private async Task<RankTransitionOperationResult> ApplyDiscordMemberOperationAsync(
        RankTransitionOperation operation,
        RankTransitionManifest manifest,
        bool resumeOwnedPartial,
        CancellationToken ct)
    {
        if (discordRoleGateway is null) throw new InvalidOperationException("Discord gateway is not configured.");
        var plan = FindMemberPlan(manifest, operation);
        var member = await ReadDiscordMemberAsync(operation.TargetKey, ct);
        if (string.Equals(member.EffectiveRoleHash, plan.ExpectedBeforeRoleHash, StringComparison.OrdinalIgnoreCase))
        {
            VerifyDiscordMemberState(member, plan, expectedAfter: false);
        }
        else if (!resumeOwnedPartial || !IsResumableOwnedPartial(member, plan))
        {
            throw new TransitionDriftException($"Discord member {member.UserId} role membership is not the frozen before state or an owned resumable partial state.");
        }

        var update = await discordRoleGateway.ApplyRankMembershipAsync(plan.ToMutationRequest(resumeOwnedPartial), ct);
        if (!update.Success)
        {
            throw new HttpRequestException($"Discord member rank-role update failed: {update.Details}");
        }

        var verified = await ReadDiscordMemberAsync(operation.TargetKey, ct);
        VerifyDiscordMemberState(verified, plan, expectedAfter: true);
        return Applied(
            operation,
            plan.ExpectedBeforeRoleHash,
            plan.ExpectedAfterRoleHash,
            "Discord member rank-role change applied and verified; unmanaged/special roles were preserved.");
    }

    private async Task<RankTransitionOperationResult> RollbackDiscordMemberOperationAsync(
        RankTransitionOperation operation,
        RankTransitionManifest manifest,
        bool allowOwnedPartialResume,
        CancellationToken ct)
    {
        if (discordRoleGateway is null) throw new InvalidOperationException("Discord gateway is not configured.");
        var plan = FindMemberPlan(manifest, operation);
        var member = await ReadDiscordMemberAsync(operation.TargetKey, ct);
        if (string.Equals(member.EffectiveRoleHash, plan.ExpectedBeforeRoleHash, StringComparison.OrdinalIgnoreCase))
        {
            VerifyDiscordMemberState(member, plan, expectedAfter: false);
            return AlreadyRolledBack(operation, member.EffectiveRoleHash, "Discord member already has the frozen before role set.");
        }
        if (!string.Equals(member.EffectiveRoleHash, plan.ExpectedAfterRoleHash, StringComparison.OrdinalIgnoreCase) &&
            !(allowOwnedPartialResume && IsResumableOwnedPartial(member, plan)))
        {
            throw new TransitionDriftException(
                $"Cannot roll back Discord member {member.UserId}; current role membership is neither the frozen after state nor an owned resumable partial state.");
        }
        var update = await discordRoleGateway.ApplyRankMembershipAsync(plan.ToMutationRequest().ForRollback(), ct);
        if (!update.Success)
        {
            throw new HttpRequestException($"Discord member rank-role rollback failed: {update.Details}");
        }
        var verified = await ReadDiscordMemberAsync(operation.TargetKey, ct);
        VerifyDiscordMemberState(verified, plan, expectedAfter: false);
        return RolledBack(
            operation,
            plan.ExpectedAfterRoleHash,
            plan.ExpectedBeforeRoleHash,
            "Discord member rank-role change rolled back and verified; unmanaged/special roles were preserved.");
    }

    private async Task<DiscordMemberObservation> ReadDiscordMemberAsync(
        string userId,
        CancellationToken ct)
    {
        if (discordRoleGateway is null) throw new InvalidOperationException("Discord gateway is not configured.");
        return await discordRoleGateway.GetMemberObservationAsync(userId, ct)
            ?? throw new TransitionDriftException($"Discord member {userId} is missing from the live observation.");
    }

    private static DiscordRankMembershipPlan FindMemberPlan(
        RankTransitionManifest manifest,
        RankTransitionOperation operation) =>
        manifest.DiscordMemberRankChanges.FirstOrDefault(x =>
            string.Equals(x.DiscordUserId, operation.TargetKey, StringComparison.Ordinal))
        ?? throw new TransitionDriftException($"Frozen Discord member rank plan for '{operation.TargetKey}' is missing.");

    private static void VerifyDiscordMemberState(
        DiscordMemberObservation member,
        DiscordRankMembershipPlan plan,
        bool expectedAfter)
    {
        var expectedRoleIds = expectedAfter ? plan.ExpectedAfterRoleIds : plan.OriginalRoleIds;
        var expectedHash = expectedAfter ? plan.ExpectedAfterRoleHash : plan.ExpectedBeforeRoleHash;
        var actualRoleIds = DiscordMemberTransitionHelpers.CanonicalRoleIds(member.RoleIds);
        if (!string.Equals(member.EffectiveRoleHash, expectedHash, StringComparison.OrdinalIgnoreCase) ||
            !actualRoleIds.ToHashSet(StringComparer.Ordinal).SetEquals(expectedRoleIds))
        {
            throw new TransitionDriftException(
                $"Discord member {member.UserId} role membership drifted; expected {(expectedAfter ? "after" : "before")} frozen role set.");
        }
        if (!string.IsNullOrWhiteSpace(plan.ExpectedIdentityHash) &&
            !string.Equals(member.EffectiveIdentityHash, plan.ExpectedIdentityHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new TransitionDriftException($"Discord member {member.UserId} identity fields changed since planning.");
        }
    }

    private static bool IsResumableOwnedPartial(
        DiscordMemberObservation member,
        DiscordRankMembershipPlan plan)
    {
        if (!string.IsNullOrWhiteSpace(plan.ExpectedIdentityHash) &&
            !string.Equals(member.EffectiveIdentityHash, plan.ExpectedIdentityHash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var original = plan.OriginalRoleIds.ToHashSet(StringComparer.Ordinal);
        var expectedAfter = plan.ExpectedAfterRoleIds.ToHashSet(StringComparer.Ordinal);
        var actual = DiscordMemberTransitionHelpers.CanonicalRoleIds(member.RoleIds)
            .ToHashSet(StringComparer.Ordinal);
        var managed = plan.ManagedRankRoleIds.ToHashSet(StringComparer.Ordinal);

        // Only managed rank IDs may be in flight. Every special/unmanaged role
        // must remain exactly as frozen, and no unrelated managed role may be
        // introduced while recovering a partially acknowledged request.
        if (!actual.Except(managed).ToHashSet(StringComparer.Ordinal)
                .SetEquals(original.Except(managed))) return false;
        if (!actual.Except(original.Union(expectedAfter)).Any()) return false;
        if (actual.SetEquals(original) || actual.SetEquals(expectedAfter)) return false;
        return true;
    }

    private async Task RollbackDatabaseOperationsAsync(
        RankTransitionManifest manifest,
        RankTransitionJournal journal,
        string journalPath,
        CancellationToken ct)
    {
        var operations = new List<(RankTransitionOperation Operation, int? RelatedEntityId)>();
        foreach (var operation in manifest.Operations.Where(x => !x.IsExternal).Reverse())
        {
            var checkpoint = FindResult(journal, operation.OperationId);
            var auditRelatedEntityId = await ReadAppliedAuditEntityIdAsync(operation, manifest.ManifestId, ct);
            if (checkpoint?.Status == TransitionOperationStatus.Applied ||
                await HasAppliedAuditAsync(operation, manifest.ManifestId, ct))
            {
                operations.Add((operation, checkpoint?.RelatedEntityId ?? auditRelatedEntityId));
            }
        }
        if (operations.Count == 0) return;

        await using var transaction = await BeginTransactionIfRelationalAsync(ct);
        var results = new List<RankTransitionOperationResult>();
        foreach (var (operation, relatedEntityId) in operations)
        {
            var result = await RollbackDatabaseOperationAsync(operation, manifest, relatedEntityId, ct);
            results.Add(result);
            await RecordAuditAsync(operation, result, manifest.ManifestId, ct);
        }
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        foreach (var result in results) await AddAndPersistResultAsync(journalPath, journal, result, ct);
    }

    private async Task<RankTransitionOperationResult> RollbackDatabaseOperationAsync(
        RankTransitionOperation operation,
        RankTransitionManifest manifest,
        int? relatedEntityId,
        CancellationToken ct)
    {
        if (operation.Kind is TransitionOperationKind.DatabaseCurrentRank or TransitionOperationKind.DatabaseEligibleRank)
        {
            var player = await FindPlayerAsync(operation.TargetKey, ct)
                ?? throw new TransitionDriftException($"Player {operation.TargetKey} no longer exists.");
            await EnsurePlayerObservationStillMatchesAsync(player, operation, manifest, ct);
            var property = operation.Kind == TransitionOperationKind.DatabaseCurrentRank
                ? nameof(Player.CurrentRank)
                : nameof(Player.EligibleRank);
            var actual = property == nameof(Player.CurrentRank) ? player.CurrentRank : player.EligibleRank;
            if (Equivalent(actual, operation.Before)) return AlreadyRolledBack(operation, actual, "Database already has the frozen before value.");
            if (!Equivalent(actual, operation.After)) throw new TransitionDriftException($"Cannot roll back {property} for player {player.Id}; current value '{actual}' is neither frozen before nor after.");
            if (property == nameof(Player.CurrentRank)) player.CurrentRank = operation.Before!;
            else player.EligibleRank = operation.Before!;
            return RolledBack(operation, actual, operation.Before!, "Database rank restored to frozen before value.");
        }

        if (operation.Kind == TransitionOperationKind.FinalizeApprovedPromotion)
        {
            var player = await FindPlayerAsync(operation.TargetKey, ct)
                ?? throw new TransitionDriftException($"Player {operation.TargetKey} no longer exists.");
            await EnsurePlayerObservationStillMatchesAsync(player, operation, manifest, ct);
            if (!relatedEntityId.HasValue)
            {
                throw new TransitionDriftException($"Approved promotion ownership for player {player.Id} is missing; refusing to compensate an unrelated approval.");
            }

            var candidate = await db.PromotionCandidates
                .FirstOrDefaultAsync(x => x.Id == relatedEntityId.Value && x.PlayerId == player.Id, ct);
            if (candidate is null)
            {
                throw new TransitionDriftException($"Exact approved promotion history {relatedEntityId.Value} no longer exists.");
            }
            if (!Equivalent(candidate.NewRank, operation.After))
            {
                throw new TransitionDriftException($"Exact approved promotion history {candidate.Id} no longer has the frozen final rank.");
            }

            var actualRank = player.CurrentRank;
            if (Equivalent(actualRank, operation.Before) && candidate.Status == PromotionStatus.DISMISSED)
            {
                return AlreadyRolledBack(operation, actualRank, "The exact approved cutover promotion is already compensated.") with
                {
                    RelatedEntityId = candidate.Id
                };
            }
            if (!Equivalent(actualRank, operation.After))
            {
                throw new TransitionDriftException($"Player {player.Id} current rank is neither frozen before nor approved after value.");
            }
            if (candidate.Status != PromotionStatus.APPROVED)
            {
                throw new TransitionDriftException($"Exact approved promotion history {candidate.Id} has status '{candidate.Status}', not APPROVED.");
            }

            player.CurrentRank = operation.Before!;
            candidate.Status = PromotionStatus.DISMISSED;
            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = player.Id,
                EventType = "RANK_TRANSITION_APPROVED_PROMOTION_ROLLED_BACK",
                MetadataJson = JsonSerializer.Serialize(new
                {
                    ManifestId = manifest.ManifestId,
                    OperationId = operation.OperationId,
                    CandidateId = candidate.Id,
                    Before = operation.Before,
                    After = operation.After,
                    Details = "The owned cutover APPROVED history was compensated; unrelated approvals were not changed."
                }),
                Status = "DONE",
                CreatedAt = _clock.UtcNow
            });
            return RolledBack(
                operation,
                actualRank,
                operation.Before!,
                "Owned approved cutover promotion compensated and final rank restored.",
                candidate.Id);
        }

        if (operation.Kind == TransitionOperationKind.QueuePromotionCandidate)
        {
            if (!int.TryParse(operation.TargetKey, out var playerId)) throw new TransitionDriftException($"Invalid player key '{operation.TargetKey}'.");
            var player = await db.Players.FirstOrDefaultAsync(x => x.Id == playerId, ct);
            if (player is not null) await EnsurePlayerObservationStillMatchesAsync(player, operation, manifest, ct);
            if (!relatedEntityId.HasValue)
            {
                throw new TransitionDriftException($"Promotion candidate ownership for player {playerId} is missing; refusing to choose a candidate by rank.");
            }
            var candidate = await db.PromotionCandidates
                .FirstOrDefaultAsync(x => x.Id == relatedEntityId.Value && x.PlayerId == playerId, ct);
            if (candidate is null) return AlreadyRolledBack(operation, null, "No pending candidate remains to roll back.");
            if (candidate.Status != PromotionStatus.PENDING)
            {
                return Equivalent(candidate.Status.ToString(), PromotionStatus.DISMISSED.ToString())
                    ? AlreadyRolledBack(operation, candidate.Status.ToString(), "The exact queued candidate is already dismissed.")
                    : throw new TransitionDriftException($"The exact queued candidate {candidate.Id} has status '{candidate.Status}', not pending.");
            }
            if (!Equivalent(FrozenRankMapping.Normalize(candidate.NewRank), operation.After))
            {
                throw new TransitionDriftException($"The exact queued candidate {candidate.Id} no longer has the frozen target rank.");
            }
            candidate.Status = PromotionStatus.DISMISSED;
            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = playerId,
                EventType = "RANK_TRANSITION_PROMOTION_ROLLED_BACK",
                MetadataJson = JsonSerializer.Serialize(new { OperationId = operation.OperationId, CandidateId = candidate.Id }),
                Status = "DONE",
                CreatedAt = _clock.UtcNow
            });
            return RolledBack(operation, PromotionStatus.PENDING.ToString(), PromotionStatus.DISMISSED.ToString(), "Queued candidate dismissed during rollback; row history preserved.", candidate.Id);
        }

        if (operation.Kind == TransitionOperationKind.SupersedePromotionCandidate)
        {
            if (!int.TryParse(operation.TargetKey, out var candidateId)) throw new TransitionDriftException($"Invalid candidate key '{operation.TargetKey}'.");
            var candidate = await db.PromotionCandidates.FirstOrDefaultAsync(x => x.Id == candidateId, ct)
                ?? throw new TransitionDriftException($"Promotion candidate {candidateId} no longer exists.");
            var candidatePlayer = await db.Players.FirstOrDefaultAsync(x => x.Id == candidate.PlayerId, ct);
            if (candidatePlayer is not null) await EnsurePlayerObservationStillMatchesAsync(candidatePlayer, operation, manifest, ct);
            var actual = candidate.Status.ToString();
            if (Equivalent(actual, operation.Before)) return AlreadyRolledBack(operation, actual, "Candidate already has its frozen before status.");
            if (!Equivalent(actual, operation.After)) throw new TransitionDriftException($"Cannot restore candidate {candidateId}; current status is '{actual}'.");
            candidate.Status = PromotionStatus.PENDING;
            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = candidate.PlayerId,
                EventType = "RANK_TRANSITION_CANDIDATE_RESTORED",
                MetadataJson = JsonSerializer.Serialize(new { OperationId = operation.OperationId, CandidateId = candidateId }),
                Status = "DONE",
                CreatedAt = _clock.UtcNow
            });
            return RolledBack(operation, actual, operation.Before, "Candidate status restored to frozen before value.");
        }

        throw new InvalidOperationException($"Unsupported rollback operation {operation.Kind}.");
    }

    private async Task<RankTransitionOperationResult> RollbackWomOperationAsync(
        RankTransitionOperation operation,
        CancellationToken ct)
    {
        var actual = await ReadFreshWomRoleAsync(operation.TargetKey, ct);
        if (Equivalent(actual, operation.Before)) return AlreadyRolledBack(operation, actual, "WOM already has the frozen before role.");
        if (!Equivalent(actual, operation.After)) throw new TransitionDriftException($"Cannot roll back WOM role {operation.TargetKey}; current role '{actual ?? "<missing>"}' is neither frozen before nor after.");
        var update = await wiseOldManClient.UpdateMemberRoleAsync(operation.TargetKey, operation.Before!, ct);
        if (!update.Success) throw new HttpRequestException($"WOM rollback failed: {update.Details}");
        var verified = await ReadFreshWomRoleAsync(operation.TargetKey, ct);
        if (!Equivalent(verified, operation.Before)) throw new TransitionDriftException($"WOM rollback verification observed '{verified ?? "<missing>"}'.");
        return RolledBack(operation, actual, verified, "WOM role restored and verified.");
    }

    private async Task<RankTransitionOperationResult> RollbackDiscordOperationAsync(
        RankTransitionOperation operation,
        RankTransitionManifest manifest,
        CancellationToken ct)
    {
        if (discordRoleGateway is null) throw new InvalidOperationException("Discord gateway is not configured.");
        var observed = await discordRoleGateway.GetGuildObservationAsync(ct)
            ?? throw new HttpRequestException("Discord guild observation is unavailable.");
        if (!observed.MembersFullyObserved)
        {
            throw new TransitionDriftException("Discord membership observation is partial; no role mutation is permitted.");
        }
        var actual = observed.Roles.FirstOrDefault(x => string.Equals(x.RoleId, operation.TargetKey, StringComparison.Ordinal))?.NameBefore;
        if (Equivalent(actual, operation.Before)) return AlreadyRolledBack(operation, actual, "Discord role already has the frozen before name.");
        if (!Equivalent(actual, operation.After)) throw new TransitionDriftException($"Cannot roll back Discord role {operation.TargetKey}; current name '{actual ?? "<missing>"}' is neither frozen before nor after.");
        var roleBeforeRollback = observed.Roles.First(x => string.Equals(x.RoleId, operation.TargetKey, StringComparison.Ordinal));
        VerifyDiscordMetadata(operation, roleBeforeRollback, manifest);
        var update = await discordRoleGateway.RenameRoleAsync(operation.TargetKey, operation.After!, operation.Before!, ct);
        if (!update.Success) throw new HttpRequestException($"Discord rollback failed: {update.Details}");
        var verifiedSnapshot = await discordRoleGateway.GetGuildObservationAsync(ct)
            ?? throw new HttpRequestException("Discord rollback verification observation is unavailable.");
        var verifiedRole = verifiedSnapshot.Roles.FirstOrDefault(x => string.Equals(x.RoleId, operation.TargetKey, StringComparison.Ordinal));
        if (verifiedRole is null || !Equivalent(verifiedRole.NameBefore, operation.Before)) throw new TransitionDriftException($"Discord rollback verification observed '{verifiedRole?.NameBefore ?? "<missing>"}'.");
        VerifyDiscordMetadata(operation, verifiedRole, manifest);
        return RolledBack(operation, actual, verifiedRole.NameBefore, "Discord role restored by the same role ID and verified.");
    }

    private async Task<string?> ReadDiscordRoleNameAsync(string roleId, CancellationToken ct)
    {
        if (discordRoleGateway is null) throw new InvalidOperationException("Discord gateway is not configured.");
        var observed = await discordRoleGateway.GetGuildObservationAsync(ct)
            ?? throw new HttpRequestException("Discord guild observation is unavailable.");
        return observed.Roles.FirstOrDefault(x => string.Equals(x.RoleId, roleId, StringComparison.Ordinal))?.NameBefore;
    }

    private async Task<string?> ReadFreshWomRoleAsync(string username, CancellationToken ct)
    {
        try
        {
            return await wiseOldManClient.GetMemberRoleFreshAsync(username, ct);
        }
        catch (NotSupportedException)
        {
            await wiseOldManClient.InvalidateCacheAsync(ct);
            var roles = await wiseOldManClient.GetMemberRolesAsync(ct);
            return roles.TryGetValue(FrozenRankMapping.Normalize(username), out var role) ? role : null;
        }
    }

    private async Task<Player?> FindPlayerAsync(string targetKey, CancellationToken ct)
    {
        return int.TryParse(targetKey, out var id)
            ? await db.Players.FirstOrDefaultAsync(x => x.Id == id, ct)
            : null;
    }

    private async Task EnsurePlayerObservationStillMatchesAsync(
        Player player,
        RankTransitionOperation operation,
        RankTransitionManifest manifest,
        CancellationToken ct)
    {
        var observation = manifest.Players.FirstOrDefault(x => x.PlayerId == player.Id);
        if (observation is null) return;
        if (!string.Equals(FrozenRankMapping.Normalize(observation.Username), FrozenRankMapping.Normalize(player.Username), StringComparison.OrdinalIgnoreCase))
        {
            throw new TransitionDriftException($"Player identity for ID {player.Id} changed from '{observation.Username}' to '{player.Username}'.");
        }
        if (!string.Equals(observation.Status, player.Status.ToString(), StringComparison.Ordinal))
        {
            throw new TransitionDriftException($"Player status for {player.Username} changed from '{observation.Status}' to '{player.Status}'.");
        }
        if (operation.Kind is (TransitionOperationKind.DatabaseCurrentRank or
            TransitionOperationKind.DatabaseEligibleRank or
            TransitionOperationKind.QueuePromotionCandidate or
            TransitionOperationKind.FinalizeApprovedPromotion) &&
            !string.IsNullOrWhiteSpace(observation.LatestSnapshotHash))
        {
            var latest = await db.PlayerSnapshots
                .AsNoTracking()
                .Where(x => x.PlayerId == player.Id)
                .OrderByDescending(x => x.Timestamp)
                .ThenByDescending(x => x.Id)
                .FirstOrDefaultAsync(ct);
            if (latest is null || !string.Equals(RankTransitionPlanner.SnapshotHash(latest), observation.LatestSnapshotHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new TransitionDriftException($"Latest snapshot for {player.Username} changed since planning; eligibility normalization was not applied.");
            }
        }
    }

    private async Task<IDbContextTransaction?> BeginTransactionIfRelationalAsync(CancellationToken ct)
    {
        if (db.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true) return null;
        return await db.Database.BeginTransactionAsync(ct);
    }

    private static FileStream AcquireExecutionLock(string journalPath)
    {
        var fullPath = Path.GetFullPath(journalPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("Journal path has no directory.");
        Directory.CreateDirectory(directory);
        return new FileStream(
            $"{fullPath}.lock",
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            options: FileOptions.WriteThrough);
    }

    private async Task RecordAuditAsync(
        RankTransitionOperation operation,
        RankTransitionOperationResult result,
        string manifestId,
        CancellationToken ct)
    {
        var auditMetadata = await db.LifecycleEvents
            .AsNoTracking()
            .Where(x => x.EventType == "RANK_TRANSITION_AUDIT")
            .Select(x => x.MetadataJson)
            .ToListAsync(ct);
        var exists = auditMetadata.Any(x =>
            string.Equals(ReadMetadataValue(x, "ManifestId"), manifestId, StringComparison.Ordinal) &&
            string.Equals(ReadMetadataValue(x, "OperationId"), operation.OperationId, StringComparison.Ordinal) &&
            string.Equals(ReadMetadataValue(x, "Status"), result.Status.ToString(), StringComparison.Ordinal));
        if (exists) return;
        var playerId = await ResolveAuditPlayerIdAsync(operation, ct);
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = playerId,
            EventType = "RANK_TRANSITION_AUDIT",
            MetadataJson = JsonSerializer.Serialize(new
            {
                ManifestId = manifestId,
                OperationId = operation.OperationId,
                Kind = operation.Kind.ToString(),
                Target = operation.TargetKey,
                Before = operation.Before,
                After = operation.After,
                ObservedBefore = result.ObservedBefore,
                ObservedAfter = result.ObservedAfter,
                Status = result.Status.ToString(),
                Details = result.Details,
                RelatedEntityId = result.RelatedEntityId,
                At = result.ObservedAtUtc
            }),
            Status = "DONE",
            CreatedAt = result.ObservedAtUtc
        });
    }

    private async Task<bool> HasAppliedAuditAsync(
        RankTransitionOperation operation,
        string manifestId,
        CancellationToken ct)
    {
        var metadata = await db.LifecycleEvents
            .AsNoTracking()
            .Where(x => x.EventType == "RANK_TRANSITION_AUDIT")
            .Select(x => x.MetadataJson)
            .ToListAsync(ct);
        return metadata.Any(x =>
            string.Equals(ReadMetadataValue(x, "ManifestId"), manifestId, StringComparison.Ordinal) &&
            string.Equals(ReadMetadataValue(x, "OperationId"), operation.OperationId, StringComparison.Ordinal) &&
            string.Equals(ReadMetadataValue(x, "Status"), TransitionOperationStatus.Applied.ToString(), StringComparison.Ordinal));
    }

    private async Task<int?> ReadAppliedAuditEntityIdAsync(
        RankTransitionOperation operation,
        string manifestId,
        CancellationToken ct)
    {
        if (operation.Kind is not (TransitionOperationKind.QueuePromotionCandidate or TransitionOperationKind.FinalizeApprovedPromotion)) return null;
        var metadata = await db.LifecycleEvents
            .AsNoTracking()
            .Where(x => x.EventType == "RANK_TRANSITION_AUDIT")
            .Select(x => x.MetadataJson)
            .ToListAsync(ct);
        var audit = metadata.FirstOrDefault(x =>
            string.Equals(ReadMetadataValue(x, "ManifestId"), manifestId, StringComparison.Ordinal) &&
            string.Equals(ReadMetadataValue(x, "OperationId"), operation.OperationId, StringComparison.Ordinal) &&
            string.Equals(ReadMetadataValue(x, "Status"), TransitionOperationStatus.Applied.ToString(), StringComparison.Ordinal));
        return audit is null ? null : ReadMetadataInt32(audit, "RelatedEntityId");
    }

    private async Task<int> ResolveAuditPlayerIdAsync(RankTransitionOperation operation, CancellationToken ct)
    {
        if (operation.Kind is TransitionOperationKind.DatabaseCurrentRank or
            TransitionOperationKind.DatabaseEligibleRank or
            TransitionOperationKind.QueuePromotionCandidate or
            TransitionOperationKind.FinalizeApprovedPromotion)
        {
            return int.TryParse(operation.TargetKey, out var id) ? id : 0;
        }
        if (operation.Kind == TransitionOperationKind.WiseOldManRole)
        {
            var players = await db.Players.AsNoTracking().Select(x => new { x.Id, x.Username }).ToListAsync(ct);
            return players.FirstOrDefault(x => string.Equals(FrozenRankMapping.Normalize(x.Username), FrozenRankMapping.Normalize(operation.TargetKey), StringComparison.OrdinalIgnoreCase))?.Id ?? 0;
        }
        return await db.Players.AsNoTracking().OrderBy(x => x.Id).Select(x => (int?)x.Id).FirstOrDefaultAsync(ct) ?? 0;
    }

    private async Task PersistResultsAsync(
        string journalPath,
        RankTransitionJournal journal,
        IReadOnlyList<RankTransitionOperationResult> results,
        CancellationToken ct)
    {
        foreach (var result in results)
        {
            UpsertResult(journal, result);
        }
        await _journalStore.SaveAsync(journalPath, journal, ct);
    }

    private async Task AddAndPersistResultAsync(
        string journalPath,
        RankTransitionJournal journal,
        RankTransitionOperationResult result,
        CancellationToken ct)
    {
        UpsertResult(journal, result);
        await _journalStore.SaveAsync(journalPath, journal, ct);
    }

    private static void UpsertResult(RankTransitionJournal journal, RankTransitionOperationResult result)
    {
        journal.Results = journal.Results
            .Where(x => !string.Equals(x.OperationId, result.OperationId, StringComparison.Ordinal))
            .Append(result)
            .ToArray();
    }

    private static RankTransitionOperationResult? FindResult(RankTransitionJournal journal, string operationId) =>
        journal.Results.FirstOrDefault(x => string.Equals(x.OperationId, operationId, StringComparison.Ordinal));

    private static bool IsTerminalApplyResult(RankTransitionOperationResult? result) =>
        result?.Status is TransitionOperationStatus.Applied or TransitionOperationStatus.AlreadyApplied or TransitionOperationStatus.Skipped;

    private static bool ShouldPreflight(RankTransitionOperationResult? result) =>
        result?.Status != TransitionOperationStatus.Skipped;

    private RankTransitionOperationResult Applied(
        RankTransitionOperation op,
        string? before,
        string? after,
        string details,
        int? relatedEntityId = null) =>
        Result(op, TransitionOperationStatus.Applied, before, after, details, relatedEntityId: relatedEntityId);

    private RankTransitionOperationResult AlreadyApplied(RankTransitionOperation op, string? actual, string details) =>
        Result(op, TransitionOperationStatus.AlreadyApplied, actual, actual, details);

    private RankTransitionOperationResult Skipped(RankTransitionOperation op, string? actual, string details) =>
        Result(op, TransitionOperationStatus.Skipped, actual, actual, details);

    private RankTransitionOperationResult RolledBack(
        RankTransitionOperation op,
        string? before,
        string? after,
        string details,
        int? relatedEntityId = null) =>
        Result(op, TransitionOperationStatus.RolledBack, before, after, details, relatedEntityId: relatedEntityId);

    private RankTransitionOperationResult AlreadyRolledBack(RankTransitionOperation op, string? actual, string details) =>
        Result(op, TransitionOperationStatus.AlreadyRolledBack, actual, actual, details);

    private RankTransitionOperationResult Failure(
        RankTransitionOperation op,
        TransitionOperationStatus status,
        string details,
        bool externalIntentOwned = false) =>
        Result(op, status, null, null, details, externalIntentOwned);

    private RankTransitionOperationResult Result(
        RankTransitionOperation op,
        TransitionOperationStatus status,
        string? before,
        string? after,
        string details,
        bool externalIntentOwned = false,
        int? relatedEntityId = null) =>
        new(op.OperationId, status, _clock.UtcNow, before, after, details, ExternalIntentOwned: externalIntentOwned, RelatedEntityId: relatedEntityId);

    private static bool Equivalent(string? left, string? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return string.Equals(FrozenRankMapping.Normalize(left), FrozenRankMapping.Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadMetadataValue(string metadata, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(metadata);
            return doc.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? ReadMetadataInt32(string metadata, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(metadata);
            return doc.RootElement.TryGetProperty(key, out var value) && value.TryGetInt32(out var number)
                ? number
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void ValidateEnvironment(RankTransitionManifest manifest)
    {
        if (manifest.Environment.IsBound)
        {
            if (_environment is null) throw new InvalidOperationException("A bound manifest requires a current environment identity before mutation.");
            manifest.Environment.ValidateAgainst(_environment);
        }
    }
}
