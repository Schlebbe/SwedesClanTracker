using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.RankTransition;

public sealed record RankTransitionPlannerOptions(
    bool IncludeDiscord = true,
    bool IncludeWom = true,
    IReadOnlyDictionary<string, string>? DiscordRankRoleIds = null,
    bool ApproveQualifyingPromotions = false,
    IReadOnlyDictionary<int, string>? DiscordMemberIdentityOverrides = null,
    bool DiscordMemberRanksManual = false,
    bool WomUpdatesManual = false);

sealed record ConfiguredDiscordRole(
    DiscordRoleObservation Role,
    string SourceName,
    string TargetName)
{
    public bool AlreadyAtTarget => string.Equals(
        FrozenRankMapping.Normalize(Role.NameBefore),
        FrozenRankMapping.Normalize(TargetName),
        StringComparison.OrdinalIgnoreCase);
}

public interface ITransitionClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemTransitionClock : ITransitionClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class RankTransitionPlanner(
    TrackerDbContext db,
    IWiseOldManClient wiseOldManClient,
    IDiscordRoleGateway? discordRoleGateway = null,
    ITransitionClock? clock = null,
    TransitionEnvironmentIdentity? environment = null)
{
    private readonly ITransitionClock _clock = clock ?? new SystemTransitionClock();
    private readonly TransitionEnvironmentIdentity _environment = environment ?? new();

    public async Task<RankTransitionManifest> PlanAsync(
        RankTransitionPlannerOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new RankTransitionPlannerOptions();
        var now = _clock.UtcNow;
        var warnings = new List<TransitionWarning>();
        var operations = new List<RankTransitionOperation>();
        var promotionDecisions = new List<PromotionDecision>();

        // AsNoTracking is intentional: plan/verify are never allowed to mutate
        // the context, and the CLI wraps this query in a read-only transaction.
        var players = await db.Players
            .AsNoTracking()
            .Include(x => x.Snapshots)
            .OrderBy(x => x.Id)
            .ToListAsync(ct);
        var lifecycleEvents = await db.LifecycleEvents.AsNoTracking().ToListAsync(ct);
        var existingMarker = lifecycleEvents.FirstOrDefault(x =>
            x.EventType == "RANK_TRANSITION_POLICY_MARKER" &&
            x.MetadataJson.Contains(FrozenRankMapping.Version));
        if (existingMarker is not null)
        {
            throw new InvalidOperationException(
                "This frozen rank transition is already marked in the database. Resume or verify the original manifest instead of creating a second plan.");
        }
        var pendingCandidates = await db.PromotionCandidates
            .AsNoTracking()
            .Where(x => x.Status == PromotionStatus.PENDING)
            .ToListAsync(ct);

        IReadOnlyDictionary<string, string> womRoles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var womFresh = false;
        if (options.IncludeWom)
        {
            if (_environment.WiseOldManGroupId <= 0)
            {
                warnings.Add(new TransitionWarning(
                    "WOM_GROUP_MISSING",
                    "WiseOldMan",
                    "WiseOldMan:GroupId is missing; a production transition plan cannot apply WOM or promotion actions.",
                    BlocksApply: true));
            }
            try
            {
                // Existing Core client exposes an explicit cache invalidation
                // boundary. GetMemberRolesAsync then performs a fresh GET.
                await wiseOldManClient.InvalidateCacheAsync(ct);
                womRoles = await wiseOldManClient.GetMemberRolesAsync(ct);
                womFresh = true;
            }
            catch (Exception ex) when (ex is HttpRequestException or WiseOldManRateLimitException or InvalidOperationException)
            {
                warnings.Add(new TransitionWarning(
                    "WOM_UNAVAILABLE",
                    "WiseOldMan",
                    "Fresh Wise Old Man roles could not be read; no WOM mutations or promotion queues will be planned. " +
                    SafeExceptionName(ex),
                    BlocksApply: true));
            }
        }

        // Normalize identities once, before any system-specific work. A
        // case-only duplicate is not safe to correlate to a WOM member or a
        // promotion candidate, so retain it as an explicit review condition
        // instead of guessing which database row owns the role.
        var playerNameGroups = players
            .GroupBy(x => FrozenRankMapping.Normalize(x.Username), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ambiguousPlayerNames = playerNameGroups
            .Where(x => string.IsNullOrWhiteSpace(x.Key) || x.Count() != 1)
            .Select(x => x.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in playerNameGroups.Where(x => ambiguousPlayerNames.Contains(x.Key)))
        {
            warnings.Add(new TransitionWarning(
                "AMBIGUOUS_PLAYER_IDENTITY",
                group.Key,
                group.Key.Length == 0
                    ? "One or more database players have no username; external correlation and promotion work are held."
                    : $"Database username '{group.Key}' occurs {group.Count()} times; external correlation and promotion work are held.",
                BlocksApply: true));
        }
        var playersByUsername = playerNameGroups
            .Where(x => !ambiguousPlayerNames.Contains(x.Key))
            .ToDictionary(x => x.Key, x => x.Single(), StringComparer.OrdinalIgnoreCase);

        var womRoleGroups = womRoles
            .GroupBy(x => FrozenRankMapping.Normalize(x.Key), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ambiguousWomNames = womRoleGroups
            .Where(x => string.IsNullOrWhiteSpace(x.Key) || x.Count() != 1)
            .Select(x => x.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in womRoleGroups.Where(x => ambiguousWomNames.Contains(x.Key)))
        {
            warnings.Add(new TransitionWarning(
                "AMBIGUOUS_WOM_IDENTITY",
                group.Key,
                group.Key.Length == 0
                    ? "The fresh WOM response contained a member without a username; no role action is planned for it."
                    : $"The fresh WOM response contains {group.Count()} entries for normalized username '{group.Key}'; role action is held.",
                BlocksApply: true));
        }
        var womRolesByUsername = womRoleGroups
            .Where(x => !ambiguousWomNames.Contains(x.Key))
            .ToDictionary(x => x.Key, x => x.Single().Value, StringComparer.OrdinalIgnoreCase);

        DiscordGuildObservation? discord = null;
        if (options.IncludeDiscord && discordRoleGateway is not null)
        {
            try
            {
                discord = await discordRoleGateway.GetGuildObservationAsync(ct);
                if (discord is null)
                {
                    warnings.Add(new TransitionWarning(
                        "DISCORD_UNAVAILABLE",
                        "Discord",
                        "Discord credentials or guild ID are not configured; Discord roles were not planned.",
                        BlocksApply: true));
                }
                else if (!discord.MembersFullyObserved)
                {
                    warnings.Add(new TransitionWarning(
                        "DISCORD_MEMBERS_PARTIAL",
                        discord.GuildId,
                        discord.Notes ?? "Discord membership observation was partial.",
                        BlocksApply: true));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                warnings.Add(new TransitionWarning(
                    "DISCORD_UNAVAILABLE",
                    "Discord",
                    "Discord role observation failed; no Discord mutations will be planned. " + SafeExceptionName(ex),
                    BlocksApply: true));
            }
        }
        else if (options.IncludeDiscord)
        {
            warnings.Add(new TransitionWarning(
                "DISCORD_GATEWAY_MISSING",
                "Discord",
                "No Discord observation gateway is configured; a production transition plan cannot apply Discord changes.",
                BlocksApply: true));
        }

        if (options.ApproveQualifyingPromotions)
        {
            if (!options.IncludeDiscord || discordRoleGateway is null)
            {
                warnings.Add(new TransitionWarning(
                    "CUTOVER_DISCORD_REQUIRED",
                    "Discord",
                    "Approved cutover promotions require a complete Discord guild role/member observation for frozen role IDs and metadata; member rank changes are manual review unless automatic mode is explicitly selected.",
                    BlocksApply: true));
            }
            else if (discord is not null && (!discord.MembersFullyObserved || discord.Members is null))
            {
                warnings.Add(new TransitionWarning(
                    "CUTOVER_DISCORD_MEMBERS_REQUIRED",
                    discord.GuildId,
                    "Approved cutover promotions require a complete Discord member snapshot so frozen role membership and the manual review roster can be verified; partial observations block apply.",
                    BlocksApply: true));
            }
        }

        var ignoredMismatchPlayerIds = lifecycleEvents
            .Where(x => x.Status == "OPEN" && x.EventType == "WOM_RANK_MISMATCH_IGNORED")
            .Select(x => x.PlayerId)
            .ToHashSet();
        var ignoredMismatchUsernames = lifecycleEvents
            .Where(x => x.Status == "OPEN" && x.EventType == "WOM_RANK_MISMATCH_IGNORED")
            .Select(x => TryReadUsername(x.MetadataJson))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(FrozenRankMapping.Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reviewSuppressedPlayerIds = lifecycleEvents
            .Where(x => x.Status == "OPEN" &&
                        (x.EventType is "PLAYER_SNAPSHOT_CONTAMINATION_REQUIRED" or "MERGE_SUGGESTED" or "MERGE_ACTION_REQUIRED" or "TEMPLE_NAME_CHANGE_REQUIRED" or "PLAYER_IDENTITY_REVIEW"))
            .Select(x => x.PlayerId)
            .ToHashSet();

        var observations = new List<RankTransitionPlayerObservation>(players.Count);
        var womObservations = new List<RankTransitionWomObservation>();
        var manualWomChecks = new List<RankTransitionManualWomCheck>();
        var discordMemberRankChanges = new List<DiscordRankMembershipPlan>();

        foreach (var player in players)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = player.Snapshots
                .OrderByDescending(x => x.Timestamp)
                .ThenByDescending(x => x.Id)
                .FirstOrDefault();
            var normalizedUsername = FrozenRankMapping.Normalize(player.Username);
            var identityAmbiguous = ambiguousPlayerNames.Contains(normalizedUsername);
            var currentBefore = FrozenRankMapping.Normalize(player.CurrentRank);
            var currentTarget = FrozenRankMapping.Map(player.CurrentRank);
            var eligibleBefore = FrozenRankMapping.Normalize(player.EligibleRank);
            var evaluated = snapshot is null ? null : RankEvaluator.Evaluate(snapshot);
            // Evaluator output is already canonical. The frozen mapping only
            // applies to values read from the legacy database/WOM systems.
            var evaluatedTarget = evaluated is null ? null : FrozenRankMapping.Normalize(evaluated.Rank);

            // A rank transition must never demote the standing rank. The
            // evaluator may lower eligibility after a data/policy change, but
            // CurrentRank remains the mapped standing value.
            // Without a snapshot there is no current evaluator result. Hold
            // the eligibility value exactly as observed; mapping a legacy
            // eligible rank here would look like a newly evaluated promotion
            // and can silently reinterpret an unavailable record.
            var eligibleTarget = evaluatedTarget ??
                (snapshot is null ? eligibleBefore : FrozenRankMapping.Map(player.EligibleRank));
            if (string.IsNullOrWhiteSpace(eligibleTarget)) eligibleTarget = eligibleBefore;

            var isActive = player.Status == PlayerStatus.ACTIVE;
            // LastSynced is the freshness signal. Snapshot timestamps/hash
            // identify the frozen data and detect drift, but an unchanged
            // snapshot may legitimately be older than the latest successful
            // sync.
            var freshnessTime = player.LastSynced;
            var staleSnapshot = freshnessTime is null || now - freshnessTime.Value > TimeSpan.FromDays(1);
            var currentKnown = IsKnownDatabaseRank(currentBefore);
            var eligibleKnown = IsKnownDatabaseRank(eligibleBefore);
            var protectedDb = string.IsNullOrWhiteSpace(currentBefore) || string.IsNullOrWhiteSpace(eligibleBefore) || !currentKnown || !eligibleKnown;
            var protectionReason = string.IsNullOrWhiteSpace(currentBefore) || string.IsNullOrWhiteSpace(eligibleBefore)
                ? "Missing database rank value."
                : !currentKnown || !eligibleKnown
                    ? "Unknown database rank; manual review required."
                    : null;
            var playerObservation = new RankTransitionPlayerObservation(
                player.Id,
                player.Username,
                player.Status.ToString(),
                isActive,
                currentBefore,
                currentTarget,
                eligibleBefore,
                eligibleTarget,
                snapshot?.Id,
                snapshot is null ? null : SnapshotHash(snapshot),
                snapshot?.Timestamp,
                player.LastSynced,
                evaluatedTarget,
                evaluated?.Explanation,
                protectedDb,
                protectionReason);
            observations.Add(playerObservation);
            if (protectedDb)
            {
                warnings.Add(new TransitionWarning(
                    "UNKNOWN_DATABASE_RANK",
                    player.Username,
                    protectionReason ?? "Database rank requires manual review.",
                    BlocksApply: true));
            }

            if (!protectedDb && !string.Equals(eligibleBefore, eligibleTarget, StringComparison.Ordinal))
            {
                operations.Add(CreateOperation(
                    TransitionOperationKind.DatabaseEligibleRank,
                    $"player:{player.Id}:eligible-rank",
                    player.Id.ToString(),
                    nameof(Player.EligibleRank),
                    eligibleBefore,
                    eligibleTarget,
                    isExternal: false,
                    isActive,
                    protectedDb,
                    requiresReview: false,
                    snapshot,
                    player.LastSynced,
                    "Recalculate eligibility with the current Core RankEvaluator and frozen mapping."));
            }
            if (staleSnapshot)
            {
                warnings.Add(new TransitionWarning(
                    "STALE_PLAYER_SNAPSHOT",
                    player.Username,
                    freshnessTime is null
                        ? "No successful sync timestamp is available; no promotion is queued from the snapshot."
                        : $"Last successful sync is older than one day ({freshnessTime.Value:O}); no promotion is queued from it.",
                    BlocksApply: false));
            }
            if (snapshot is null)
            {
                warnings.Add(new TransitionWarning(
                    "NO_PLAYER_SNAPSHOT",
                    player.Username,
                    "No player snapshot is available; eligibility is unavailable and no promotion is queued.",
                    BlocksApply: false));
            }
            var reviewSuppressed = reviewSuppressedPlayerIds.Contains(player.Id);
            if (reviewSuppressed)
            {
                warnings.Add(new TransitionWarning(
                    "PLAYER_REVIEW_SUPPRESSED",
                    player.Username,
                    "An open identity/contamination/rename review suppresses new promotion work.",
                    BlocksApply: false));
            }

            string? womRole = null;
            var womIdentityAmbiguous = ambiguousWomNames.Contains(normalizedUsername);
            if (womFresh && !womIdentityAmbiguous)
            {
                womRolesByUsername.TryGetValue(normalizedUsername, out womRole);
            }
            var womSpecial = FrozenRankMapping.IsSpecialOrUnknownWomRole(womRole);
            var ignoredMismatch = ignoredMismatchPlayerIds.Contains(player.Id) ||
                                  ignoredMismatchUsernames.Contains(FrozenRankMapping.Normalize(player.Username));
            var womTarget = womSpecial || string.IsNullOrWhiteSpace(womRole)
                ? null
                : FrozenRankMapping.MapWomRole(womRole);
            var womStandingAboveEligibility = womTarget is not null &&
                                              evaluatedTarget is not null &&
                                              IsKnownCanonicalRank(womTarget) &&
                                              IsKnownCanonicalRank(evaluatedTarget) &&
                                              RankRules.RankOrder(womTarget) > RankRules.RankOrder(evaluatedTarget);
            if (womStandingAboveEligibility && !options.ApproveQualifyingPromotions)
            {
                warnings.Add(new TransitionWarning(
                    "WOM_STANDING_ABOVE_ELIGIBILITY",
                    player.Username,
                    $"Mapped WOM standing '{womTarget}' is above evaluated eligibility '{evaluatedTarget}'; promotion and in-game alignment are held for review while the independent WOM rename may proceed.",
                    BlocksApply: false));
            }

            if (womFresh && string.IsNullOrWhiteSpace(womRole) && !identityAmbiguous && !womIdentityAmbiguous)
            {
                warnings.Add(new TransitionWarning(
                    "WOM_IDENTITY_MISSING",
                    player.Username,
                    "Player was not found in the fresh WOM response; no role action or promotion is planned.",
                    BlocksApply: isActive));
            }

            if (!womFresh || !isActive || identityAmbiguous || womIdentityAmbiguous || womSpecial || string.IsNullOrWhiteSpace(womRole))
            {
                if (womSpecial && isActive && !string.IsNullOrWhiteSpace(womRole))
                {
                    warnings.Add(new TransitionWarning(
                        "WOM_SPECIAL_PROTECTED",
                        player.Username,
                        $"WOM role '{womRole}' is protected and will not be renamed or aligned.",
                        BlocksApply: false));
                }
            }
            var safePromotionBase = isActive &&
                                    !identityAmbiguous &&
                                    !womIdentityAmbiguous &&
                                    !protectedDb &&
                                    womFresh &&
                                    !womSpecial &&
                                    !ignoredMismatch &&
                                    !staleSnapshot &&
                                    !reviewSuppressed &&
                                    !womStandingAboveEligibility &&
                                    !string.IsNullOrWhiteSpace(womRole) &&
                                    evaluatedTarget is not null &&
                                    IsKnownCanonicalRank(evaluatedTarget) &&
                                    IsKnownCanonicalRank(womTarget);
            var finalTarget = currentTarget;
            if (safePromotionBase)
            {
                finalTarget = HighestCanonicalRank(currentTarget, evaluatedTarget, womTarget);
            }

            DiscordRankMembershipPlan? memberPlan = null;
            var approvedForCutover = options.ApproveQualifyingPromotions &&
                                     safePromotionBase &&
                                     IsStrictUpgrade(currentTarget, finalTarget);
            if (approvedForCutover)
            {
                memberPlan = BuildApprovedDiscordMemberPlan(
                    player,
                    finalTarget,
                    discord,
                    options.DiscordRankRoleIds,
                    options.DiscordMemberIdentityOverrides,
                    options.DiscordMemberRanksManual,
                    warnings);
                if (options.DiscordMemberRanksManual)
                {
                    // Manual mode freezes the complete roster and suggested
                    // member target for the operator report only. It never
                    // emits a DiscordMemberRankRole mutation.
                    if (memberPlan is not null) discordMemberRankChanges.Add(memberPlan);
                    if (memberPlan is null || memberPlan.RequiresReview)
                    {
                        warnings.Add(new TransitionWarning(
                            "CUTOVER_MEMBER_MANUAL_REVIEW",
                            player.Username,
                            memberPlan?.ReviewReason ?? "No verified Discord member identity was available; print and reconcile this account manually after cutover.",
                            BlocksApply: false));
                    }
                }
                else if (memberPlan is not null && !memberPlan.RequiresReview)
                {
                    discordMemberRankChanges.Add(memberPlan);
                    if (!memberPlan.IsNoOp)
                    {
                        operations.Add(CreateOperation(
                            TransitionOperationKind.DiscordMemberRankRole,
                            $"discord-member:{memberPlan.DiscordUserId}:{memberPlan.TargetRoleId}",
                            memberPlan.DiscordUserId,
                            "RankMembership",
                            memberPlan.ExpectedBeforeRoleHash,
                            memberPlan.ExpectedAfterRoleHash,
                            isExternal: true,
                            isActive: true,
                            Protected: false,
                            requiresReview: false,
                            snapshot,
                            player.LastSynced,
                            "Replace only the member's managed rank roles with the final approved cutover role; preserve every special/unmanaged role."));
                    }
                }
                else if (memberPlan is null)
                {
                    warnings.Add(new TransitionWarning(
                        "CUTOVER_MEMBER_PLAN_MISSING",
                        player.Username,
                        "The qualifying promotion target remains frozen in the manifest, but apply is blocked because a verified Discord member-role plan could not be created.",
                        BlocksApply: true));
                }
                else
                {
                    warnings.Add(new TransitionWarning(
                        "CUTOVER_MEMBER_REVIEW_REQUIRED",
                        player.Username,
                        memberPlan.ReviewReason ?? "Discord member rank-role state requires explicit review; apply is blocked.",
                        BlocksApply: true));
                }
            }

            var womTargetAfterCutover = approvedForCutover ? finalTarget : womTarget;
            var womProtected = identityAmbiguous ||
                               womIdentityAmbiguous ||
                               womSpecial ||
                               (womStandingAboveEligibility && !approvedForCutover);
            var womNotes = identityAmbiguous || womIdentityAmbiguous
                ? "Ambiguous identity is protected; no external action or promotion is planned."
                : string.IsNullOrWhiteSpace(womRole) && womFresh
                    ? "Player was not present in the fresh WOM response; identity requires review."
                : womSpecial
                    ? "Special, unknown, or absent WOM role is protected."
                : womStandingAboveEligibility && !approvedForCutover
                    ? $"Mapped WOM standing '{womTarget}' exceeds evaluated eligibility '{evaluatedTarget}'; hold promotion and in-game alignment for review."
                : approvedForCutover
                    ? $"Approved cutover target is '{finalTarget}'; WOM and in-game rank changes are verified before final database approval."
                : ignoredMismatch
                    ? "Open ignored mismatch preserved while each system is independently normalized."
                    : null;
            womObservations.Add(new RankTransitionWomObservation(
                player.Username,
                womRole,
                womTargetAfterCutover,
                womFresh,
                womProtected,
                ignoredMismatch,
                womNotes));

            if (womFresh &&
                isActive &&
                !identityAmbiguous &&
                !womIdentityAmbiguous &&
                !string.IsNullOrWhiteSpace(womRole) &&
                !womSpecial &&
                !protectedDb &&
                !string.Equals(FrozenRankMapping.Normalize(womRole), womTargetAfterCutover, StringComparison.Ordinal))
            {
                var womOperation = CreateOperation(
                    TransitionOperationKind.WiseOldManRole,
                    $"wom:{FrozenRankMapping.Normalize(player.Username)}",
                    FrozenRankMapping.Normalize(player.Username),
                    "Role",
                    FrozenRankMapping.Normalize(womRole),
                    womTargetAfterCutover,
                    isExternal: true,
                    isActive,
                    Protected: false,
                    requiresReview: true,
                    snapshot,
                    player.LastSynced,
                    approvedForCutover
                        ? "Set the frozen WOM role to the final approved cutover rank and verify it before database finalization."
                        : "Rename only the frozen legacy WOM role; never infer a target from live state during retry.");
                if (options.WomUpdatesManual)
                {
                    manualWomChecks.Add(new RankTransitionManualWomCheck(
                        womOperation.OperationId,
                        womOperation.TargetKey,
                        womOperation.Before,
                        womOperation.After,
                        isActive,
                        womOperation.Reason));
                }
                else
                {
                    operations.Add(womOperation);
                }
            }

            if (approvedForCutover)
            {
                var observationIndex = observations.FindIndex(x => x.PlayerId == player.Id);
                if (observationIndex >= 0)
                {
                    observations[observationIndex] = observations[observationIndex] with
                    {
                        DatabaseCurrentRankTarget = finalTarget
                    };
                }
                promotionDecisions.Add(new PromotionDecision(
                    player.Id,
                    player.Username,
                    currentTarget,
                    evaluatedTarget!,
                    womTargetAfterCutover,
                    IsActive: true,
                    QueuedThroughApprovalFlow: false,
                    Reason: $"{evaluated!.Explanation}; approved cutover target: {finalTarget}.",
                    ApprovedForCutover: true));
                operations.Add(CreateOperation(
                    TransitionOperationKind.FinalizeApprovedPromotion,
                    $"player:{player.Id}:approved-promotion:{finalTarget}",
                    player.Id.ToString(),
                    "PromotionApproval",
                    currentBefore,
                    finalTarget,
                    isExternal: false,
                    isActive: true,
                    Protected: false,
                    requiresReview: false,
                    snapshot,
                    player.LastSynced,
                    "Finalize the owned approved promotion only after WOM and planned Discord member changes verify."));
            }
            else
            {
                if (!options.ApproveQualifyingPromotions &&
                         safePromotionBase &&
                         !womStandingAboveEligibility &&
                         IsStrictUpgrade(currentTarget, evaluatedTarget!))
                {
                    // Every pending candidate was evaluated under the old policy;
                    // it is superseded below and never suppresses a fresh
                    // candidate generated from the current evaluator.
                    promotionDecisions.Add(new PromotionDecision(
                        player.Id,
                        player.Username,
                        currentTarget,
                        evaluatedTarget!,
                        womRole,
                        IsActive: true,
                        QueuedThroughApprovalFlow: true,
                        Reason: evaluated!.Explanation));
                    operations.Add(CreateOperation(
                        TransitionOperationKind.QueuePromotionCandidate,
                        $"player:{player.Id}:promotion:{evaluatedTarget}",
                        player.Id.ToString(),
                        "PromotionCandidate",
                        null,
                        evaluatedTarget,
                        isExternal: false,
                        isActive: true,
                        Protected: false,
                        requiresReview: true,
                        snapshot,
                        player.LastSynced,
                        "Queue a pending candidate for the existing promotion approval flow; do not auto-approve."));
                }
            }

            if (!protectedDb && !approvedForCutover && !string.Equals(currentBefore, currentTarget, StringComparison.Ordinal))
            {
                operations.Add(CreateOperation(
                    TransitionOperationKind.DatabaseCurrentRank,
                    $"player:{player.Id}:current-rank",
                    player.Id.ToString(),
                    nameof(Player.CurrentRank),
                    currentBefore,
                    currentTarget,
                    isExternal: false,
                    isActive,
                    protectedDb,
                    requiresReview: false,
                    snapshot,
                    player.LastSynced,
                    "Normalize the frozen database standing rank."));
            }
        }

        // A Discord account can be linked to more than one tracker player.
        // Rebuild each shared-account plan from the complete candidate set so
        // the highest confirmed floor wins and only one member mutation is
        // emitted. The per-player planning above still supplies identity and
        // review warnings; this pass prevents duplicate/lower-role operations.
        if (discord is not null && discord.Members is not null && discordMemberRankChanges.Count > 0)
        {
            var normalizedMemberPlans = new List<DiscordRankMembershipPlan>();
            foreach (var group in discordMemberRankChanges.GroupBy(x => x.DiscordUserId, StringComparer.Ordinal))
            {
                var plan = BuildMergedApprovedDiscordMemberPlan(
                    group,
                    players,
                    observations,
                    promotionDecisions,
                    womObservations,
                    reviewSuppressedPlayerIds,
                    discord,
                    options.DiscordRankRoleIds,
                    options.DiscordMemberIdentityOverrides);
                normalizedMemberPlans.Add(plan);

                operations.RemoveAll(x =>
                    x.Kind == TransitionOperationKind.DiscordMemberRankRole &&
                    string.Equals(x.TargetKey, group.Key, StringComparison.Ordinal));
                if (plan.RequiresReview)
                {
                    warnings.Add(new TransitionWarning(
                        "CUTOVER_MEMBER_REVIEW_REQUIRED",
                        group.Key,
                        plan.ReviewReason ?? "A shared Discord account could not be assigned one safe highest-floor rank role.",
                        BlocksApply: !options.DiscordMemberRanksManual));
                }
                else if (!options.DiscordMemberRanksManual && !plan.IsNoOp)
                {
                    var mergedPlayer = players.FirstOrDefault(x => plan.PlayerIds.Contains(x.Id));
                    var snapshot = mergedPlayer?.Snapshots
                        .OrderByDescending(x => x.Timestamp)
                        .ThenByDescending(x => x.Id)
                        .FirstOrDefault();
                    operations.Add(CreateOperation(
                        TransitionOperationKind.DiscordMemberRankRole,
                        $"discord-member:{plan.DiscordUserId}:{plan.TargetRoleId}",
                        plan.DiscordUserId,
                        "RankMembership",
                        plan.ExpectedBeforeRoleHash,
                        plan.ExpectedAfterRoleHash,
                        isExternal: true,
                        isActive: true,
                        Protected: false,
                        requiresReview: false,
                        snapshot,
                        mergedPlayer?.LastSynced,
                        "Apply one highest-floor managed rank-role change for all reviewed tracker players sharing this Discord account; preserve every special/unmanaged role."));
                }
            }
            discordMemberRankChanges.Clear();
            discordMemberRankChanges.AddRange(normalizedMemberPlans);
        }

        // Capture WOM-only observations without creating external actions.
        if (womFresh)
        {
            foreach (var (usernameRaw, roleRaw) in womRoles)
            {
                var username = FrozenRankMapping.Normalize(usernameRaw);
                if (playersByUsername.ContainsKey(username)) continue;
                var normalizedUsername = FrozenRankMapping.Normalize(usernameRaw);
                var ambiguous = ambiguousWomNames.Contains(normalizedUsername);
                var special = ambiguous || FrozenRankMapping.IsSpecialOrUnknownWomRole(roleRaw);
                womObservations.Add(new RankTransitionWomObservation(
                    username,
                    roleRaw,
                    special ? null : FrozenRankMapping.MapWomRole(roleRaw),
                    true,
                    special,
                    false,
                    ambiguous
                        ? "Ambiguous WOM identity; role is protected."
                        : "WOM-only observation; no player identity was inferred."));
            }
        }

        // Pending candidates from the old policy are superseded, never deleted.
        // The original row and reason remain available in DB history.
        foreach (var candidate in pendingCandidates)
        {
            operations.Add(CreateOperation(
                TransitionOperationKind.SupersedePromotionCandidate,
                $"candidate:{candidate.Id}",
                candidate.Id.ToString(),
                "Status",
                PromotionStatus.PENDING.ToString(),
                PromotionStatus.DISMISSED.ToString(),
                isExternal: false,
                isActive: players.FirstOrDefault(x => x.Id == candidate.PlayerId)?.Status == PlayerStatus.ACTIVE,
                Protected: false,
                requiresReview: true,
                snapshot: players.FirstOrDefault(x => x.Id == candidate.PlayerId)?.Snapshots.OrderByDescending(x => x.Timestamp).ThenByDescending(x => x.Id).FirstOrDefault(),
                lastSynced: players.FirstOrDefault(x => x.Id == candidate.PlayerId)?.LastSynced,
                "Supersede an old-policy pending candidate while preserving the candidate row and audit history."));
        }

        var discordRoles = new List<DiscordRoleObservation>();
        if (discord is not null)
        {
            var transitionRoles = SelectConfiguredDiscordRoles(discord, options.DiscordRankRoleIds, warnings);
            var transitionRolesById = transitionRoles.ToDictionary(x => x.Role.RoleId, StringComparer.Ordinal);
            foreach (var role in discord.Roles)
            {
                if (!transitionRolesById.TryGetValue(role.RoleId, out var configuredRole))
                {
                    discordRoles.Add(role);
                    continue;
                }

                var targetName = configuredRole.TargetName;
                var alreadyAtTarget = configuredRole.AlreadyAtTarget;
                var protectedRole = role.Protected || string.Equals(role.RoleId, discord.GuildId, StringComparison.Ordinal);
                var collision = !alreadyAtTarget && discord.Roles.Any(other =>
                {
                    if (string.Equals(other.RoleId, role.RoleId, StringComparison.Ordinal) ||
                        !string.Equals(FrozenRankMapping.Normalize(other.NameBefore), FrozenRankMapping.Normalize(targetName), StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    // A configured role occupying the target is safe only if
                    // its own frozen source is moving away. This uses the
                    // configured source/target pair, never a live name remap
                    // (which would turn a new General into Colonel).
                    return !transitionRolesById.TryGetValue(other.RoleId, out var targetOwner) ||
                           targetOwner.AlreadyAtTarget;
                });
                var notes = protectedRole
                    ? "Managed/@everyone role is protected."
                    : collision
                        ? $"Target role name '{targetName}' already exists under another role ID; manual collision review required."
                        : role.Notes;
                if (collision)
                {
                    warnings.Add(new TransitionWarning(
                        "DISCORD_ROLE_TARGET_COLLISION",
                        role.RoleId,
                        $"Cannot rename '{role.NameBefore}' to '{targetName}' because another role ID owns the target name.",
                        BlocksApply: true));
                }
                discordRoles.Add(role with
                {
                    NameTarget = protectedRole || collision ? null : targetName,
                    Protected = protectedRole,
                    Notes = notes
                });

                if (!protectedRole && !collision && !alreadyAtTarget && !string.Equals(role.NameBefore, targetName, StringComparison.Ordinal))
                {
                    operations.Add(CreateOperation(
                        TransitionOperationKind.DiscordRoleRename,
                        $"discord-role:{role.RoleId}",
                        role.RoleId,
                        "Name",
                        role.NameBefore,
                        targetName,
                        isExternal: true,
                        isActive: true,
                        Protected: false,
                        requiresReview: true,
                        snapshot: null,
                        lastSynced: null,
                        "Rename the existing role ID only; permissions, hierarchy, and membership remain untouched."));
                }
            }
        }

        // General -> Colonel must be applied before Astral -> General when the
        // pre-cutover role set contains all three names. The role IDs remain
        // frozen; this is only a deterministic dependency ordering.
        var orderedOperations = operations
            .Where(x => x.Kind != TransitionOperationKind.DiscordRoleRename &&
                        x.Kind != TransitionOperationKind.DiscordMemberRankRole &&
                        x.Kind != TransitionOperationKind.FinalizeApprovedPromotion)
            .OrderBy(x => x.Kind switch
            {
                TransitionOperationKind.DatabaseCurrentRank => 0,
                TransitionOperationKind.DatabaseEligibleRank => 0,
                TransitionOperationKind.SupersedePromotionCandidate => 1,
                TransitionOperationKind.QueuePromotionCandidate => 2,
                _ => 3
            })
            .Concat(OrderDiscordRoleOperations(operations.Where(x => x.Kind == TransitionOperationKind.DiscordRoleRename)))
            .Concat(operations.Where(x => x.Kind == TransitionOperationKind.DiscordMemberRankRole))
            .Concat(operations.Where(x => x.Kind == TransitionOperationKind.FinalizeApprovedPromotion))
            .ToArray();
        var frozenDiscordRankRoleIds = BuildFrozenDiscordRankRoleIds(options.DiscordRankRoleIds, warnings);

        return new RankTransitionManifest
        {
            ManifestId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = now,
            FrozenMappingVersion = FrozenRankMapping.Version,
            FrozenMapping = FrozenRankMapping.Document(),
            CompleteReadOnlyScope = options.IncludeWom && options.IncludeDiscord,
            ApprovedForCutover = options.ApproveQualifyingPromotions,
            CutoverApprovalPolicyVersion = RankTransitionManifest.ApprovedCutoverPolicyVersion,
            DiscordMemberRanksManual = options.DiscordMemberRanksManual,
            WomUpdatesManual = options.WomUpdatesManual,
            Players = observations,
            WomObservations = womObservations,
            ManualWomChecks = manualWomChecks,
            DiscordRoles = discordRoles,
            DiscordMembers = discord?.Members ?? [],
            FrozenDiscordRankRoleIds = frozenDiscordRankRoleIds,
            DiscordMemberRankChanges = discordMemberRankChanges,
            PromotionDecisions = promotionDecisions,
            Warnings = warnings,
            Operations = orderedOperations,
            Environment = _environment,
            ConfigKeyGuidance = BuildConfigGuidance(options.DiscordRankRoleIds)
        };
    }

    private static DiscordRankMembershipPlan? BuildApprovedDiscordMemberPlan(
        Player player,
        string finalTarget,
        DiscordGuildObservation? discord,
        IReadOnlyDictionary<string, string>? configuredRoleIds,
        IReadOnlyDictionary<int, string>? reviewedIdentityOverrides,
        bool manualMemberRanks,
        List<TransitionWarning> warnings)
    {
        if (discord is null || !discord.MembersFullyObserved || discord.Members is null)
        {
            return null;
        }

        if (configuredRoleIds is null || configuredRoleIds.Count == 0)
        {
            warnings.Add(new TransitionWarning(
                "DISCORD_MEMBER_ROLE_CONFIG_MISSING",
                player.Username,
                "No configured Discord rank-role IDs were available for the approved member-role change.",
                BlocksApply: !manualMemberRanks));
            return null;
        }

        var identity = DiscordMemberIdentityResolver.Resolve(
                [new DiscordPlayerIdentity(player.Id, player.Username)],
                discord.Members,
                reviewedIdentityOverrides)
            .Single();
        if (!identity.IsResolved || string.IsNullOrWhiteSpace(identity.DiscordUserId))
        {
            warnings.Add(new TransitionWarning(
                "DISCORD_MEMBER_IDENTITY_REVIEW",
                player.Username,
                identity.Reason,
                BlocksApply: !manualMemberRanks));
            return null;
        }

        var targetRoleId = ResolveConfiguredRankRoleId(finalTarget, configuredRoleIds);
        if (string.IsNullOrWhiteSpace(targetRoleId))
        {
            warnings.Add(new TransitionWarning(
                "DISCORD_MEMBER_TARGET_ROLE_MISSING",
                player.Username,
                $"No configured Discord rank-role ID was found for final cutover rank '{finalTarget}'.",
                BlocksApply: !manualMemberRanks));
            return null;
        }

        var targetRole = discord.Roles.FirstOrDefault(x => string.Equals(x.RoleId, targetRoleId, StringComparison.Ordinal));
        if (targetRole is null || targetRole.Protected)
        {
            warnings.Add(new TransitionWarning(
                "DISCORD_MEMBER_TARGET_ROLE_PROTECTED",
                player.Username,
                targetRole is null
                    ? $"Configured Discord target role '{targetRoleId}' was not observed."
                    : $"Configured Discord target role '{targetRoleId}' is managed or protected.",
                BlocksApply: !manualMemberRanks));
            return null;
        }

        var managedRoleIds = configuredRoleIds.Values
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var rankFloorByRoleId = BuildRankFloorByRoleId(configuredRoleIds);
        var candidate = new DiscordRankMembershipCandidate(
            player.Id,
            player.Username,
            identity.DiscordUserId,
            finalTarget,
            targetRoleId,
            RankRules.RankOrder(finalTarget),
            Confirmed: true);
        var plan = DiscordRankMembershipPlanner.BuildPlans(
                [candidate],
                discord.Members,
                managedRoleIds,
                rankFloorByRoleId: rankFloorByRoleId)
            .Single();
        return plan;
    }

    private static DiscordRankMembershipPlan BuildMergedApprovedDiscordMemberPlan(
        IGrouping<string, DiscordRankMembershipPlan> group,
        IReadOnlyList<Player> players,
        IReadOnlyList<RankTransitionPlayerObservation> observations,
        IReadOnlyList<PromotionDecision> promotionDecisions,
        IReadOnlyList<RankTransitionWomObservation> womObservations,
        IReadOnlySet<int> reviewSuppressedPlayerIds,
        DiscordGuildObservation discord,
        IReadOnlyDictionary<string, string>? configuredRoleIds,
        IReadOnlyDictionary<int, string>? reviewedIdentityOverrides)
    {
        if (configuredRoleIds is null || configuredRoleIds.Count == 0 || discord.Members is null)
        {
            return new DiscordRankMembershipPlan(
                group.Key,
                group.SelectMany(x => x.PlayerIds).Distinct().OrderBy(x => x).ToArray(),
                group.SelectMany(x => x.TargetRanks).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                "",
                0,
                [],
                [],
                [],
                [],
                "",
                "",
                $"discord-member-rank:{group.Key}:review",
                false,
                true,
                "Shared Discord account cannot be rebuilt without the complete configured rank-role map.");
        }

        var rankFloorByRoleId = BuildRankFloorByRoleId(configuredRoleIds);
        var managedRoleIds = configuredRoleIds.Values
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var candidatePlayerIds = group
            .SelectMany(x => x.PlayerIds)
            .Distinct()
            .ToHashSet();
        var candidates = new List<DiscordRankMembershipCandidate>();
        foreach (var playerId in candidatePlayerIds)
        {
            var player = players.FirstOrDefault(x => x.Id == playerId);
            var observation = observations.FirstOrDefault(x => x.PlayerId == playerId);
            var targetRank = observation?.DatabaseCurrentRankTarget ?? "";
            var targetRoleId = ResolveConfiguredRankRoleId(targetRank, configuredRoleIds) ?? "";
            candidates.Add(new DiscordRankMembershipCandidate(
                playerId,
                player?.Username ?? $"player-{playerId}",
                group.Key,
                targetRank,
                targetRoleId,
                IsKnownCanonicalRank(targetRank) ? RankRules.RankOrder(targetRank) : int.MinValue,
                Confirmed: true));
        }

        var initialFloor = candidates
            .Where(x => x.Confirmed)
            .Select(x => x.TargetFloor)
            .DefaultIfEmpty(int.MinValue)
            .Max();
        var identities = DiscordMemberIdentityResolver.Resolve(
            players.Select(x => new DiscordPlayerIdentity(x.Id, x.Username)),
            discord.Members,
            reviewedIdentityOverrides);
        var unresolvedLinked = identities.FirstOrDefault(x =>
            !x.IsResolved && x.CandidateUserIds.Contains(group.Key, StringComparer.Ordinal));
        if (unresolvedLinked is not null)
        {
            return ReviewApprovedMemberPlan(
                group.Key,
                candidatePlayerIds.Append(unresolvedLinked.PlayerId),
                candidates.Select(x => x.TargetRank),
                $"A linked tracker identity ({unresolvedLinked.Username}) is ambiguous for Discord account '{group.Key}'; an explicit reviewed override is required before using one shared rank floor.");
        }
        foreach (var identity in identities.Where(x =>
                     x.IsResolved &&
                     string.Equals(x.DiscordUserId, group.Key, StringComparison.Ordinal) &&
                     !candidatePlayerIds.Contains(x.PlayerId)))
        {
            var player = players.First(x => x.Id == identity.PlayerId);
            var observation = observations.FirstOrDefault(x => x.PlayerId == player.Id);
            if (observation is null) continue;
            if (observation.Protected)
            {
                return ReviewApprovedMemberPlan(
                    group.Key,
                    candidatePlayerIds.Append(player.Id),
                    candidates.Select(x => x.TargetRank),
                    $"A linked player ({player.Username}) has a protected or unknown mapped standing; the shared Discord floor cannot be proven safely.");
            }

            var standing = observation.DatabaseCurrentRankTarget;
            if (!IsKnownCanonicalRank(standing)) continue;
            var standingFloor = RankRules.RankOrder(standing);
            if (standingFloor <= initialFloor) continue;
            var womObservation = womObservations.FirstOrDefault(x =>
                string.Equals(
                    FrozenRankMapping.Normalize(x.Username),
                    FrozenRankMapping.Normalize(player.Username),
                    StringComparison.OrdinalIgnoreCase));
            if (womObservation?.IgnoredMismatch == true || womObservation?.Protected == true ||
                reviewSuppressedPlayerIds.Contains(player.Id))
            {
                return ReviewApprovedMemberPlan(
                    group.Key,
                    candidatePlayerIds.Append(player.Id),
                    candidates.Select(x => x.TargetRank).Append(standing),
                    $"A linked player ({player.Username}) has a protected, ignored, or review-suppressed higher standing '{standing}'; shared Discord membership requires review.");
            }
            if (!observation.IsActive)
            {
                return ReviewApprovedMemberPlan(
                    group.Key,
                    candidatePlayerIds.Append(player.Id),
                    candidates.Select(x => x.TargetRank).Append(standing),
                    $"A linked inactive player ({player.Username}) has the higher mapped standing '{standing}'; no Discord membership alignment is safe without review.");
            }

            var standingRoleId = ResolveConfiguredRankRoleId(standing, configuredRoleIds);
            if (string.IsNullOrWhiteSpace(standingRoleId))
            {
                return ReviewApprovedMemberPlan(
                    group.Key,
                    candidatePlayerIds.Append(player.Id),
                    candidates.Select(x => x.TargetRank).Append(standing),
                    $"No configured Discord rank-role ID is available for linked higher standing '{standing}' ({player.Username}).");
            }
            candidates.Add(new DiscordRankMembershipCandidate(
                player.Id,
                player.Username,
                group.Key,
                standing,
                standingRoleId,
                standingFloor,
                Confirmed: true));
            candidatePlayerIds.Add(player.Id);
        }
        return DiscordRankMembershipPlanner.BuildPlans(
                candidates,
                discord.Members,
                managedRoleIds,
                rankFloorByRoleId: rankFloorByRoleId)
            .Single();
    }

    private static DiscordRankMembershipPlan ReviewApprovedMemberPlan(
        string discordUserId,
        IEnumerable<int> playerIds,
        IEnumerable<string> targetRanks,
        string reason) =>
        new(
            discordUserId,
            playerIds.Distinct().OrderBy(x => x).ToArray(),
            targetRanks.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            "",
            0,
            [],
            [],
            [],
            [],
            "",
            "",
            $"discord-member-rank:{discordUserId}:review",
            false,
            true,
            reason);

    private static IReadOnlyDictionary<string, int> BuildRankFloorByRoleId(
        IReadOnlyDictionary<string, string> configuredRoleIds)
    {
        var floors = new Dictionary<string, int>(StringComparer.Ordinal);
        var legacyKeysPresent = HasLegacyDiscordConfigKeys(configuredRoleIds);
        foreach (var entry in configuredRoleIds)
        {
            if (string.IsNullOrWhiteSpace(entry.Value)) continue;
            // Legacy configuration keys describe the pre-cutover role names;
            // post-cutover keys are already canonical. Applying the frozen
            // legacy map to a new General key would incorrectly give the
            // former Astral role Colonel's floor.
            var rank = legacyKeysPresent
                ? FrozenRankMapping.Map(entry.Key)
                : FrozenRankMapping.Normalize(entry.Key);
            if (!IsKnownCanonicalRank(rank)) continue;
            var floor = RankRules.RankOrder(rank);
            if (!floors.TryGetValue(entry.Value, out var existing) || floor > existing)
            {
                floors[entry.Value] = floor;
            }
        }
        return floors;
    }

    private static string? ResolveConfiguredRankRoleId(
        string rank,
        IReadOnlyDictionary<string, string> configuredRoleIds)
    {
        var normalizedRank = FrozenRankMapping.Normalize(rank);
        var legacyKeysPresent = HasLegacyDiscordConfigKeys(configuredRoleIds);
        if (!legacyKeysPresent)
        {
            var direct = configuredRoleIds.FirstOrDefault(x =>
                string.Equals(FrozenRankMapping.Normalize(x.Key), normalizedRank, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(direct.Value)) return direct.Value;
        }
        // For the three renamed roles, the old source key remains the frozen
        // identity until the reviewed config cutover is complete. This keeps
        // General on the former Astral role ID and Colonel on former General.
        foreach (var source in FrozenRankMapping.DiscordRenames
                     .Where(x => string.Equals(FrozenRankMapping.Normalize(x.Value), normalizedRank, StringComparison.OrdinalIgnoreCase))
                     .Select(x => x.Key)
                     .Append(rank))
        {
            var entry = configuredRoleIds.FirstOrDefault(x =>
                string.Equals(FrozenRankMapping.Normalize(x.Key), FrozenRankMapping.Normalize(source), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(entry.Value)) return entry.Value;
        }
        return null;
    }

    private static bool HasLegacyDiscordConfigKeys(IReadOnlyDictionary<string, string> configuredRoleIds) =>
        configuredRoleIds.Keys.Any(x =>
            string.Equals(FrozenRankMapping.Normalize(x), "Officer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(FrozenRankMapping.Normalize(x), "Astral", StringComparison.OrdinalIgnoreCase));

    private static string HighestCanonicalRank(params string?[] ranks)
    {
        var known = ranks
            .Where(x => IsKnownCanonicalRank(x!))
            .Select(x => FrozenRankMapping.Normalize(x!))
            .OrderBy(RankRules.RankOrder)
            .ToArray();
        return known.Length == 0 ? "Brassican" : known[^1];
    }

    private static RankTransitionOperation CreateOperation(
        TransitionOperationKind kind,
        string operationSeed,
        string targetKey,
        string? propertyName,
        string? before,
        string? after,
        bool isExternal,
        bool isActive,
        bool Protected,
        bool requiresReview,
        PlayerSnapshot? snapshot,
        DateTimeOffset? lastSynced,
        string reason)
    {
        var operationId = ComputeOperationId(kind, operationSeed, before, after);
        return new RankTransitionOperation(
            operationId,
            kind,
            targetKey,
            propertyName,
            before,
            after,
            isExternal,
            isActive,
            Protected,
            requiresReview,
            snapshot?.Id,
            snapshot is null ? null : SnapshotHash(snapshot),
            lastSynced,
            reason);
    }

    public static string ComputeOperationId(
        TransitionOperationKind kind,
        string targetKey,
        string? before,
        string? after)
    {
        var seed = $"{kind}|{targetKey}|{before ?? "<null>"}|{after ?? "<null>"}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant()[..24];
    }

    public static string SnapshotHash(PlayerSnapshot snapshot)
    {
        var seed = $"{snapshot.Id}|{snapshot.PlayerId}|{snapshot.Timestamp:O}|{snapshot.TotalLevel}|{snapshot.Ehb:R}|{snapshot.Ehp:R}|{snapshot.Collections}|{snapshot.PetCount}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();
    }

    private static bool IsStrictUpgrade(string current, string evaluated)
    {
        if (string.Equals(FrozenRankMapping.Normalize(current), FrozenRankMapping.Normalize(evaluated), StringComparison.OrdinalIgnoreCase)) return false;
        return RankRules.RankOrder(evaluated) > RankRules.RankOrder(current);
    }

    private static bool IsKnownDatabaseRank(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return RankRules.IsKnownClanRank(value) || FrozenRankMapping.LegacyMappings.ContainsKey(FrozenRankMapping.Normalize(value));
    }

    private static bool IsKnownCanonicalRank(string? value) =>
        !string.IsNullOrWhiteSpace(value) && RankRules.IsKnownClanRank(value);

    private static IEnumerable<RankTransitionOperation> OrderDiscordRoleOperations(IEnumerable<RankTransitionOperation> operations)
    {
        var list = operations.ToList();
        var byBefore = list
            .Where(x => !string.IsNullOrWhiteSpace(x.Before))
            .ToDictionary(x => FrozenRankMapping.Normalize(x.Before), StringComparer.OrdinalIgnoreCase);
        var depthCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int Depth(RankTransitionOperation operation, HashSet<string>? visiting = null)
        {
            var key = FrozenRankMapping.Normalize(operation.Before);
            if (depthCache.TryGetValue(key, out var cached)) return cached;
            visiting ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!visiting.Add(key)) return 0;
            var depth = byBefore.TryGetValue(FrozenRankMapping.Normalize(operation.After), out var dependency)
                ? 1 + Depth(dependency, visiting)
                : 0;
            visiting.Remove(key);
            depthCache[key] = depth;
            return depth;
        }

        // A source role that currently owns the target name must move first
        // (General -> Colonel, then Astral -> General).
        return list.OrderBy(x => Depth(x)).ThenBy(x => x.TargetKey, StringComparer.Ordinal);
    }

    private static string? TryReadUsername(string metadataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            foreach (var key in new[] { "Username", "Player", "PreviousUsername" })
            {
                if (doc.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
        }
        catch (JsonException)
        {
            // An unrelated malformed lifecycle event must not make planning
            // unsafe; the event is simply not used as a username hint.
        }
        return null;
    }

    private static string SafeExceptionName(Exception ex) => ex.GetType().Name;

    private static IReadOnlyList<ConfiguredDiscordRole> SelectConfiguredDiscordRoles(
        DiscordGuildObservation discord,
        IReadOnlyDictionary<string, string>? configuredRoleIds,
        List<TransitionWarning> warnings)
    {
        // A null map is used only by library tests/diagnostics. Production CLI
        // passes the configuration map explicitly, so a missing map cannot
        // accidentally rename an unrelated duplicate role by display name.
        if (configuredRoleIds is null)
        {
            return discord.Roles
                .Where(role => FrozenRankMapping.TryMapDiscordRoleName(role.NameBefore, out var target))
                .Select(role => new ConfiguredDiscordRole(
                    role,
                    FrozenRankMapping.Normalize(role.NameBefore),
                    FrozenRankMapping.TryMapDiscordRoleName(role.NameBefore, out var target) ? target : FrozenRankMapping.Normalize(role.NameBefore)))
                .ToArray();
        }
        var selected = new List<ConfiguredDiscordRole>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var legacyKeysPresent = HasLegacyDiscordConfigKeys(configuredRoleIds);
        foreach (var source in FrozenRankMapping.DiscordRenames.Keys)
        {
            var target = FrozenRankMapping.DiscordRenames[source];
            var configuredKey = legacyKeysPresent ? source : target;
            var configEntry = configuredRoleIds.FirstOrDefault(x => string.Equals(FrozenRankMapping.Normalize(x.Key), configuredKey, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(configEntry.Key) && !legacyKeysPresent)
            {
                // A partially migrated operator configuration may still have
                // a source key. Use it only as a fallback when the unambiguous
                // post-cutover target key is absent; never reinterpret a
                // present target key by live role name.
                configEntry = configuredRoleIds.FirstOrDefault(x => string.Equals(FrozenRankMapping.Normalize(x.Key), source, StringComparison.OrdinalIgnoreCase));
            }
            if (string.IsNullOrWhiteSpace(configEntry.Key) || string.IsNullOrWhiteSpace(configEntry.Value))
            {
                warnings.Add(new TransitionWarning(
                    "DISCORD_ROLE_ID_MISSING",
                    source,
                    $"Configure DiscordBot:RankRoleIds:{target} with the existing role ID before applying role renames.",
                    BlocksApply: true));
                continue;
            }
            if (!ids.Add(configEntry.Value))
            {
                warnings.Add(new TransitionWarning(
                    "DISCORD_ROLE_ID_COLLISION",
                    source,
                    $"Configured Discord role ID '{configEntry.Value}' is reused by another legacy rank; no rename is planned.",
                    BlocksApply: true));
                continue;
            }
            var role = discord.Roles.FirstOrDefault(x => string.Equals(x.RoleId, configEntry.Value, StringComparison.Ordinal));
            if (role is null)
            {
                warnings.Add(new TransitionWarning(
                    "DISCORD_ROLE_ID_NOT_FOUND",
                    source,
                    $"Configured Discord role ID '{configEntry.Value}' was not observed in the guild.",
                    BlocksApply: true));
                continue;
            }
            if (role.Protected)
            {
                warnings.Add(new TransitionWarning(
                    "DISCORD_ROLE_PROTECTED",
                    source,
                    $"Configured Discord role ID '{configEntry.Value}' is managed or @everyone and cannot be renamed.",
                    BlocksApply: true));
                continue;
            }
            var isSource = string.Equals(FrozenRankMapping.Normalize(role.NameBefore), FrozenRankMapping.Normalize(source), StringComparison.OrdinalIgnoreCase);
            var isTarget = string.Equals(FrozenRankMapping.Normalize(role.NameBefore), FrozenRankMapping.Normalize(target), StringComparison.OrdinalIgnoreCase);
            if (!isSource && !isTarget)
            {
                warnings.Add(new TransitionWarning(
                    "DISCORD_ROLE_NAME_DRIFT",
                    source,
                    $"Configured role ID '{configEntry.Value}' is named '{role.NameBefore}', not the frozen source '{source}' or exact target '{target}'.",
                    BlocksApply: true));
                continue;
            }
            selected.Add(new ConfiguredDiscordRole(role, source, target));
        }
        return selected;
    }

    private static IReadOnlyDictionary<string, string> BuildConfigGuidance(IReadOnlyDictionary<string, string>? configuredRoleIds)
    {
        var guidance = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:DefaultConnection"] = "Read from the process environment or local secret store; never put a secret in the manifest.",
            ["WiseOldMan:GroupId"] = "Required for fresh WOM role observations.",
            ["WiseOldMan:VerificationCode"] = "Not required for the standard manual-WOM cutover; only an explicitly opted-in library manifest that mutates WOM would need it. The value is never printed or persisted.",
            ["DiscordBot:Token"] = "Required for plan (fresh Discord GET observations) and explicit Discord apply/rollback; value is never printed or persisted.",
            ["DiscordBot:GuildId"] = "Guild whose roles and membership are observed.",
            ["RankTransition:DiscordMemberRanksManual"] = "Approved CLI cutovers freeze the full Discord roster for a human member-rank report; the tool never writes member role membership unless the library caller explicitly selects automatic mode.",
            ["RankTransition:WomUpdatesManual"] = "Standard cutovers treat the in-game roster as authoritative and use fresh WOM reads only as a reconciliation gate; the tool never writes or rolls back WOM roles.",
            ["DiscordBot:RankRoleIdsMigration"] = "After review, remove retired DiscordBot:RankRoleIds:Officer and DiscordBot:RankRoleIds:Astral keys. The target keys below retain the frozen IDs: Brassican uses the former Officer ID, General uses the former Astral ID, and Colonel uses the former General ID."
        };

        // The running tool still reads the pre-cutover source keys so that a
        // reviewed manifest can be made from the existing configuration. The
        // manifest guidance deliberately emits only the post-cutover keys:
        // General must point at the former Astral role, never the former
        // General role (which is now Colonel).
        var legacyKeysPresent = configuredRoleIds is not null && HasLegacyDiscordConfigKeys(configuredRoleIds);
        foreach (var source in FrozenRankMapping.DiscordRenames.Keys)
        {
            string? configuredId = null;
            if (configuredRoleIds is not null)
            {
                var targetName = FrozenRankMapping.DiscordRenames[source];
                var preferredKey = legacyKeysPresent ? source : targetName;
                foreach (var entry in configuredRoleIds)
                {
                    if (string.Equals(
                            FrozenRankMapping.Normalize(entry.Key),
                            FrozenRankMapping.Normalize(preferredKey),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        configuredId = entry.Value;
                        break;
                    }
                }
                if (string.IsNullOrWhiteSpace(configuredId))
                {
                    foreach (var entry in configuredRoleIds)
                    {
                        if (string.Equals(
                                FrozenRankMapping.Normalize(entry.Key),
                                FrozenRankMapping.Normalize(legacyKeysPresent ? targetName : source),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            configuredId = entry.Value;
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(configuredId)) continue;

            var target = FrozenRankMapping.DiscordRenames[source];
            guidance[$"DiscordBot:RankRoleIds:{target}"] = configuredId;
        }

        return guidance;
    }

    private static IReadOnlyDictionary<string, string> BuildFrozenDiscordRankRoleIds(
        IReadOnlyDictionary<string, string>? configuredRoleIds,
        List<TransitionWarning> warnings)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (configuredRoleIds is null) return result;
        var legacyKeysPresent = HasLegacyDiscordConfigKeys(configuredRoleIds);
        foreach (var entry in configuredRoleIds)
        {
            if (string.IsNullOrWhiteSpace(entry.Value)) continue;
            // The old source keys must be frozen through the one-time mapping,
            // while a fully migrated config already contains canonical names.
            // In particular, post-cutover General is still General; mapping it
            // again would collide with the post-cutover Colonel key.
            var canonical = legacyKeysPresent
                ? FrozenRankMapping.Map(entry.Key)
                : FrozenRankMapping.Normalize(entry.Key);
            if (!IsKnownCanonicalRank(canonical)) continue;
            if (result.TryGetValue(canonical, out var existing) &&
                !string.Equals(existing, entry.Value, StringComparison.Ordinal))
            {
                warnings.Add(new TransitionWarning(
                    "DISCORD_RANK_ROLE_ID_COLLISION",
                    canonical,
                    $"Configured Discord IDs '{existing}' and '{entry.Value}' both map to canonical rank '{canonical}'; manual rank suggestions are held.",
                    BlocksApply: true));
                continue;
            }
            result[canonical] = entry.Value;
        }
        return result;
    }
}
