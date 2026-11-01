using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.RankTransition;

public sealed record RankTransitionVerificationItem(
    string OperationId,
    TransitionOperationStatus Status,
    string Details,
    string? ObservedValue);

public sealed record RankTransitionVerificationReport(
    string ManifestId,
    IReadOnlyList<RankTransitionVerificationItem> Items,
    bool SafeToProceed);

public sealed class RankTransitionVerifier(
    SwedesClanTracker.Core.TrackerDbContext db,
    SwedesClanTracker.Core.IWiseOldManClient wiseOldManClient,
    IDiscordRoleGateway? discordRoleGateway)
{
    public async Task<RankTransitionVerificationReport> VerifyAsync(
        RankTransitionManifest manifest,
        string? journalPath = null,
        CancellationToken ct = default)
    {
        RankTransitionJournal? journal = null;
        if (!string.IsNullOrWhiteSpace(journalPath) && File.Exists(journalPath))
        {
            journal = await new RankTransitionJournalStore().LoadOrCreateAsync(journalPath, manifest, ct);
        }

        var items = new List<RankTransitionVerificationItem>();
        IReadOnlyDictionary<string, string> womRoles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (manifest.WomObservations.Any(x => x.FreshObservation) ||
            manifest.ManualWomChecks.Count > 0 ||
            manifest.Operations.Any(x => x.Kind == TransitionOperationKind.WiseOldManRole))
        {
            try
            {
                await wiseOldManClient.InvalidateCacheAsync(ct);
                womRoles = await wiseOldManClient.GetMemberRolesAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or WiseOldManRateLimitException or InvalidOperationException)
            {
                foreach (var operation in manifest.Operations.Where(x => x.Kind == TransitionOperationKind.WiseOldManRole))
                {
                    items.Add(new RankTransitionVerificationItem(operation.OperationId, TransitionOperationStatus.Failed, "Fresh WOM observation unavailable.", null));
                }
                foreach (var check in manifest.ManualWomChecks)
                {
                    items.Add(new RankTransitionVerificationItem(check.OperationId, TransitionOperationStatus.Failed, "Fresh WOM observation unavailable for the manual reconciliation gate.", null));
                }
            }
        }

        DiscordGuildObservation? discord = null;
        if ((manifest.DiscordRoles.Count > 0 ||
             manifest.DiscordMembers.Count > 0 ||
             manifest.DiscordMemberRankChanges.Count > 0 ||
             manifest.Operations.Any(x => x.Kind is TransitionOperationKind.DiscordRoleRename or TransitionOperationKind.DiscordMemberRankRole)) &&
            discordRoleGateway is not null)
        {
            try
            {
                discord = await discordRoleGateway.GetGuildObservationAsync(ct);
                if (discord is not null && !discord.MembersFullyObserved)
                {
                    foreach (var operation in manifest.Operations.Where(x => x.Kind is TransitionOperationKind.DiscordRoleRename or TransitionOperationKind.DiscordMemberRankRole))
                    {
                        items.Add(new RankTransitionVerificationItem(
                            operation.OperationId,
                            TransitionOperationStatus.Failed,
                            "Discord membership observation is partial; permissions, hierarchy, and member IDs cannot be verified.",
                            null));
                    }
                }
            }
            catch (HttpRequestException)
            {
                foreach (var operation in manifest.Operations.Where(x => x.Kind == TransitionOperationKind.DiscordRoleRename))
                {
                    items.Add(new RankTransitionVerificationItem(operation.OperationId, TransitionOperationStatus.Failed, "Discord observation unavailable.", null));
                }
            }
        }

        foreach (var operation in manifest.Operations)
        {
            if (items.Any(x => x.OperationId == operation.OperationId)) continue;
            var journalResult = journal is null ? null : journal.Results.FirstOrDefault(x => x.OperationId == operation.OperationId);
            var expected = journalResult?.Status is TransitionOperationStatus.RolledBack or TransitionOperationStatus.AlreadyRolledBack
                ? operation.Before
                : operation.After;
            var discordRole = operation.Kind == TransitionOperationKind.DiscordRoleRename
                ? discord?.Roles.FirstOrDefault(x => x.RoleId == operation.TargetKey)
                : null;
            var discordMember = operation.Kind == TransitionOperationKind.DiscordMemberRankRole
                ? discord?.Members?.FirstOrDefault(x => x.UserId == operation.TargetKey)
                : null;
            var isRolledBack = journalResult?.Status is TransitionOperationStatus.RolledBack or TransitionOperationStatus.AlreadyRolledBack;
            var discordMetadataFailure = discordRole is null
                ? null
                : CheckDiscordMetadata(operation, discordRole, manifest, isRolledBack);
            var discordMemberFailure = discordMember is null || operation.Kind != TransitionOperationKind.DiscordMemberRankRole
                ? null
                : CheckDiscordMemberState(operation, discordMember, manifest, isRolledBack);
            var actual = operation.Kind switch
            {
                TransitionOperationKind.DatabaseCurrentRank => await ReadPlayerPropertyAsync(operation.TargetKey, true, ct),
                TransitionOperationKind.DatabaseEligibleRank => await ReadPlayerPropertyAsync(operation.TargetKey, false, ct),
                TransitionOperationKind.QueuePromotionCandidate => await ReadPromotionTargetAsync(operation, manifest, journal, ct),
                TransitionOperationKind.SupersedePromotionCandidate => await ReadCandidateStatusAsync(operation.TargetKey, ct),
                TransitionOperationKind.FinalizeApprovedPromotion => await ReadApprovedPromotionTargetAsync(operation, manifest, journal, ct),
                TransitionOperationKind.WiseOldManRole => womRoles.TryGetValue(FrozenRankMapping.Normalize(operation.TargetKey), out var role) ? role : null,
                TransitionOperationKind.DiscordRoleRename => discordRole?.NameBefore,
                TransitionOperationKind.DiscordMemberRankRole => discordMember is null ? null : MemberRoleHash(discordMember),
                _ => null
            };
            var status = discordMetadataFailure is null &&
                         discordMemberFailure is null &&
                         Equivalent(actual, expected)
                ? (expected == operation.Before ? TransitionOperationStatus.AlreadyRolledBack : TransitionOperationStatus.AlreadyApplied)
                : TransitionOperationStatus.Drifted;
            items.Add(new RankTransitionVerificationItem(
                operation.OperationId,
                status,
                status == TransitionOperationStatus.Drifted
                    ? discordMetadataFailure ?? discordMemberFailure ?? $"Observed '{actual ?? "<missing>"}', expected '{expected ?? "<missing>"}'."
                    : "Observed expected frozen state.",
                actual));
        }

        await VerifyWomObservationsAsync(manifest, womRoles, items, ct);
        VerifyDiscordObservations(manifest, discord, journal, items);
        VerifyDiscordMemberPlans(manifest, discord, journal, items);
        VerifyManualDiscordRoster(manifest, discord, items);

        return new RankTransitionVerificationReport(
            manifest.ManifestId,
            items,
            items.All(x => x.Status is TransitionOperationStatus.AlreadyApplied or TransitionOperationStatus.AlreadyRolledBack or TransitionOperationStatus.Skipped));
    }

    private async Task VerifyWomObservationsAsync(
        RankTransitionManifest manifest,
        IReadOnlyDictionary<string, string> womRoles,
        List<RankTransitionVerificationItem> items,
        CancellationToken ct)
    {
        if (manifest.WomObservations.Count == 0 && manifest.ManualWomChecks.Count == 0) return;
        var normalizedRoles = womRoles
            .GroupBy(x => FrozenRankMapping.Normalize(x.Key), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count() == 1 ? x.Single().Value : null, StringComparer.OrdinalIgnoreCase);
        var ignoredEvents = await db.LifecycleEvents
            .AsNoTracking()
            .Where(x => x.EventType == "WOM_RANK_MISMATCH_IGNORED" && x.Status == "OPEN")
            .Select(x => new { x.PlayerId, x.MetadataJson })
            .ToListAsync(ct);

        var manualChecksByUsername = manifest.ManualWomChecks
            .ToDictionary(x => FrozenRankMapping.Normalize(x.Username), StringComparer.OrdinalIgnoreCase);
        foreach (var check in manifest.ManualWomChecks)
        {
            if (items.Any(x => string.Equals(x.OperationId, check.OperationId, StringComparison.Ordinal))) continue;
            var actual = normalizedRoles.TryGetValue(FrozenRankMapping.Normalize(check.Username), out var role)
                ? role
                : null;
            var matches = Equivalent(actual, check.RoleAfter);
            items.Add(new RankTransitionVerificationItem(
                check.OperationId,
                matches ? TransitionOperationStatus.AlreadyApplied : TransitionOperationStatus.Drifted,
                matches
                    ? "Fresh WOM role matches the manual in-game reconciliation target; no WOM mutation is performed by this tool."
                    : $"Manual WOM reconciliation required for {check.Username}: observed '{actual ?? "<missing>"}', expected final role '{check.RoleAfter ?? "<missing>"}'.",
                actual));
        }

        foreach (var observation in manifest.WomObservations)
        {
            var operation = manifest.Operations.FirstOrDefault(x =>
                x.Kind == TransitionOperationKind.WiseOldManRole &&
                string.Equals(FrozenRankMapping.Normalize(x.TargetKey), FrozenRankMapping.Normalize(observation.Username), StringComparison.OrdinalIgnoreCase));
            if (operation is null && !manualChecksByUsername.ContainsKey(FrozenRankMapping.Normalize(observation.Username)))
            {
                var id = $"wom-observation:{FrozenRankMapping.Normalize(observation.Username)}";
                if (!observation.FreshObservation)
                {
                    items.Add(new RankTransitionVerificationItem(id, TransitionOperationStatus.Failed, "WOM observation was not captured in the manifest.", null));
                }
                else
                {
                    normalizedRoles.TryGetValue(FrozenRankMapping.Normalize(observation.Username), out var actual);
                    var status = Equivalent(actual, observation.RoleBefore)
                        ? TransitionOperationStatus.AlreadyApplied
                        : TransitionOperationStatus.Drifted;
                    items.Add(new RankTransitionVerificationItem(
                        id,
                        status,
                        status == TransitionOperationStatus.Drifted
                            ? $"Observed '{actual ?? "<missing>"}', expected held role '{observation.RoleBefore ?? "<missing>"}'."
                            : "Held/special WOM role remains at its frozen observed value.",
                        actual));
                }
            }

            if (observation.IgnoredMismatch)
            {
                var playerId = manifest.Players.FirstOrDefault(x =>
                    string.Equals(FrozenRankMapping.Normalize(x.Username), FrozenRankMapping.Normalize(observation.Username), StringComparison.OrdinalIgnoreCase))?.PlayerId;
                var remainsOpen = ignoredEvents.Any(x =>
                    (playerId.HasValue && x.PlayerId == playerId.Value) ||
                    string.Equals(TryReadUsername(x.MetadataJson), observation.Username, StringComparison.OrdinalIgnoreCase));
                items.Add(new RankTransitionVerificationItem(
                    $"wom-ignored-mismatch:{FrozenRankMapping.Normalize(observation.Username)}",
                    remainsOpen ? TransitionOperationStatus.AlreadyApplied : TransitionOperationStatus.Drifted,
                    remainsOpen ? "The ignored WOM mismatch remains OPEN for manual review." : "The ignored WOM mismatch review event is no longer OPEN.",
                    remainsOpen ? "OPEN" : null));
            }
        }
    }

    private static void VerifyDiscordObservations(
        RankTransitionManifest manifest,
        DiscordGuildObservation? discord,
        RankTransitionJournal? journal,
        List<RankTransitionVerificationItem> items)
    {
        if (manifest.DiscordRoles.Count == 0) return;
        if (discord is null)
        {
            foreach (var observation in manifest.DiscordRoles.Where(x =>
                         !manifest.Operations.Any(operation => operation.Kind == TransitionOperationKind.DiscordRoleRename && operation.TargetKey == x.RoleId)))
            {
                items.Add(new RankTransitionVerificationItem(
                    $"discord-observation:{observation.RoleId}",
                    TransitionOperationStatus.Failed,
                    "Discord observation is unavailable for a held/unselected role.",
                    null));
            }
            return;
        }
        if (!discord.MembersFullyObserved)
        {
            foreach (var observation in manifest.DiscordRoles.Where(x =>
                         !manifest.Operations.Any(operation => operation.Kind == TransitionOperationKind.DiscordRoleRename && operation.TargetKey == x.RoleId)))
            {
                items.Add(new RankTransitionVerificationItem(
                    $"discord-observation:{observation.RoleId}",
                    TransitionOperationStatus.Failed,
                    "Discord membership observation is partial for a held/unselected role.",
                    null));
            }
            return;
        }
        foreach (var observation in manifest.DiscordRoles)
        {
            if (manifest.Operations.Any(operation =>
                    operation.Kind == TransitionOperationKind.DiscordRoleRename &&
                    operation.TargetKey == observation.RoleId)) continue;
            var actual = discord.Roles.FirstOrDefault(x => x.RoleId == observation.RoleId);
            var metadataFailure = actual is null ? "Discord role is missing." : CheckDiscordMetadata(
                new RankTransitionOperation(
                    $"discord-observation:{observation.RoleId}",
                    TransitionOperationKind.DiscordRoleRename,
                    observation.RoleId,
                    "Name",
                    observation.NameBefore,
                    observation.NameTarget,
                    true,
                    true,
                    observation.Protected,
                    true,
                    null,
                    null,
                    null,
                    "held-role verification"),
                actual,
                manifest,
                IsManifestRolledBack(journal));
            var status = actual is not null && metadataFailure is null &&
                         string.Equals(actual.NameBefore, observation.NameBefore, StringComparison.Ordinal)
                ? TransitionOperationStatus.AlreadyApplied
                : TransitionOperationStatus.Drifted;
            items.Add(new RankTransitionVerificationItem(
                $"discord-observation:{observation.RoleId}",
                status,
                status == TransitionOperationStatus.Drifted
                    ? metadataFailure ?? $"Observed role name '{actual?.NameBefore ?? "<missing>"}', expected held name '{observation.NameBefore}'."
                    : "Held/unselected Discord role remains unchanged with its frozen metadata.",
                actual?.NameBefore));
        }
    }

    private async Task<string?> ReadPlayerPropertyAsync(string key, bool current, CancellationToken ct)
    {
        if (!int.TryParse(key, out var id)) return null;
        var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return player is null ? null : current ? player.CurrentRank : player.EligibleRank;
    }

    private async Task<string?> ReadPromotionTargetAsync(
        RankTransitionOperation operation,
        RankTransitionManifest manifest,
        RankTransitionJournal? journal,
        CancellationToken ct)
    {
        if (!int.TryParse(operation.TargetKey, out var playerId) || string.IsNullOrWhiteSpace(operation.After)) return null;
        var checkpoint = journal?.Results.FirstOrDefault(x => x.OperationId == operation.OperationId);
        var candidateId = checkpoint?.RelatedEntityId ??
                          await ReadAppliedCandidateIdFromAuditAsync(operation, manifest.ManifestId, ct);
        if (!candidateId.HasValue) return null;
        var candidate = await db.PromotionCandidates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == candidateId.Value && x.PlayerId == playerId, ct);
        if (candidate is null || candidate.Status != SwedesClanTracker.Core.PromotionStatus.PENDING) return null;
        // Candidate.NewRank is written from the canonical evaluator result.
        // Applying the frozen legacy mapping here would reinterpret a newly
        // queued General as Colonel during verify/retry.
        return Equivalent(FrozenRankMapping.Normalize(candidate.NewRank), operation.After)
            ? operation.After
            : null;
    }

    private async Task<string?> ReadApprovedPromotionTargetAsync(
        RankTransitionOperation operation,
        RankTransitionManifest manifest,
        RankTransitionJournal? journal,
        CancellationToken ct)
    {
        if (!int.TryParse(operation.TargetKey, out var playerId)) return null;
        var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(x => x.Id == playerId, ct);
        if (player is null) return null;
        var checkpoint = journal?.Results.FirstOrDefault(x => x.OperationId == operation.OperationId);
        var candidateId = checkpoint?.RelatedEntityId ??
                          await ReadAppliedCandidateIdFromAuditAsync(operation, manifest.ManifestId, ct);
        if (!candidateId.HasValue) return null;
        var candidate = await db.PromotionCandidates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == candidateId.Value && x.PlayerId == playerId, ct);
        if (candidate is null) return null;
        if (checkpoint?.Status is TransitionOperationStatus.RolledBack or TransitionOperationStatus.AlreadyRolledBack)
        {
            return candidate.Status == PromotionStatus.DISMISSED &&
                   Equivalent(player.CurrentRank, operation.Before)
                ? operation.Before
                : null;
        }
        return candidate.Status == PromotionStatus.APPROVED &&
               Equivalent(player.CurrentRank, operation.After) &&
               Equivalent(candidate.NewRank, operation.After)
            ? operation.After
            : null;
    }

    private static string MemberRoleHash(DiscordMemberObservation member) =>
        member.EffectiveRoleHash;

    private static string? CheckDiscordMemberState(
        RankTransitionOperation operation,
        DiscordMemberObservation actual,
        RankTransitionManifest manifest,
        bool rolledBack)
    {
        var plan = manifest.DiscordMemberRankChanges.FirstOrDefault(x =>
            string.Equals(x.DiscordUserId, operation.TargetKey, StringComparison.Ordinal));
        if (plan is null) return "Frozen Discord member rank plan is missing.";
        var expectedIds = rolledBack ? plan.OriginalRoleIds : plan.ExpectedAfterRoleIds;
        var expectedHash = rolledBack ? plan.ExpectedBeforeRoleHash : plan.ExpectedAfterRoleHash;
        if (!string.Equals(actual.EffectiveRoleHash, expectedHash, StringComparison.OrdinalIgnoreCase) ||
            !DiscordMemberTransitionHelpers.CanonicalRoleIds(actual.RoleIds)
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(expectedIds))
        {
            return "Discord member rank-role state drifted; unrelated/special roles must remain unchanged.";
        }
        return null;
    }

    private static void VerifyDiscordMemberPlans(
        RankTransitionManifest manifest,
        DiscordGuildObservation? discord,
        RankTransitionJournal? journal,
        List<RankTransitionVerificationItem> items)
    {
        if (manifest.DiscordMemberRankChanges.Count == 0) return;
        foreach (var plan in manifest.DiscordMemberRankChanges)
        {
            var operation = manifest.Operations.FirstOrDefault(x =>
                x.Kind == TransitionOperationKind.DiscordMemberRankRole &&
                string.Equals(x.TargetKey, plan.DiscordUserId, StringComparison.Ordinal));
            var member = discord?.Members?.FirstOrDefault(x =>
                string.Equals(x.UserId, plan.DiscordUserId, StringComparison.Ordinal));
            var id = $"discord-member-observation:{plan.DiscordUserId}";
            if (member is null)
            {
                items.Add(new RankTransitionVerificationItem(
                    id,
                    TransitionOperationStatus.Failed,
                    "Discord member observation is unavailable for the frozen approved cutover plan.",
                    null));
                continue;
            }
            var rolledBack = operation is not null &&
                             journal?.Results.FirstOrDefault(x => x.OperationId == operation.OperationId)?.Status
                                 is TransitionOperationStatus.RolledBack or TransitionOperationStatus.AlreadyRolledBack;
            var manual = manifest.DiscordMemberRanksManual;
            var expectedIds = rolledBack || manual ? plan.OriginalRoleIds : plan.ExpectedAfterRoleIds;
            var expectedHash = rolledBack || manual ? plan.ExpectedBeforeRoleHash : plan.ExpectedAfterRoleHash;
            var failure = !string.Equals(member.EffectiveRoleHash, expectedHash, StringComparison.OrdinalIgnoreCase) ||
                          !DiscordMemberTransitionHelpers.CanonicalRoleIds(member.RoleIds)
                              .ToHashSet(StringComparer.Ordinal)
                              .SetEquals(expectedIds)
                ? "Discord member rank-role state drifted; unrelated/special roles must remain unchanged."
                : !manual && !string.IsNullOrWhiteSpace(plan.ExpectedIdentityHash) &&
                  !string.Equals(member.EffectiveIdentityHash, plan.ExpectedIdentityHash, StringComparison.OrdinalIgnoreCase)
                    ? "Discord member identity fields changed since planning."
                    : null;
            var status = failure is null && string.Equals(member.EffectiveRoleHash, expectedHash, StringComparison.OrdinalIgnoreCase)
                ? (rolledBack ? TransitionOperationStatus.AlreadyRolledBack : TransitionOperationStatus.AlreadyApplied)
                : TransitionOperationStatus.Drifted;
            items.Add(new RankTransitionVerificationItem(
                id,
                manual && status == TransitionOperationStatus.AlreadyApplied
                    ? TransitionOperationStatus.Skipped
                    : status,
                status == TransitionOperationStatus.Drifted
                    ? failure ?? "Discord member rank-role state does not match the frozen cutover target."
                    : manual
                        ? "Manual mode confirms the frozen member roster is unchanged; the suggested rank move remains an operator action."
                        : "Discord member rank-role state matches the frozen cutover target and preserves unrelated roles.",
                member.EffectiveRoleHash));
        }
    }

    private static void VerifyManualDiscordRoster(
        RankTransitionManifest manifest,
        DiscordGuildObservation? discord,
        List<RankTransitionVerificationItem> items)
    {
        if (!manifest.DiscordMemberRanksManual) return;
        var operationId = "discord-manual-roster";
        if (manifest.DiscordMembers.Count == 0)
        {
            items.Add(new RankTransitionVerificationItem(
                operationId,
                TransitionOperationStatus.Failed,
                "The manual manifest has no frozen Discord member roster.",
                null));
            return;
        }
        if (discord?.Members is null)
        {
            items.Add(new RankTransitionVerificationItem(
                operationId,
                TransitionOperationStatus.Failed,
                "The frozen manual Discord member roster is unavailable.",
                null));
            return;
        }

        var expected = manifest.DiscordMembers
            .GroupBy(x => x.UserId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var actual = discord.Members
            .GroupBy(x => x.UserId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var matches = expected.Count == actual.Count && expected.Keys.All(actual.ContainsKey) &&
                      expected.All(pair =>
                          string.Equals(pair.Value.EffectiveRoleHash, actual[pair.Key].EffectiveRoleHash, StringComparison.OrdinalIgnoreCase) &&
                          DiscordMemberTransitionHelpers.CanonicalRoleIds(pair.Value.RoleIds)
                              .ToHashSet(StringComparer.Ordinal)
                              .SetEquals(actual[pair.Key].RoleIds));
        items.Add(new RankTransitionVerificationItem(
            operationId,
            matches ? TransitionOperationStatus.AlreadyApplied : TransitionOperationStatus.Drifted,
            matches
                ? "Manual Discord member roster matches the frozen before observation; suggested rank changes remain human actions."
                : "Discord member roster identity or roles changed from the frozen manual observation.",
            DiscordMemberTransitionHelpers.ComputeMemberHash(actual.Values)));
    }

    private async Task<int?> ReadAppliedCandidateIdFromAuditAsync(
        RankTransitionOperation operation,
        string manifestId,
        CancellationToken ct)
    {
        var metadata = await db.LifecycleEvents
            .AsNoTracking()
            .Where(x => x.EventType == "RANK_TRANSITION_AUDIT")
            .Select(x => x.MetadataJson)
            .ToListAsync(ct);
        foreach (var json in metadata)
        {
            if (!string.Equals(ReadMetadataValue(json, "ManifestId"), manifestId, StringComparison.Ordinal) ||
                !string.Equals(ReadMetadataValue(json, "OperationId"), operation.OperationId, StringComparison.Ordinal) ||
                !string.Equals(ReadMetadataValue(json, "Status"), TransitionOperationStatus.Applied.ToString(), StringComparison.Ordinal))
            {
                continue;
            }
            if (TryReadMetadataInt32(json, "RelatedEntityId") is { } candidateId) return candidateId;
        }
        return null;
    }

    private async Task<string?> ReadCandidateStatusAsync(string key, CancellationToken ct)
    {
        if (!int.TryParse(key, out var id)) return null;
        var candidate = await db.PromotionCandidates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return candidate?.Status.ToString();
    }

    private static string? CheckDiscordMetadata(
        RankTransitionOperation operation,
        DiscordRoleObservation actual,
        RankTransitionManifest manifest,
        bool rolledBack = false)
    {
        var frozen = manifest.DiscordRoles.FirstOrDefault(x =>
            string.Equals(x.RoleId, operation.TargetKey, StringComparison.Ordinal));
        if (frozen is null) return "Discord role observation is missing from the reviewed manifest.";
        if (!string.Equals(frozen.Permissions, actual.Permissions, StringComparison.Ordinal) ||
            frozen.Position != actual.Position ||
            frozen.Protected != actual.Protected)
        {
            return "Discord role permissions or hierarchy position changed since planning.";
        }
        var expectedMemberIds = ExpectedRoleMemberIds(frozen, manifest, rolledBack);
        var expectedMembershipHash = expectedMemberIds is null
            ? frozen.MembershipHash
            : DiscordMemberTransitionHelpers.ComputeRoleHash(expectedMemberIds);
        if (expectedMemberIds is not null && expectedMemberIds.Count != actual.MembershipCount)
        {
            return "Discord role member IDs changed since planning.";
        }
        if (expectedMemberIds is null && frozen.MembershipCount != actual.MembershipCount)
        {
            return "Discord role membership count changed since planning.";
        }
        if (!string.Equals(expectedMembershipHash, actual.MembershipHash, StringComparison.OrdinalIgnoreCase))
        {
            return "Discord role membership count/hash changed since planning.";
        }
        if (expectedMemberIds is not null &&
            (actual.MemberIds is null || !expectedMemberIds.SequenceEqual(actual.MemberIds, StringComparer.Ordinal)))
        {
            return "Discord role member IDs changed since planning.";
        }
        if (expectedMemberIds is null &&
            ((frozen.MemberIds is null) != (actual.MemberIds is null) ||
             frozen.MemberIds is not null && !frozen.MemberIds.SequenceEqual(actual.MemberIds!, StringComparer.Ordinal)))
        {
            return "Discord role member IDs changed since planning.";
        }
        return null;
    }

    private static IReadOnlyList<string>? ExpectedRoleMemberIds(
        DiscordRoleObservation frozen,
        RankTransitionManifest manifest,
        bool rolledBack)
    {
        if (frozen.MemberIds is null) return null;
        if (manifest.DiscordMemberRanksManual)
        {
            return frozen.MemberIds.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }
        var members = frozen.MemberIds.ToHashSet(StringComparer.Ordinal);
        foreach (var plan in manifest.DiscordMemberRankChanges)
        {
            var before = plan.OriginalRoleIds.Contains(frozen.RoleId, StringComparer.Ordinal);
            var after = plan.ExpectedAfterRoleIds.Contains(frozen.RoleId, StringComparer.Ordinal);
            var userId = plan.DiscordUserId;
            if (rolledBack)
            {
                if (after && !before) members.Remove(userId);
                if (before && !after) members.Add(userId);
            }
            else
            {
                if (before && !after) members.Remove(userId);
                if (after && !before) members.Add(userId);
            }
        }
        return members.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    private static bool IsManifestRolledBack(RankTransitionJournal? journal) =>
        journal?.Results.Any(x => x.Status is TransitionOperationStatus.RolledBack or TransitionOperationStatus.AlreadyRolledBack) == true;

    private static string? TryReadUsername(string metadataJson)
    {
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            foreach (var key in new[] { "Username", "Player", "PreviousUsername" })
            {
                if (document.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var username = value.GetString();
                    if (!string.IsNullOrWhiteSpace(username)) return username;
                }
            }
        }
        catch (JsonException)
        {
            // Ignore malformed unrelated lifecycle metadata; the missing
            // ignored event will still fail closed below.
        }
        return null;
    }

    private static string? ReadMetadataValue(string metadataJson, string key)
    {
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            return document.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? TryReadMetadataInt32(string metadataJson, string key)
    {
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            return document.RootElement.TryGetProperty(key, out var value) && value.TryGetInt32(out var number)
                ? number
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Equivalent(string? left, string? right) =>
        left is null || right is null
            ? left is null && right is null
            : string.Equals(FrozenRankMapping.Normalize(left), FrozenRankMapping.Normalize(right), StringComparison.OrdinalIgnoreCase);
}
