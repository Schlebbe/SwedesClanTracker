using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.Worker;

public class DiscordPromotionBotWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    IPlayerUpdateQueue queue,
    AppStatusReporter statusReporter,
    ILogger<DiscordPromotionBotWorker> logger) : BackgroundService
{
    private DiscordSocketClient? _client;
    private DiscordBotOptions _options = new();
    private int _discordDeleteDelayMinutes = 5;
    private int _discordDeleteHardCapMinutes = 10;
    private readonly TimeZoneInfo _swedishTimeZone = ResolveSwedishTimeZone();
    private static readonly IReadOnlyList<WomRoleChoice> WomRoleChoices = RankRules.AssignableClanRanks
        .Select(rank => new WomRoleChoice(rank, RankRules.ToWomRoleValue(rank)))
        .Append(new WomRoleChoice("Imp", "imp"))
        .ToArray();
    private static readonly IReadOnlyList<string> DiscordRankNames = RankRules.AssignableClanRanks;
    private enum TrackedMessageState
    {
        Found,
        Missing,
        Unknown
    }
    private enum PostedMessageLookupState
    {
        Found,
        Missing,
        Malformed,
        Unknown
    }
    private sealed record TempleRosterLookupResult(bool Succeeded, HashSet<string> Usernames);

    private static bool IsDiscordMessageMissingOrInaccessible(Discord.Net.HttpException ex)
    {
        return ex.HttpCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden ||
               ex.DiscordCode is DiscordErrorCode.UnknownMessage or
                   DiscordErrorCode.UnknownChannel or
                   DiscordErrorCode.UnknownWebhook or
                   DiscordErrorCode.UnknownInteraction or
                   DiscordErrorCode.MissingPermissions or
                   DiscordErrorCode.InsufficientPermissions;
    }

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lookupBackoffUntilByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ulong, MessagePatchState> _messagePatchStateByMessageId = new();
    private static readonly TimeSpan MessagePatchMinInterval = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _discordMemberCacheLock = new(1, 1);
    private readonly SemaphoreSlim _discordClearCommandLock = new(1, 1);
    private IReadOnlyList<DiscordMemberLookupCandidate>? _discordMemberCache;
    private DateTimeOffset _discordMemberCacheValidUntil = DateTimeOffset.MinValue;
    private static readonly TimeSpan DiscordGuessCommandTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan DiscordMemberCacheLockTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DiscordMemberDownloadTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DiscordMemberWarmupDownloadTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DiscordMemberSearchTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DiscordMemberSearchTotalTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DiscordBulkDeleteMaxAge = TimeSpan.FromDays(14);
    private const int DiscordClearPageSize = 100;
    private static readonly TimeSpan TempleNameChangeDetectionWindow = TimeSpan.FromHours(6);
    private static readonly TimeSpan WomOnlyPostGracePeriod = TimeSpan.FromMinutes(5);
    private const string TempleNameChangeResolvedByLiveCheckEventType = "TEMPLE_NAME_CHANGE_RESOLVED_BY_LIVE_CHECK";
    private static readonly string[] AdminHistoryEventTypes =
    [
        "PROMOTION_CANDIDATE_CREATED",
        "PROMOTION_DISCORD_ACTION_APPLIED",
        "PROMOTION_DISCORD_RANK_ACTION_APPLIED",
        "NEW_PLAYER",
        "MISSING_IN_ROSTER",
        "STATUS_UPDATED",
        "MERGE_SUGGESTED",
        "RENAME_RECHECK_REQUESTED",
        DiscordMergeCardRules.SupersededCardHandledEventType,
        "MERGE_ACTION_APPLIED",
        "TEMPLE_MISSING_ACTION_APPLIED",
        "WOM_MISSING_ACTION_APPLIED",
        "WOM_ONLY_ACTION_APPLIED",
        "WOM_RANK_MISMATCH_REQUIRED",
        "WOM_RANK_MISMATCH_ACTION_APPLIED",
        "WOM_ROLE_UPDATE_APPLIED",
        "PLAYER_SNAPSHOT_CONTAMINATION_REQUIRED"
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _options = configuration.GetSection("DiscordBot").Get<DiscordBotOptions>() ?? new DiscordBotOptions();
        _discordDeleteDelayMinutes = Math.Max(1, configuration.GetValue<int?>("Tracker:DiscordDeleteDelayMinutes") ?? 5);
        _discordDeleteHardCapMinutes = Math.Max(1, configuration.GetValue<int?>("Tracker:DiscordDeleteHardCapMinutes") ?? 10);
        if (!_options.Enabled)
        {
            await statusReporter.ReportAsync("Discord", "Disabled", "Discord bot is disabled.", stoppingToken);
            logger.LogInformation("Discord bot disabled.");
            return;
        }
        if (string.IsNullOrWhiteSpace(_options.Token) || _options.ChannelId <= 0)
        {
            await statusReporter.ReportAsync("Discord", "Misconfigured", "Discord bot token or channel id is missing.", stoppingToken);
            logger.LogWarning("Discord bot enabled but Token/ChannelId missing.");
            return;
        }

        await statusReporter.ReportAsync("Discord", "Starting", "Discord worker is logging in.", stoppingToken);
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.GuildMembers,
            AlwaysDownloadUsers = true,
            LargeThreshold = 250
        });

        _client.Log += msg =>
        {
            logger.LogInformation("Discord: {Msg}", msg.Message);
            return Task.CompletedTask;
        };
        _client.Ready += OnReadyAsync;
        _client.ButtonExecuted += HandleButtonAsync;
        _client.SelectMenuExecuted += HandleSelectMenuAsync;
        _client.ModalSubmitted += HandleModalSubmittedAsync;
        _client.SlashCommandExecuted += HandleSlashCommandAsync;

        await _client.LoginAsync(TokenType.Bot, _options.Token);
        await _client.StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        await statusReporter.ReportAsync("Discord", "Online", "Discord worker is connected.", stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Process admin/button actions first so user feedback updates are not delayed behind posting/reconciliation work.
            await RunDiscordStep("ProcessMessageActionUpdates", () => ProcessMessageActionUpdates(stoppingToken), stoppingToken);
            await RunDiscordStep("ProcessTempleMissingActionUpdates", () => ProcessTempleMissingActionUpdates(stoppingToken), stoppingToken);
            await RunDiscordStep("ProcessMergeActionUpdates", () => ProcessMergeActionUpdates(stoppingToken), stoppingToken);
            await RunDiscordStep("ProcessScheduledDeletes", () => ProcessScheduledDeletes(stoppingToken), stoppingToken);
            await RunDiscordStep("PostPendingPromotionCandidates", () => PostPendingPromotionCandidates(stoppingToken), stoppingToken);
            await RunDiscordStep("PostTempleNameChangeNeededMessages", () => PostTempleNameChangeNeededMessages(stoppingToken), stoppingToken);
            await RunDiscordStep("PostTempleMissingActionMessages", () => PostTempleMissingActionMessages(stoppingToken), stoppingToken);
            await RunDiscordStep("PostWomMissingActionMessages", () => PostWomMissingActionMessages(stoppingToken), stoppingToken);
            await RunDiscordStep("PostWomOnlyActionMessages", () => PostWomOnlyActionMessages(stoppingToken), stoppingToken);
            await RunDiscordStep("PostWomRankMismatchMessages", () => PostWomRankMismatchMessages(stoppingToken), stoppingToken);
            await RunDiscordStep("PostMergeActionMessages", () => PostMergeActionMessages(stoppingToken), stoppingToken);
            await RunDiscordStep("ProcessReviewCardRequeueRequests", () => ProcessReviewCardRequeueRequests(stoppingToken), stoppingToken);
            await RunDiscordStep("UpdatePetHiscoresMessages", () => UpdatePetHiscoresMessages(stoppingToken), stoppingToken);
            await RunDiscordStep("ReconcileCompletedMessageDeletes", () => ReconcileCompletedMessageDeletes(stoppingToken), stoppingToken);
            await RunDiscordStep("ReconcileOrphanTrackerCards", () => ReconcileOrphanTrackerCards(stoppingToken), stoppingToken);
            await statusReporter.ReportAsync("Discord", "Waiting", "Waiting for the next Discord maintenance cycle.", stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
    }

    private async Task RunDiscordStep(string stepName, Func<Task> action, CancellationToken ct)
    {
        var friendlyStep = FriendlyStepName(stepName);
        try
        {
            await statusReporter.ReportAsync("Discord", "Working", friendlyStep, ct, new { Step = stepName });
            await action();
            await statusReporter.ReportAsync("Discord", "Online", $"Finished: {friendlyStep}", ct, new { Step = stepName });
        }
        catch (Exception ex)
        {
            await statusReporter.ReportAsync("Discord", "Error", $"Discord step failed: {friendlyStep}", ct, new { Step = stepName, Error = ex.GetType().Name });
            logger.LogError(ex, "Discord worker step failed: {Step}", stepName);
        }
    }

    private static async Task CloseOpenLifecycleEventsAsync(TrackerDbContext db, int playerId, params string[] eventTypes)
    {
        var events = await db.LifecycleEvents
            .Where(x => x.PlayerId == playerId && x.Status == "OPEN" && eventTypes.Contains(x.EventType))
            .ToListAsync();
        foreach (var ev in events)
        {
            ev.Status = "DONE";
        }
    }

    private static async Task<SwedesClanTracker.Core.WomRoleUpdateResult> ApplyWomRoleUpdateForApprovedPromotionAsync(
        TrackerDbContext db,
        IWiseOldManClient wiseOldManClient,
        Player player,
        PromotionCandidate candidate,
        string handledBy,
        ulong handledByDiscordUserId,
        string source,
        CancellationToken ct)
    {
        var womRoleBefore = await wiseOldManClient.GetMemberRoleAsync(player.Username, ct);
        var result = await wiseOldManClient.UpdateMemberRoleAsync(player.Username, candidate.NewRank, ct);
        var womRoleAfter = result.UpdatedRole ?? womRoleBefore;

        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = player.Id,
            EventType = "WOM_ROLE_UPDATE_APPLIED",
            MetadataJson = JsonUtil.Serialize(new
            {
                CandidateId = candidate.Id,
                Player = player.Username,
                RequestedRole = candidate.NewRank,
                PreviousWomRole = womRoleBefore,
                UpdatedRole = result.UpdatedRole,
                Success = result.Success,
                HttpStatus = result.HttpStatus,
                WiseOldManPlayerId = result.WomPlayerId,
                WiseOldManDisplayName = result.DisplayName,
                Details = result.Details,
                HandledBy = handledBy,
                HandledByDiscordUserId = handledByDiscordUserId,
                Source = $"promotion-approval-{source}"
            }),
            Status = "DONE",
            CreatedAt = DateTimeOffset.UtcNow
        });

        if (IsRankAligned(candidate.NewRank, womRoleAfter))
        {
            await CloseOpenLifecycleEventsAsync(db, player.Id, "WOM_RANK_MISMATCH_REQUIRED");
        }
        else
        {
            await EnsurePromotionWomRankMismatchLifecycleAsync(db, player, womRoleAfter, source, ct);
        }

        return result;
    }

    private static async Task EnsurePromotionWomRankMismatchLifecycleAsync(
        TrackerDbContext db,
        Player player,
        string? womRole,
        string source,
        CancellationToken ct)
    {
        if (player.Status != PlayerStatus.ACTIVE) return;
        if (string.IsNullOrWhiteSpace(womRole)) return;
        if (string.Equals(player.CurrentRank, "Recruit", StringComparison.OrdinalIgnoreCase)) return;
        if (RankRules.IsSpecialWomRole(womRole)) return;
        if (IsRankAligned(player.CurrentRank, womRole)) return;

        var now = DateTimeOffset.UtcNow;
        var direction = GetWomRankMismatchDirection(player.CurrentRank, womRole);
        var openMismatches = await db.LifecycleEvents
            .Where(x =>
                x.PlayerId == player.Id &&
                x.EventType == "WOM_RANK_MISMATCH_REQUIRED" &&
                x.Status == "OPEN")
            .ToListAsync(ct);

        var metadata = JsonUtil.Serialize(new
        {
            player.Username,
            ExpectedRank = player.CurrentRank,
            ActualWomRole = womRole,
            Direction = direction,
            Source = $"promotion-approval-{source}",
            DetectedAt = now
        });

        if (openMismatches.Count > 0)
        {
            foreach (var ev in openMismatches)
            {
                ev.MetadataJson = metadata;
            }
            return;
        }

        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = player.Id,
            EventType = "WOM_RANK_MISMATCH_REQUIRED",
            MetadataJson = metadata,
            Status = "OPEN",
            CreatedAt = now
        });
    }

    private static bool IsRankAligned(string expectedRank, string? actualRank) =>
        !string.IsNullOrWhiteSpace(actualRank) &&
        string.Equals(RankRules.NormalizeRankName(expectedRank), RankRules.NormalizeRankName(actualRank), StringComparison.OrdinalIgnoreCase);

    private static async Task EnsureOpenMergeSuggestedEventAsync(TrackerDbContext db, int playerId, string username, string handledBy)
    {
        var hasOpenMerge = await db.LifecycleEvents.AnyAsync(x =>
            x.PlayerId == playerId &&
            x.EventType == "MERGE_SUGGESTED" &&
            x.Status == "OPEN");
        if (hasOpenMerge) return;

        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = playerId,
            EventType = "MERGE_SUGGESTED",
            MetadataJson = JsonUtil.Serialize(new { Username = username, Source = "discord", HandledBy = handledBy }),
            Status = "OPEN",
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private static string FriendlyStepName(string stepName)
    {
        return stepName switch
        {
            "PostPendingPromotionCandidates" => "Checking pending promotions for Discord posts.",
            "PostTempleNameChangeNeededMessages" => "Checking possible Temple name-change setup.",
            "PostTempleMissingActionMessages" => "Checking Temple missing-player review messages.",
            "PostWomMissingActionMessages" => "Checking Wise Old Man missing-player review messages.",
            "PostWomOnlyActionMessages" => "Checking Wise Old Man only-player review messages.",
            "PostWomRankMismatchMessages" => "Checking Wise Old Man rank mismatch alerts.",
            "PostMergeActionMessages" => "Checking rename review cards.",
            "ProcessMessageActionUpdates" => "Applying Discord promotion button actions.",
            "ProcessTempleMissingActionUpdates" => "Applying Temple missing-player actions.",
            "ProcessMergeActionUpdates" => "Applying rename review actions.",
            "ProcessReviewCardRequeueRequests" => "Processing manual review-card repost requests.",
            "UpdatePetHiscoresMessages" => "Updating pet hiscore Discord messages.",
            "ReconcileCompletedMessageDeletes" => "Reconciling completed Discord cleanup.",
            "ReconcileOrphanTrackerCards" => "Reconciling orphaned Discord tracker cards.",
            "ProcessScheduledDeletes" => "Processing scheduled Discord message deletes.",
            _ => stepName
        };
    }

    private async Task PostPendingPromotionCandidates(CancellationToken ct)
    {
        if (_client is null) return;
        var channel = _client.GetChannel(_options.ChannelId) as IMessageChannel;
        if (channel is null) return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var wiseOldManClient = scope.ServiceProvider.GetRequiredService<IWiseOldManClient>();

        var pending = await db.PromotionCandidates
            .Where(x => x.Status == PromotionStatus.PENDING)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new
            {
                x.Id,
                x.PlayerId,
                x.OldRank,
                x.NewRank,
                x.Reason,
                PlayerStatus = x.Player.Status,
                PlayerName = x.Player.Username,
                CurrentRank = x.Player.CurrentRank,
                LastSynced = x.Player.LastSynced,
                StoredPetCount = x.Player.StoredPetCount,
                ManualPetOverride = x.Player.ManualPetOverride,
                Latest = x.Player.Snapshots
                    .OrderByDescending(s => s.Timestamp)
                    .Select(s => new
                    {
                        s.Ehb,
                        s.Ehp,
                        s.Collections
                    })
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        var openMergeEvents = await db.LifecycleEvents
            .Where(x => x.EventType == "MERGE_ACTION_REQUIRED" && x.Status == "OPEN")
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);

        foreach (var c in pending)
        {
            var postedEvents = await db.LifecycleEvents
                .Where(x => x.EventType == "PROMOTION_DISCORD_POSTED" && x.PlayerId == c.PlayerId)
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync(ct);
            postedEvents = postedEvents
                .Where(x => MetadataIntEquals(x.MetadataJson, "CandidateId", c.Id))
                .ToList();
            var postedEvent = postedEvents.FirstOrDefault(x => !IsPromotionPostedSupersededByMerge(x));

            // Revalidate candidate status at loop-time to avoid stale snapshots re-opening handled cards.
            var currentStatus = await GetCurrentPromotionCandidateStatusAsync(db, c.Id, ct);
            if (currentStatus != PromotionStatus.PENDING)
            {
                if (postedEvent is not null)
                {
                    await SchedulePromotionCandidateCleanupAsync(
                        db,
                        c.PlayerId,
                        c.Id,
                        postedEvent,
                        null,
                        null,
                        "candidate-no-longer-pending-loop",
                        ct);
                    logger.LogInformation(
                        "Skipping promotion card patch for candidate {CandidateId}; latest status is {CandidateStatus}.",
                        c.Id,
                        currentStatus?.ToString() ?? "MISSING");
                }
                continue;
            }

            if (RankRules.RankOrder(c.NewRank) <= RankRules.RankOrder(c.CurrentRank))
            {
                await DismissPromotionCandidateAlreadyCurrentRankAsync(
                    db,
                    c.PlayerId,
                    c.Id,
                    c.PlayerName,
                    c.CurrentRank,
                    c.NewRank,
                    "discord-promotion-post-guard",
                    ct);
                if (postedEvent is not null)
                {
                    await SchedulePromotionCandidateCleanupAsync(
                        db,
                        c.PlayerId,
                        c.Id,
                        postedEvent,
                        null,
                        null,
                        "candidate-already-current-rank",
                        ct);
                }
                continue;
            }

            var isMergePending = c.PlayerStatus == PlayerStatus.MERGE_SUGGESTED ||
                openMergeEvents.Any(x => IsMergeEventRelatedToPlayer(x, c.PlayerId, c.PlayerName));
            if (isMergePending)
            {
                if (postedEvent is not null)
                {
                    await SupersedePromotionCardForMergeAsync(
                        db,
                        postedEvent,
                        c.Id,
                        c.PlayerId,
                        "merge-review-pending",
                        null,
                        ct);
                    await db.SaveChangesAsync(ct);
                }
                continue;
            }

            var womRole = await wiseOldManClient.GetMemberRoleAsync(c.PlayerName, ct);
            var candidateType = RankRules.ClassifyPromotionCandidate(c.NewRank, womRole);
            var discordGuess = await GuessDiscordMemberForPlayerAsync(db, c.PlayerId, c.PlayerName, ct);
            var exactDiscordMatch = PromotionDiscordActionRules.FindUniqueExactMatch(discordGuess);

            var embed = BuildPromotionEmbed(
                c.PlayerName,
                c.OldRank,
                c.NewRank,
                string.IsNullOrWhiteSpace(womRole) ? "Unknown" : RankRules.NormalizeRankName(womRole),
                ToPromotionUpdateTargetLabel(candidateType),
                discordGuess,
                BuildStatsSummary(c.Latest?.Ehb, c.Latest?.Ehp, c.Latest?.Collections, c.ManualPetOverride ?? c.StoredPetCount),
                c.Reason,
                FormatSwedishTime(c.LastSynced));
            var renderFingerprint = ComputeRenderFingerprint(new
            {
                Type = "promotion-card",
                CandidateId = c.Id,
                PlayerId = c.PlayerId,
                c.PlayerName,
                c.OldRank,
                c.NewRank,
                WomRole = string.IsNullOrWhiteSpace(womRole) ? "Unknown" : RankRules.NormalizeRankName(womRole),
                CandidateType = candidateType.ToString(),
                DiscordGuess = FormatDiscordGuessForFingerprint(discordGuess),
                DiscordRankActionTargetUserId = exactDiscordMatch?.UserId,
                Stats = BuildStatsSummary(c.Latest?.Ehb, c.Latest?.Ehp, c.Latest?.Collections, c.ManualPetOverride ?? c.StoredPetCount),
                c.Reason,
                LastSynced = FormatSwedishTime(c.LastSynced)
            });

            var builder = new ComponentBuilder()
                .WithButton("Approve", $"promo:approve:{c.Id}", ButtonStyle.Success)
                .WithButton("Dismiss", $"promo:dismiss:{c.Id}", ButtonStyle.Danger)
                .WithButton("Mark Rename Suspect", $"promo:rename:{c.Id}", ButtonStyle.Secondary);

            if (postedEvent is not null)
            {
                var lookupKey = $"promotion:{c.Id}";
                if (IsLookupBackoffActive(lookupKey)) continue;

                var (lookupState, liveDiscordMessage, channelId, messageId) = await TryGetPostedUserMessageAsync(postedEvent, lookupKey);
                if (lookupState == PostedMessageLookupState.Unknown)
                {
                    SetLookupBackoff(lookupKey);
                    continue;
                }
                if (lookupState == PostedMessageLookupState.Malformed)
                {
                    postedEvent.Status = "DONE";
                    await db.SaveChangesAsync(ct);
                    continue;
                }
                if (lookupState == PostedMessageLookupState.Missing)
                {
                    await RecordMissingTrackedMessageEventAsync(
                        db,
                        c.PlayerId,
                        "promotion",
                        postedEvent.Id,
                        null,
                        channelId,
                        messageId,
                        "post-promotion",
                        ct);
                }

                if (lookupState == PostedMessageLookupState.Found && liveDiscordMessage is not null)
                {
                    var latestStatusBeforePatch = await GetCurrentPromotionCandidateStatusAsync(db, c.Id, ct);
                    var reenableRequested = IsPromotionReenableRequested(postedEvent);
                    var patchDecision = PromotionCardPatchGuard.Decide(
                        latestStatusBeforePatch ?? PromotionStatus.APPROVED,
                        HasPromotionActionButtons(liveDiscordMessage));
                    if (patchDecision == PromotionCardPatchDecision.SkipCandidateNotPending)
                    {
                        await SchedulePromotionCandidateCleanupAsync(
                            db,
                            c.PlayerId,
                            c.Id,
                            postedEvent,
                            channelId,
                            messageId,
                            "candidate-no-longer-pending-before-patch",
                            ct);
                        logger.LogInformation(
                            "Skipping promotion card patch for candidate {CandidateId}; latest status is {CandidateStatus} before patch.",
                            c.Id,
                            latestStatusBeforePatch?.ToString() ?? "MISSING");
                        continue;
                    }

                    if (patchDecision == PromotionCardPatchDecision.SkipMessageNotActionable && !reenableRequested)
                    {
                        logger.LogInformation(
                            "Skipping promotion card patch for candidate {CandidateId}; message {MessageId} has no actionable promotion buttons.",
                            c.Id,
                            liveDiscordMessage.Id);
                        continue;
                    }

                    if (!reenableRequested && ShouldSkipMessagePatch(liveDiscordMessage.Id, renderFingerprint))
                    {
                        continue;
                    }

                    await liveDiscordMessage.ModifyAsync(props =>
                    {
                        props.Embed = embed;
                        props.Components = builder.Build();
                    });
                    RecordMessagePatched(liveDiscordMessage.Id, renderFingerprint);
                    postedEvent.MetadataJson = BuildPromotionPostedMetadata(
                        c.Id,
                        liveDiscordMessage.Id,
                        liveDiscordMessage.Channel.Id,
                        renderFingerprint,
                        discordGuess);
                    await db.SaveChangesAsync(ct);
                    continue;
                }
            }

            var lease = await TryAcquirePostLeaseAsync(db, c.PlayerId, $"promotion:{c.Id}", ct);
            if (lease is null) continue;
            try
            {
                var stillPending = await db.PromotionCandidates.AnyAsync(x => x.Id == c.Id && x.Status == PromotionStatus.PENDING, ct);
                if (!stillPending) continue;

                var msg = await channel.SendMessageAsync(embed: embed, components: builder.Build());
                db.LifecycleEvents.Add(new LifecycleEvent
                {
                    PlayerId = c.PlayerId,
                    EventType = "PROMOTION_DISCORD_POSTED",
                    MetadataJson = BuildPromotionPostedMetadata(
                        c.Id,
                        msg.Id,
                        _options.ChannelId,
                        renderFingerprint,
                        discordGuess),
                    Status = "DONE",
                    CreatedAt = DateTimeOffset.UtcNow
                });
                RecordMessagePatched(msg.Id, renderFingerprint);
                await db.SaveChangesAsync(ct);
            }
            finally
            {
                lease.Status = "DONE";
                await db.SaveChangesAsync(ct);
            }
        }
    }

    private async Task OnReadyAsync()
    {
        if (_client is null) return;
        if (_options.GuildId == 0)
        {
            logger.LogWarning("DiscordBot:GuildId is not configured, slash command registration skipped.");
            return;
        }

        try
        {
            var socketGuild = _client.GetGuild(_options.GuildId);
            if (socketGuild is null)
            {
                logger.LogWarning("Could not resolve guild {GuildId} for slash command registration.", _options.GuildId);
                return;
            }

            var lookup = new SlashCommandBuilder()
                .WithName("lookup")
                .WithDescription("Lookup a specific player in SwedesClanTracker.")
                .AddOption("player", ApplicationCommandOptionType.String, "Player username", isRequired: true);
            var discordGuess = new SlashCommandBuilder()
                .WithName("discord-guess")
                .WithDescription("Guess the Discord member for a tracked player.")
                .AddOption("player", ApplicationCommandOptionType.String, "Player username", isRequired: true);
            var discordRankCheck = new SlashCommandBuilder()
                .WithName("discord-rank-check")
                .WithDescription("Check one exact-matched Discord member's rank role against the tracker.")
                .AddOption("player", ApplicationCommandOptionType.String, "Player username", isRequired: true);
            var discordRankAudit = new SlashCommandBuilder()
                .WithName("discord-rank-audit")
                .WithDescription("Audit exact Discord matches for rank-role mismatches and return a CSV.");
            var womSpecialRankAudit = new SlashCommandBuilder()
                .WithName("ungnomify")
                .WithDescription("List special WOM roles with each player's current tracker rank.");
            var discordRankSet = new SlashCommandBuilder()
                .WithName("discord-rank-set")
                .WithDescription("Replace a Discord member's clan rank role with the selected rank.")
                .AddOption("user", ApplicationCommandOptionType.User, "Discord member to update", isRequired: true)
                .AddOption(BuildDiscordRankRoleOption());
            var discordRankReviewPost = new SlashCommandBuilder()
                .WithName("discord-rank-review-post")
                .WithDescription("Post actionable Discord rank role review cards for current mismatches.");
            var history = new SlashCommandBuilder()
                .WithName("history")
                .WithDescription("Show recent tracker history.")
                .AddOption("rows", ApplicationCommandOptionType.Integer, "Number of recent rows to show (1-25).", isRequired: true)
                .AddOption("everything", ApplicationCommandOptionType.Boolean, "Include bot/system diagnostic events.", isRequired: false);
            var clear = new SlashCommandBuilder()
                .WithName("clear")
                .WithDescription("Clear user messages in the configured rank-up channel, keeping the quickguide.");
            var help = new SlashCommandBuilder()
                .WithName("help")
                .WithDescription("Shows all available commands and what they do.");
            var rankConditions = new SlashCommandBuilder()
                .WithName("rank-conditions")
                .WithDescription("Show the current tracker rank requirements.");
            var update = new SlashCommandBuilder()
                .WithName("update")
                .WithDescription("Prioritize a player for immediate stats update.")
                .AddOption("player", ApplicationCommandOptionType.String, "Player username", isRequired: true);
            var templeAdd = new SlashCommandBuilder()
                .WithName("temple-add")
                .WithDescription("Add one or more players to the TempleOSRS group.")
                .AddOption("players", ApplicationCommandOptionType.String, "Comma-separated player names", isRequired: true);
            var add = new SlashCommandBuilder()
                .WithName("add")
                .WithDescription("Add one or more players to both TempleOSRS and WiseOldMan.")
                .AddOption("players", ApplicationCommandOptionType.String, "Comma-separated player names", isRequired: true);
            var remove = new SlashCommandBuilder()
                .WithName("remove")
                .WithDescription("Remove one or more players from both TempleOSRS and WiseOldMan.")
                .AddOption("players", ApplicationCommandOptionType.String, "Comma-separated player names", isRequired: true);
            var templeRemove = new SlashCommandBuilder()
                .WithName("temple-remove")
                .WithDescription("Remove one or more players from the TempleOSRS group.")
                .AddOption("players", ApplicationCommandOptionType.String, "Comma-separated player names", isRequired: true);
            var womAdd = new SlashCommandBuilder()
                .WithName("wom-add")
                .WithDescription("Add one or more players to the WiseOldMan group.")
                .AddOption("players", ApplicationCommandOptionType.String, "Comma-separated player names", isRequired: true);
            var womRemove = new SlashCommandBuilder()
                .WithName("wom-remove")
                .WithDescription("Remove one or more players from the WiseOldMan group.")
                .AddOption("players", ApplicationCommandOptionType.String, "Comma-separated player names", isRequired: true);
            var womRoleUpdate = new SlashCommandBuilder()
                .WithName("wom-role-update")
                .WithDescription("Update a player's WiseOldMan group role.")
                .AddOption("player", ApplicationCommandOptionType.String, "Player username", isRequired: true)
                .AddOption(BuildWomRoleOption());
            var womCacheRefresh = new SlashCommandBuilder()
                .WithName("wom-cache-refresh")
                .WithDescription("Refresh the local WiseOldMan roster cache now.");
            var unignore = new SlashCommandBuilder()
                .WithName("unignore")
                .WithDescription("Remove ignore flags for WiseOldMan-only and WOM rank mismatch tracking.")
                .AddOption("player", ApplicationCommandOptionType.String, "Player username", isRequired: true);
            var showIgnored = new SlashCommandBuilder()
                .WithName("show-ignored")
                .WithDescription("Show all currently ignored players for WOM-only and WOM rank mismatch.");
            var requeueReviewCard = new SlashCommandBuilder()
                .WithName("requeue-review-card")
                .WithDescription("Force requeue of review Discord card(s) for a player.")
                .AddOption("player", ApplicationCommandOptionType.String, "Player username", isRequired: true);
            var setPets = new SlashCommandBuilder()
                .WithName("set-pets")
                .WithDescription("Manually set a player's pet count override.")
                .AddOption("player", ApplicationCommandOptionType.String, "Player username", isRequired: true)
                .AddOption("count", ApplicationCommandOptionType.Integer, "Manual pet count (0 or higher)", isRequired: true);

            await socketGuild.CreateApplicationCommandAsync(lookup.Build());
            await socketGuild.CreateApplicationCommandAsync(discordGuess.Build());
            await socketGuild.CreateApplicationCommandAsync(discordRankCheck.Build());
            await socketGuild.CreateApplicationCommandAsync(discordRankAudit.Build());
            await socketGuild.CreateApplicationCommandAsync(womSpecialRankAudit.Build());
            await socketGuild.CreateApplicationCommandAsync(discordRankSet.Build());
            await socketGuild.CreateApplicationCommandAsync(discordRankReviewPost.Build());
            await socketGuild.CreateApplicationCommandAsync(history.Build());
            await socketGuild.CreateApplicationCommandAsync(clear.Build());
            await socketGuild.CreateApplicationCommandAsync(help.Build());
            await socketGuild.CreateApplicationCommandAsync(rankConditions.Build());
            await socketGuild.CreateApplicationCommandAsync(update.Build());
            await socketGuild.CreateApplicationCommandAsync(add.Build());
            await socketGuild.CreateApplicationCommandAsync(remove.Build());
            await socketGuild.CreateApplicationCommandAsync(templeAdd.Build());
            await socketGuild.CreateApplicationCommandAsync(templeRemove.Build());
            await socketGuild.CreateApplicationCommandAsync(womAdd.Build());
            await socketGuild.CreateApplicationCommandAsync(womRemove.Build());
            await socketGuild.CreateApplicationCommandAsync(womRoleUpdate.Build());
            await socketGuild.CreateApplicationCommandAsync(womCacheRefresh.Build());
            await socketGuild.CreateApplicationCommandAsync(unignore.Build());
            await socketGuild.CreateApplicationCommandAsync(showIgnored.Build());
            await socketGuild.CreateApplicationCommandAsync(requeueReviewCard.Build());
            await socketGuild.CreateApplicationCommandAsync(setPets.Build());

            logger.LogInformation("Registered Discord slash commands in guild {GuildId}.", _options.GuildId);
            _ = Task.Run(() => WarmDiscordMemberCacheAsync(CancellationToken.None));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Slash command registration failed (may already exist).");
        }
    }

    private static SlashCommandOptionBuilder BuildWomRoleOption()
    {
        var option = new SlashCommandOptionBuilder()
            .WithName("rank")
            .WithDescription("WiseOldMan rank to set")
            .WithType(ApplicationCommandOptionType.String)
            .WithRequired(true);

        foreach (var role in WomRoleChoices)
        {
            option.AddChoice(role.Label, role.Value);
        }

        return option;
    }

    private static SlashCommandOptionBuilder BuildDiscordRankRoleOption()
    {
        var option = new SlashCommandOptionBuilder()
            .WithName("rank")
            .WithDescription("Discord rank role to assign")
            .WithType(ApplicationCommandOptionType.String)
            .WithRequired(true);

        foreach (var rank in DiscordRankNames)
        {
            option.AddChoice(rank, rank);
        }

        return option;
    }

    private async Task HandleButtonAsync(SocketMessageComponent component)
    {
        var sw = Stopwatch.StartNew();
        logger.LogInformation("Discord button start: {CustomId} by {User}", component.Data.CustomId, component.User.Username);
        try
        {
            var parts = component.Data.CustomId.Split(':');
            var isMergeManual = parts.Length >= 2 &&
                string.Equals(parts[0], "merge", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parts[1], "manual", StringComparison.OrdinalIgnoreCase);
            var isPromoButton = parts.Length == 3 &&
                string.Equals(parts[0], "promo", StringComparison.OrdinalIgnoreCase);

            // Promo actions update the originating card immediately via UpdateAsync.
            // Keep them non-deferred so we can acknowledge with UpdateMessage in-band.
            if (!component.HasResponded && !isMergeManual && !isPromoButton)
            {
                await component.DeferAsync();
            }

            if (IsAdminLockedButton(parts.FirstOrDefault()) && !HasDiscordAdminRole(component.User))
            {
                await DenyComponentAsync(component);
                return;
            }

            if (isMergeManual)
            {
                await ProcessButtonActionAsync(component, parts);
                return;
            }

            if (isPromoButton)
            {
                await ProcessButtonActionAsync(component, parts);
                logger.LogInformation("Discord button end: {CustomId} in {ElapsedMs}ms", component.Data.CustomId, sw.ElapsedMilliseconds);
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessButtonActionAsync(component, parts);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed handling Discord button.");
                    await RespondToComponentAsync(component, "Failed to handle action.", ephemeral: true);
                }
                finally
                {
                    logger.LogInformation("Discord button end: {CustomId} in {ElapsedMs}ms", component.Data.CustomId, sw.ElapsedMilliseconds);
                }
            });
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling Discord button.");
            await RespondToComponentAsync(component, "Failed to handle action.", ephemeral: true);
        }
        finally
        {
            if (sw.IsRunning)
            {
                logger.LogInformation("Discord button handler scheduled: {CustomId} in {ElapsedMs}ms", component.Data.CustomId, sw.ElapsedMilliseconds);
            }
        }
    }

    private async Task ProcessButtonActionAsync(SocketMessageComponent component, string[] parts)
    {
        if (parts.Length < 3)
        {
            await RespondToComponentAsync(component, "Unknown action.", ephemeral: true);
            return;
        }
        if (parts[0] == "missing")
        {
            if (parts.Length != 3)
            {
                await RespondToComponentAsync(component, "Unknown action.", ephemeral: true);
                return;
            }
            await HandleMissingTempleButtonAsync(component, parts);
            return;
        }
        if (parts[0] == "wommissing")
        {
            if (parts.Length != 3)
            {
                await RespondToComponentAsync(component, "Unknown action.", ephemeral: true);
                return;
            }
            await HandleMissingWomButtonAsync(component, parts);
            return;
        }
        if (parts[0] == "womrank")
        {
            if (parts.Length is not (3 or 4))
            {
                await RespondToComponentAsync(component, "Unknown action.", ephemeral: true);
                return;
            }
            await HandleWomRankMismatchButtonAsync(component, parts);
            return;
        }
        if (parts[0] == "womonly")
        {
            if (parts.Length != 3)
            {
                await RespondToComponentAsync(component, "Unknown action.", ephemeral: true);
                return;
            }
            await HandleWomOnlyButtonAsync(component, parts);
            return;
        }
        if (parts[0] == "templename")
        {
            if (parts.Length != 3)
            {
                await RespondToComponentAsync(component, "Unknown action.", ephemeral: true);
                return;
            }
            await HandleTempleNameChangeButtonAsync(component, parts);
            return;
        }
        if (parts[0] == "merge")
        {
            if (parts.Length != 3)
            {
                await RespondToComponentAsync(component, "Unknown action.", ephemeral: true);
                return;
            }
            await HandleMergeButtonAsync(component, parts);
            return;
        }
        if (parts[0] == "drankreview")
        {
            if (parts.Length < 2)
            {
                await RespondToComponentAsync(component, "Unknown action.", ephemeral: true);
                return;
            }
            await HandleDiscordRankReviewButtonAsync(component, parts);
            return;
        }
        if (parts[0] != "promo" || parts.Length != 3) return;
        var action = parts[1];
        if (!int.TryParse(parts[2], out var candidateId)) return;

        if (string.Equals(action, "discord-select", StringComparison.OrdinalIgnoreCase))
        {
            await HandlePromotionDiscordMemberPickerButtonAsync(component, candidateId);
            return;
        }

        if (string.Equals(action, "discord-user-select", StringComparison.OrdinalIgnoreCase))
        {
            await HandlePromotionDiscordUserPickerButtonAsync(component, candidateId);
            return;
        }

        if (string.Equals(action, "discord-rank", StringComparison.OrdinalIgnoreCase))
        {
            await HandlePromotionDiscordRankButtonAsync(component, candidateId);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var wiseOldManClient = scope.ServiceProvider.GetRequiredService<IWiseOldManClient>();
        var candidate = await db.PromotionCandidates.FirstOrDefaultAsync(x => x.Id == candidateId);
        if (candidate is null)
        {
            await RespondToComponentAsync(component, "Candidate not found.", ephemeral: true);
            return;
        }

        var player = await db.Players.FirstOrDefaultAsync(x => x.Id == candidate.PlayerId);
        if (player is null)
        {
            await RespondToComponentAsync(component, "Player not found.", ephemeral: true);
            return;
        }

        if (candidate.Status != PromotionStatus.PENDING)
        {
            var now = DateTimeOffset.UtcNow;
            ScheduleChannelMessageDelete(
                db,
                player.Id,
                component.Channel.Id,
                component.Message.Id,
                "PROMOTION_DISCORD_DELETE_SCHEDULED",
                new { CandidateId = candidate.Id, Reason = "promotion-already-handled" },
                now.AddMinutes(_discordDeleteDelayMinutes),
                now.AddMinutes(_discordDeleteHardCapMinutes),
                extendExistingSchedule: true);
            await db.SaveChangesAsync();
            var alreadyHandledAction = candidate.Status == PromotionStatus.APPROVED ? "approve" : "dismiss";
            var alreadyHandledText = $"Already handled ({candidate.Status})";
            await TryUpdatePromotionCardHandledAsync(component, alreadyHandledText, alreadyHandledAction);
            await RespondToComponentAsync(component, $"This promotion was already handled ({candidate.Status}).", ephemeral: true);
            return;
        }

        if ((action is "approve" or "dismiss") &&
            await FindOpenMergeForPromotionAsync(db, candidate.PlayerId, player.Username, CancellationToken.None) is not null)
        {
            var postedEvent = await db.LifecycleEvents
                .Where(x => x.EventType == "PROMOTION_DISCORD_POSTED")
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync();
            postedEvent = postedEvent
                .Where(x => MetadataIntEquals(x.MetadataJson, "CandidateId", candidate.Id))
                .ToList();
            var clickedPostedEvent = FindLifecycleEventByDiscordMessageId(postedEvent, component.Message.Id);
            if (clickedPostedEvent is not null)
            {
                await SupersedePromotionCardForMergeAsync(
                    db,
                    clickedPostedEvent,
                    candidate.Id,
                    candidate.PlayerId,
                    "merge-review-pending-action-guard",
                    null,
                    CancellationToken.None);
                await db.SaveChangesAsync();
            }

            await RespondToComponentAsync(
                component,
                "Promotion paused while the rename review is open. No promotion action was applied.",
                ephemeral: true);
            return;
        }

        var promotionPostedEvent = action == "approve"
            ? await FindPromotionPostedEventAsync(db, candidate.PlayerId, candidate.Id, component.Message.Id)
            : null;
        IReadOnlyList<DiscordMemberGuessMatch> promotionDiscordMatchOptions = promotionPostedEvent is null
            ? Array.Empty<DiscordMemberGuessMatch>()
            : await ResolvePromotionDiscordMatchOptionsAsync(
                db,
                promotionPostedEvent,
                player.Username,
                ct: CancellationToken.None);
        var promotionDiscordRankTargetUserId = PromotionDiscordActionRules
            .FindUniqueExactMatch(new DiscordMemberGuessResult(promotionDiscordMatchOptions))?
            .UserId;

        SwedesClanTracker.Core.WomRoleUpdateResult? promotionWomUpdate = null;
        if (action == "approve")
        {
            player.CurrentRank = candidate.NewRank;
            await CloseOpenLifecycleEventsAsync(db, player.Id, "WOM_RANK_MISMATCH_IGNORED");
            promotionWomUpdate = await ApplyWomRoleUpdateForApprovedPromotionAsync(
                db,
                wiseOldManClient,
                player,
                candidate,
                component.User.Username,
                component.User.Id,
                "discord",
                CancellationToken.None);
            candidate.Status = PromotionStatus.APPROVED;
        }
        else if (action == "dismiss")
        {
            candidate.Status = PromotionStatus.DISMISSED;
        }
        else if (action == "rename")
        {
            player.Status = PlayerStatus.MERGE_SUGGESTED;
            await CloseOpenLifecycleEventsAsync(db, player.Id, "NEW_PLAYER", "DISCORD_MARK_RENAME_SUSPECT");
            await EnsureOpenMergeSuggestedEventAsync(db, player.Id, player.Username, component.User.Username);
            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = player.Id,
                EventType = "DISCORD_MARK_RENAME_SUSPECT",
                MetadataJson = JsonUtil.Serialize(new { CandidateId = candidate.Id, User = component.User.Username, HandledBy = component.User.Username, HandledByDiscordUserId = component.User.Id }),
                Status = "DONE",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            await RespondToComponentAsync(component, "Unknown action.", ephemeral: true);
            return;
        }

        if (promotionPostedEvent is not null)
        {
            SetPromotionDiscordRankActionMetadata(
                promotionPostedEvent,
                available: action == "approve" && promotionDiscordMatchOptions.Count > 0,
                completed: false,
                success: null,
                details: null);
        }

        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = player.Id,
            EventType = "PROMOTION_DISCORD_ACTION_APPLIED",
            MetadataJson = JsonUtil.Serialize(new
            {
                CandidateId = candidate.Id,
                Action = action,
                HandledBy = component.User.Username,
                HandledByDiscordUserId = component.User.Id,
                WomUpdateSuccess = promotionWomUpdate?.Success,
                WomUpdateDetails = promotionWomUpdate?.Details,
                WomUpdatedRole = promotionWomUpdate?.UpdatedRole,
                DiscordRankActionTargetUserId = promotionDiscordRankTargetUserId,
                Source = "discord",
                ChannelId = component.Channel.Id,
                DiscordMessageId = component.Message.Id
            }),
            Status = "OPEN",
            CreatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
        ScheduleDelete(candidate.Id, player.Id);
        var handledText = $"Handled by {component.User.Username} ({action}) via discord";
        if (action == "approve" && promotionDiscordRankTargetUserId.HasValue)
        {
            handledText += ". Exact Discord match selected; use the button below to set the rank role.";
        }
        else if (action == "approve" && promotionDiscordMatchOptions.Count > 0)
        {
            handledText += ". Possible Discord matches found; choose a member before setting the rank role.";
        }
        else if (action == "approve")
        {
            handledText += ". No likely Discord match was found; use /discord-rank-set manually.";
        }

        var handledComponents = action != "approve"
            ? null
            : promotionDiscordRankTargetUserId.HasValue
                ? BuildPromotionDiscordRankActionComponents(candidate.Id, candidate.NewRank)
                : promotionDiscordMatchOptions.Count > 0
                    ? BuildPromotionDiscordMemberPickerButtonComponents(candidate.Id)
                    : null;
        await TryUpdatePromotionCardHandledAsync(component, handledText, action, handledComponents);
        // Background reconciliation still runs for web/admin-originated updates and retry/delete scheduling.
    }

    private async Task HandlePromotionDiscordMemberPickerButtonAsync(SocketMessageComponent component, int candidateId)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var candidate = await db.PromotionCandidates.FirstOrDefaultAsync(x => x.Id == candidateId);
        if (candidate is null)
        {
            await RespondToComponentAsync(component, "Promotion candidate not found.", ephemeral: true);
            return;
        }

        if (candidate.Status != PromotionStatus.APPROVED)
        {
            await RespondToComponentAsync(component, "This promotion is not approved, so its Discord rank action is no longer valid.", ephemeral: true);
            return;
        }

        var player = await db.Players.FirstOrDefaultAsync(x => x.Id == candidate.PlayerId);
        if (player is null)
        {
            await RespondToComponentAsync(component, "Player not found.", ephemeral: true);
            return;
        }

        var postedEvent = await FindPromotionPostedEventAsync(db, candidate.PlayerId, candidate.Id, component.Message.Id);
        if (postedEvent is null)
        {
            await RespondToComponentAsync(component, "The promotion card could not be matched to its tracked Discord message.", ephemeral: true);
            return;
        }

        var postedMetadata = ReadLifecycleMetadata(postedEvent.MetadataJson);
        if (string.Equals(PickLifecycleValue(postedMetadata, "DiscordRankActionCompleted"), "true", StringComparison.OrdinalIgnoreCase))
        {
            await RespondToComponentAsync(component, "The Discord rank action on this promotion card has already been handled.", ephemeral: true);
            return;
        }

        var matchOptions = await ResolvePromotionDiscordMatchOptionsAsync(
            db,
            postedEvent,
            player.Username,
            CancellationToken.None);
        if (matchOptions.Count == 0)
        {
            await db.SaveChangesAsync();
            await RespondToComponentAsync(component, "No Discord match candidates are available. Use /discord-rank-set manually.", ephemeral: true);
            return;
        }

        await db.SaveChangesAsync();
        var rank = RankRules.NormalizeRankName(candidate.NewRank);
        var picker = BuildPromotionDiscordMemberPickerMenu(candidate.Id, rank, matchOptions);
        var pickerText = $"Choose the Discord member whose rank should be set to {rank} for {player.Username}.";
        if (component.HasResponded)
        {
            await component.FollowupAsync(text: pickerText, components: picker, ephemeral: true);
        }
        else
        {
            await component.RespondAsync(text: pickerText, components: picker, ephemeral: true);
        }
    }

    private async Task HandlePromotionDiscordUserPickerButtonAsync(SocketMessageComponent component, int candidateId)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var candidate = await db.PromotionCandidates.FirstOrDefaultAsync(x => x.Id == candidateId);
        if (candidate is null)
        {
            await RespondToComponentAsync(component, "Promotion candidate not found.", ephemeral: true);
            return;
        }

        if (candidate.Status != PromotionStatus.APPROVED)
        {
            await RespondToComponentAsync(component, "This promotion is not approved, so its Discord rank action is no longer valid.", ephemeral: true);
            return;
        }

        var player = await db.Players.FirstOrDefaultAsync(x => x.Id == candidate.PlayerId);
        if (player is null)
        {
            await RespondToComponentAsync(component, "Player not found.", ephemeral: true);
            return;
        }

        var postedEvent = await FindPromotionPostedEventAsync(db, candidate.PlayerId, candidate.Id, component.Message.Id);
        if (postedEvent is null)
        {
            await RespondToComponentAsync(component, "The promotion card could not be matched to its tracked Discord message.", ephemeral: true);
            return;
        }

        var postedMetadata = ReadLifecycleMetadata(postedEvent.MetadataJson);
        if (string.Equals(PickLifecycleValue(postedMetadata, "DiscordRankActionCompleted"), "true", StringComparison.OrdinalIgnoreCase))
        {
            await RespondToComponentAsync(component, "The Discord rank action on this promotion card has already been handled.", ephemeral: true);
            return;
        }

        var rank = RankRules.NormalizeRankName(candidate.NewRank);
        var picker = BuildPromotionDiscordUserPickerMenu(candidate.Id, rank);
        var pickerText = $"Choose any guild member whose rank should be set to {rank} for {player.Username}.";
        if (component.HasResponded)
        {
            await component.FollowupAsync(text: pickerText, components: picker, ephemeral: true);
        }
        else
        {
            await component.RespondAsync(text: pickerText, components: picker, ephemeral: true);
        }
    }

    private async Task HandlePromotionDiscordMemberSelectAsync(SocketMessageComponent component, string customId)
    {
        var parts = customId.Split(':');
        if (parts.Length != 3 ||
            !string.Equals(parts[0], "promo", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(parts[1], "discord-member", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(parts[2], out var candidateId))
        {
            await RespondToComponentAsync(component, "Malformed Discord member selection.", ephemeral: true);
            return;
        }

        var selectedValue = component.Data.Values.FirstOrDefault();
        if (!ulong.TryParse(selectedValue, NumberStyles.None, CultureInfo.InvariantCulture, out var selectedUserId))
        {
            await RespondToComponentAsync(component, "No valid Discord member was selected.", ephemeral: true);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var candidate = await db.PromotionCandidates.FirstOrDefaultAsync(x => x.Id == candidateId);
        if (candidate is null)
        {
            await RespondToComponentAsync(component, "Promotion candidate not found.", ephemeral: true);
            return;
        }

        if (candidate.Status != PromotionStatus.APPROVED)
        {
            await RespondToComponentAsync(component, "This promotion is not approved, so its Discord rank action is no longer valid.", ephemeral: true);
            return;
        }

        var player = await db.Players.FirstOrDefaultAsync(x => x.Id == candidate.PlayerId);
        if (player is null)
        {
            await RespondToComponentAsync(component, "Player not found.", ephemeral: true);
            return;
        }

        var postedEvent = await FindPromotionPostedEventAsync(db, candidate.PlayerId, candidate.Id, component.Message.Id);
        if (postedEvent is null)
        {
            await RespondToComponentAsync(component, "The promotion card could not be matched to its tracked Discord message.", ephemeral: true);
            return;
        }

        var postedMetadata = ReadLifecycleMetadata(postedEvent.MetadataJson);
        if (string.Equals(PickLifecycleValue(postedMetadata, "DiscordRankActionCompleted"), "true", StringComparison.OrdinalIgnoreCase))
        {
            await RespondToComponentAsync(component, "The Discord rank action on this promotion card has already been handled.", ephemeral: true);
            return;
        }

        var matchOptions = await ResolvePromotionDiscordMatchOptionsAsync(
            db,
            postedEvent,
            player.Username,
            CancellationToken.None);
        var selectedMatch = matchOptions.FirstOrDefault(x => x.UserId == selectedUserId);
        if (selectedMatch is null)
        {
            await RespondToComponentAsync(component, "That Discord member is not one of the tracked match candidates.", ephemeral: true);
            return;
        }

        SetPromotionDiscordRankActionSelectionMetadata(
            postedEvent,
            selectedMatch.UserId,
            selectedMatch.DisplayLabel,
            isCustomSelection: false);
        SetPromotionDiscordRankActionMetadata(
            postedEvent,
            available: true,
            completed: false,
            success: null,
            details: null);
        await db.SaveChangesAsync();

        var rank = RankRules.NormalizeRankName(candidate.NewRank);
        var handledText = $"Discord member selected: {selectedMatch.DisplayLabel}. Use the button below to set the {rank} role.";
        var publicMessageId = ExtractUlong(postedEvent.MetadataJson, "DiscordMessageId");
        if (publicMessageId is ulong resolvedPublicMessageId)
        {
            await TryUpdatePromotionCardHandledAsync(
                component.Channel,
                resolvedPublicMessageId,
                handledText,
                "approve",
                BuildPromotionDiscordRankActionComponents(candidate.Id, candidate.NewRank, allowAlternateSelection: true));
        }
        await RespondToComponentAsync(
            component,
            $"Selected {selectedMatch.DisplayLabel}. Confirm the {rank} role update with the button on the promotion card.",
            ephemeral: true);
    }

    private async Task HandlePromotionDiscordUserSelectAsync(SocketMessageComponent component, string customId)
    {
        var parts = customId.Split(':');
        if (parts.Length != 3 ||
            !string.Equals(parts[0], "promo", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(parts[1], "discord-user", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(parts[2], out var candidateId))
        {
            await RespondToComponentAsync(component, "Malformed Discord user selection.", ephemeral: true);
            return;
        }

        var selectedValue = component.Data.Values.FirstOrDefault();
        if (!ulong.TryParse(selectedValue, NumberStyles.None, CultureInfo.InvariantCulture, out var selectedUserId))
        {
            await RespondToComponentAsync(component, "No valid Discord user was selected.", ephemeral: true);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var candidate = await db.PromotionCandidates.FirstOrDefaultAsync(x => x.Id == candidateId);
        if (candidate is null)
        {
            await RespondToComponentAsync(component, "Promotion candidate not found.", ephemeral: true);
            return;
        }

        if (candidate.Status != PromotionStatus.APPROVED)
        {
            await RespondToComponentAsync(component, "This promotion is not approved, so its Discord rank action is no longer valid.", ephemeral: true);
            return;
        }

        var player = await db.Players.FirstOrDefaultAsync(x => x.Id == candidate.PlayerId);
        if (player is null)
        {
            await RespondToComponentAsync(component, "Player not found.", ephemeral: true);
            return;
        }

        var postedEvent = await FindPromotionPostedEventAsync(db, candidate.PlayerId, candidate.Id, component.Message.Id);
        if (postedEvent is null)
        {
            await RespondToComponentAsync(component, "The promotion card could not be matched to its tracked Discord message.", ephemeral: true);
            return;
        }

        var postedMetadata = ReadLifecycleMetadata(postedEvent.MetadataJson);
        if (string.Equals(PickLifecycleValue(postedMetadata, "DiscordRankActionCompleted"), "true", StringComparison.OrdinalIgnoreCase))
        {
            await RespondToComponentAsync(component, "The Discord rank action on this promotion card has already been handled.", ephemeral: true);
            return;
        }

        var guild = _client?.GetGuild(_options.GuildId);
        if (guild is null)
        {
            await RespondToComponentAsync(component, "Discord guild is not available in the bot cache.", ephemeral: true);
            return;
        }

        var guildUser = await ResolveGuildUserAsync(guild, selectedUserId, CancellationToken.None);
        if (guildUser is null)
        {
            await RespondToComponentAsync(component, "The selected Discord user is not a member of this guild.", ephemeral: true);
            return;
        }

        var selectedLabel = FormatPromotionDiscordUserLabel(guildUser);
        SetPromotionDiscordRankActionSelectionMetadata(
            postedEvent,
            selectedUserId,
            selectedLabel,
            isCustomSelection: true);
        SetPromotionDiscordRankActionMetadata(
            postedEvent,
            available: true,
            completed: false,
            success: null,
            details: null);
        await db.SaveChangesAsync();

        var rank = RankRules.NormalizeRankName(candidate.NewRank);
        var handledText = $"Discord member selected: {selectedLabel}. Use the button below to set the {rank} role.";
        var publicMessageId = ExtractUlong(postedEvent.MetadataJson, "DiscordMessageId");
        if (publicMessageId is ulong resolvedPublicMessageId)
        {
            await TryUpdatePromotionCardHandledAsync(
                component.Channel,
                resolvedPublicMessageId,
                handledText,
                "approve",
                BuildPromotionDiscordRankActionComponents(candidate.Id, candidate.NewRank, allowAlternateSelection: true));
        }

        await RespondToComponentAsync(
            component,
            $"Selected {selectedLabel}. Confirm the {rank} role update with the button on the promotion card.",
            ephemeral: true);
    }

    private async Task HandlePromotionDiscordRankButtonAsync(SocketMessageComponent component, int candidateId)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var candidate = await db.PromotionCandidates.FirstOrDefaultAsync(x => x.Id == candidateId);
        if (candidate is null)
        {
            await RespondToComponentAsync(component, "Promotion candidate not found.", ephemeral: true);
            return;
        }

        if (candidate.Status != PromotionStatus.APPROVED)
        {
            await RespondToComponentAsync(component, "This promotion is not approved, so its Discord rank action is no longer valid.", ephemeral: true);
            return;
        }

        var player = await db.Players.FirstOrDefaultAsync(x => x.Id == candidate.PlayerId);
        if (player is null)
        {
            await RespondToComponentAsync(component, "Player not found.", ephemeral: true);
            return;
        }

        var postedEvent = await FindPromotionPostedEventAsync(db, candidate.PlayerId, candidate.Id, component.Message.Id);
        if (postedEvent is null)
        {
            await RespondToComponentAsync(component, "The promotion card could not be matched to its tracked Discord message.", ephemeral: true);
            return;
        }

        var postedMetadata = ReadLifecycleMetadata(postedEvent.MetadataJson);
        if (string.Equals(PickLifecycleValue(postedMetadata, "DiscordRankActionCompleted"), "true", StringComparison.OrdinalIgnoreCase))
        {
            await RespondToComponentAsync(component, "The Discord rank action on this promotion card has already been handled.", ephemeral: true);
            return;
        }

        var targetUserId = await ResolvePromotionDiscordRankTargetUserIdAsync(
            db,
            postedEvent,
            player.Username,
            ct: CancellationToken.None);
        if (!targetUserId.HasValue)
        {
            await TryUpdatePromotionCardHandledAsync(
                component,
                "Discord rank was not changed. The exact Discord match is no longer available; use /discord-rank-set manually.",
                "approve");
            await RespondToComponentAsync(component, "No unique exact Discord match is available for the automatic action. Use /discord-rank-set manually.", ephemeral: true);
            return;
        }

        try
        {
            var guild = _client?.GetGuild(_options.GuildId);
            if (guild is null)
            {
                throw new InvalidOperationException("Discord guild is not available in the bot cache.");
            }

            var rankRoles = BuildDiscordRankRoleConfiguration(guild);
            var normalizedNewRank = RankRules.NormalizeRankName(candidate.NewRank);
            if (rankRoles.MissingRanks.Contains(normalizedNewRank, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"No configured Discord role ID exists for `{EscapeInlineCode(normalizedNewRank)}`.");
            }

            var selectedRole = rankRoles.RolesByRank[normalizedNewRank];
            if (guild.GetRole(selectedRole.RoleId) is null)
            {
                throw new InvalidOperationException($"The configured Discord role for `{EscapeInlineCode(normalizedNewRank)}` was not found in this guild.");
            }

            var guildUser = await ResolveGuildUserAsync(guild, targetUserId.Value, CancellationToken.None);
            if (guildUser is null)
            {
                throw new InvalidOperationException("The selected Discord member could not be resolved in this guild.");
            }

            var update = await ApplyPromotionDiscordRankRoleAsync(
                guildUser,
                candidate.OldRank,
                candidate.NewRank,
                selectedRole,
                rankRoles);
            var roleName = string.IsNullOrWhiteSpace(selectedRole.RoleName)
                ? normalizedNewRank
                : selectedRole.RoleName;
            var removedText = update.RemovedRoles.Count == 0
                ? "none"
                : string.Join(", ", update.RemovedRoles.Select(x => x.RoleName));
            var details = update.Success
                ? update.AlreadyCorrect
                    ? $"{player.Username} already has the {roleName} role. No role changes were needed."
                    : $"Set {player.Username} to the {roleName} role. Removed: {removedText}."
                : $"No roles were changed for {player.Username}. {update.Details} Use /discord-rank-set manually.";

            SetPromotionDiscordRankActionMetadata(
                postedEvent,
                available: false,
                completed: true,
                success: update.Success,
                details: details);
            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = player.Id,
                EventType = "PROMOTION_DISCORD_RANK_ACTION_APPLIED",
                MetadataJson = JsonUtil.Serialize(new
                {
                    CandidateId = candidate.Id,
                    DiscordUserId = targetUserId.Value,
                    RequestedRank = candidate.NewRank,
                    PreviousRank = candidate.OldRank,
                    Success = update.Success,
                    AlreadyCorrect = update.AlreadyCorrect,
                    Details = update.Details,
                    RemovedRoles = update.RemovedRoles.Select(x => x.Rank).ToArray(),
                    HandledBy = component.User.Username,
                    HandledByDiscordUserId = component.User.Id,
                    Source = "discord",
                    ChannelId = component.Channel.Id,
                    DiscordMessageId = component.Message.Id
                }),
                Status = "DONE",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();

            await TryUpdatePromotionCardHandledAsync(component, details, "approve");
            await RespondToComponentAsync(
                component,
                update.Success
                    ? $"Discord rank action completed: {details}"
                    : $"Discord rank action stopped safely: {details}",
                ephemeral: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed applying automatic Discord rank action for promotion candidate {CandidateId}.", candidateId);
            var details = $"Discord rank update for {player.Username} to {RankRules.NormalizeRankName(candidate.NewRank)} did not complete. Verify the member's rank roles before retrying, then use /discord-rank-set manually if needed. {ex.Message}";
            SetPromotionDiscordRankActionMetadata(
                postedEvent,
                available: false,
                completed: true,
                success: false,
                details: details);
            await db.SaveChangesAsync();
            await TryUpdatePromotionCardHandledAsync(component, details, "approve");
            await RespondToComponentAsync(component, details, ephemeral: true);
        }
    }

    private sealed record PromotionDiscordRankRoleUpdateResult(
        bool Success,
        bool AlreadyCorrect,
        string Details,
        IReadOnlyList<DiscordRankRole> RemovedRoles);

    private static async Task<PromotionDiscordRankRoleUpdateResult> ApplyPromotionDiscordRankRoleAsync(
        IGuildUser guildUser,
        string oldRank,
        string newRank,
        DiscordRankRole selectedRole,
        DiscordRankRoleConfiguration rankRoles)
    {
        var existingRankRoles = GetDiscordRankRolesForUser(guildUser, rankRoles);
        var decision = PromotionDiscordActionRules.EvaluateRankRoleState(
            oldRank,
            newRank,
            existingRankRoles.Select(x => x.Rank));
        if (!decision.CanApply)
        {
            return new PromotionDiscordRankRoleUpdateResult(false, false, decision.Details, []);
        }

        if (decision.Action == DiscordPromotionRankRoleAction.AlreadyCorrect)
        {
            return new PromotionDiscordRankRoleUpdateResult(true, true, decision.Details, []);
        }

        var userRoleIds = guildUser is SocketGuildUser socketUser
            ? socketUser.Roles.Select(x => x.Id).ToHashSet()
            : guildUser.RoleIds.ToHashSet();
        if (!userRoleIds.Contains(selectedRole.RoleId))
        {
            await guildUser.AddRoleAsync(selectedRole.RoleId);
        }

        var removedRoles = new List<DiscordRankRole>();
        if (decision.Action == DiscordPromotionRankRoleAction.Replace)
        {
            var previousRole = existingRankRoles.Single();
            await guildUser.RemoveRoleAsync(previousRole.RoleId);
            removedRoles.Add(previousRole);
        }

        return new PromotionDiscordRankRoleUpdateResult(true, false, decision.Details, removedRoles);
    }

    private static MessageComponent BuildPromotionDiscordRankActionComponents(int candidateId, string newRank) =>
        BuildPromotionDiscordRankActionComponents(candidateId, newRank, allowAlternateSelection: false);

    private static MessageComponent BuildPromotionDiscordRankActionComponents(
        int candidateId,
        string newRank,
        bool allowAlternateSelection)
    {
        var builder = new ComponentBuilder()
            .WithButton(
                $"Set Discord rank: {RankRules.NormalizeRankName(newRank)}",
                $"promo:discord-rank:{candidateId}",
                ButtonStyle.Primary);

        if (allowAlternateSelection)
        {
            builder.WithButton(
                "Choose suggested match",
                $"promo:discord-select:{candidateId}",
                ButtonStyle.Secondary);
            builder.WithButton(
                "Choose any Discord user",
                $"promo:discord-user-select:{candidateId}",
                ButtonStyle.Secondary);
        }

        return builder.Build();
    }

    private static MessageComponent BuildPromotionDiscordMemberPickerButtonComponents(int candidateId) =>
        new ComponentBuilder()
            .WithButton(
                "Choose suggested Discord user",
                $"promo:discord-select:{candidateId}",
                ButtonStyle.Secondary)
            .WithButton(
                "Choose any Discord user",
                $"promo:discord-user-select:{candidateId}",
                ButtonStyle.Secondary)
            .Build();

    private static MessageComponent BuildPromotionDiscordMemberPickerMenu(
        int candidateId,
        string newRank,
        IReadOnlyList<DiscordMemberGuessMatch> matches)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId($"promo:discord-member:{candidateId}")
            .WithPlaceholder($"Choose Discord user for {RankRules.NormalizeRankName(newRank)}")
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var match in matches.Take(25).DistinctBy(x => x.UserId))
        {
            var label = ShortenHistoryText(
                $"{match.DisplayLabel} ({match.Strength.ToString().ToLowerInvariant()}, {match.Score}%)",
                100);
            var description = ShortenHistoryText(
                $"{match.MatchedField}: {match.MatchedValue}",
                100);
            menu.AddOption(
                label,
                match.UserId.ToString(CultureInfo.InvariantCulture),
                description);
        }

        return new ComponentBuilder()
            .WithSelectMenu(menu)
            .WithButton(
                "Choose a different Discord user",
                $"promo:discord-user-select:{candidateId}",
                ButtonStyle.Secondary)
            .Build();
    }

    private static MessageComponent BuildPromotionDiscordUserPickerMenu(int candidateId, string newRank) =>
        new ComponentBuilder()
            .WithSelectMenu(
                new SelectMenuBuilder()
                    .WithCustomId($"promo:discord-user:{candidateId}")
                    .WithType(ComponentType.UserSelect)
                    .WithPlaceholder($"Choose any Discord user for {RankRules.NormalizeRankName(newRank)}")
                    .WithMinValues(1)
                    .WithMaxValues(1))
            .Build();

    private static string FormatPromotionDiscordUserLabel(IGuildUser user)
    {
        var displayName = string.IsNullOrWhiteSpace(user.DisplayName)
            ? user.Username
            : user.DisplayName;
        return string.Equals(displayName, user.Username, StringComparison.OrdinalIgnoreCase)
            ? displayName
            : $"{displayName} (@{user.Username})";
    }

    private async Task<LifecycleEvent?> FindPromotionPostedEventAsync(
        TrackerDbContext db,
        int playerId,
        int candidateId,
        ulong messageId)
    {
        var postedEvents = await db.LifecycleEvents
            .Where(x => x.EventType == "PROMOTION_DISCORD_POSTED" && x.PlayerId == playerId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();

        return postedEvents.FirstOrDefault(x =>
                   MetadataIntEquals(x.MetadataJson, "CandidateId", candidateId) &&
                   ExtractUlong(x.MetadataJson, "DiscordMessageId") == messageId) ??
               postedEvents.FirstOrDefault(x => MetadataIntEquals(x.MetadataJson, "CandidateId", candidateId));
    }

    private async Task<ulong?> ResolvePromotionDiscordRankTargetUserIdAsync(
        TrackerDbContext db,
        LifecycleEvent postedEvent,
        string playerName,
        CancellationToken ct)
    {
        var metadata = ReadLifecycleMetadata(postedEvent.MetadataJson);

        var selectedUserIdText = PickLifecycleValue(metadata, "DiscordRankActionSelectedUserId");
        if (ulong.TryParse(selectedUserIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var selectedUserId))
        {
            var isCustomSelection = bool.TryParse(
                PickLifecycleValue(metadata, "DiscordRankActionSelectionIsCustom"),
                out var parsedCustomSelection) && parsedCustomSelection;
            if (isCustomSelection)
            {
                return selectedUserId;
            }

            var matchOptions = await ResolvePromotionDiscordMatchOptionsAsync(db, postedEvent, playerName, ct);
            return matchOptions.Any(x => x.UserId == selectedUserId) ? selectedUserId : null;
        }

        if (metadata.ContainsKey("DiscordMatchIsUniqueExact"))
        {
            var isUniqueExact = bool.TryParse(
                PickLifecycleValue(metadata, "DiscordMatchIsUniqueExact"),
                out var parsed) && parsed;
            return isUniqueExact ? ExtractUlong(postedEvent.MetadataJson, "DiscordMatchUserId") : null;
        }

        var guess = await GuessDiscordMemberForPlayerAsync(db, postedEvent.PlayerId, playerName, ct);
        SetPromotionDiscordMatchMetadata(postedEvent, guess);
        var exactMatch = PromotionDiscordActionRules.FindUniqueExactMatch(guess);
        if (exactMatch is null) return null;

        return exactMatch.UserId;
    }

    private async Task<IReadOnlyList<DiscordMemberGuessMatch>> ResolvePromotionDiscordMatchOptionsAsync(
        TrackerDbContext db,
        LifecycleEvent postedEvent,
        string playerName,
        CancellationToken ct)
    {
        var metadata = ReadLifecycleMetadata(postedEvent.MetadataJson);
        if (metadata.ContainsKey("DiscordMatchCandidates"))
        {
            return ReadPromotionDiscordMatchCandidates(postedEvent.MetadataJson);
        }

        var guess = await GuessDiscordMemberForPlayerAsync(db, postedEvent.PlayerId, playerName, ct);
        SetPromotionDiscordMatchMetadata(postedEvent, guess);
        return guess.Matches;
    }

    private static string BuildPromotionPostedMetadata(
        int candidateId,
        ulong messageId,
        ulong channelId,
        string renderFingerprint,
        DiscordMemberGuessResult discordGuess)
    {
        var exactDiscordMatch = PromotionDiscordActionRules.FindUniqueExactMatch(discordGuess);
        return JsonUtil.Serialize(new
        {
            CandidateId = candidateId,
            DiscordMessageId = messageId,
            ChannelId = channelId,
            RenderFingerprint = renderFingerprint,
            DiscordMatchIsUniqueExact = exactDiscordMatch is not null,
            DiscordMatchUserId = exactDiscordMatch?.UserId,
            DiscordMatchStrength = exactDiscordMatch?.Strength.ToString(),
            DiscordMatchCandidates = discordGuess.Matches.Select(BuildPromotionDiscordMatchCandidateMetadata).ToArray(),
            DiscordRankActionAvailable = false,
            DiscordRankActionCompleted = false
        });
    }

    private static void SetPromotionDiscordMatchMetadata(
        LifecycleEvent postedEvent,
        DiscordMemberGuessResult discordGuess)
    {
        var exactDiscordMatch = PromotionDiscordActionRules.FindUniqueExactMatch(discordGuess);
        var metadata = ParseLifecycleMetadataObject(postedEvent.MetadataJson);
        metadata["DiscordMatchIsUniqueExact"] = exactDiscordMatch is not null;
        metadata["DiscordMatchUserId"] = exactDiscordMatch is null
            ? null
            : JsonValue.Create(exactDiscordMatch.UserId);
        metadata["DiscordMatchStrength"] = exactDiscordMatch?.Strength.ToString();
        metadata["DiscordMatchCandidates"] = JsonSerializer.SerializeToNode(
            discordGuess.Matches.Select(BuildPromotionDiscordMatchCandidateMetadata).ToArray());
        postedEvent.MetadataJson = metadata.ToJsonString();
    }

    private static object BuildPromotionDiscordMatchCandidateMetadata(DiscordMemberGuessMatch match) =>
        new
        {
            match.UserId,
            match.DisplayLabel,
            match.Mention,
            match.Score,
            Strength = match.Strength.ToString(),
            match.MatchedField,
            match.MatchedValue,
            match.PlayerAlias,
            match.FromDiscordSearch
        };

    private static IReadOnlyList<DiscordMemberGuessMatch> ReadPromotionDiscordMatchCandidates(string metadataJson)
    {
        var matches = new List<DiscordMemberGuessMatch>();
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            if (!document.RootElement.TryGetProperty("DiscordMatchCandidates", out var candidates) ||
                candidates.ValueKind != JsonValueKind.Array)
            {
                return matches;
            }

            foreach (var candidate in candidates.EnumerateArray())
            {
                if (candidate.ValueKind != JsonValueKind.Object ||
                    !TryReadUlong(candidate, "UserId", out var userId))
                {
                    continue;
                }

                var displayLabel = ReadStringProperty(candidate, "DisplayLabel") ?? $"Discord user {userId}";
                var mention = ReadStringProperty(candidate, "Mention") ?? $"<@{userId}>";
                var score = candidate.TryGetProperty("Score", out var scoreProperty) &&
                            scoreProperty.TryGetInt32(out var parsedScore)
                    ? parsedScore
                    : 0;
                var strength = Enum.TryParse<DiscordMemberMatchStrength>(
                    ReadStringProperty(candidate, "Strength"),
                    ignoreCase: true,
                    out var parsedStrength)
                    ? parsedStrength
                    : DiscordMemberMatchStrength.Possible;
                var matchedField = ReadStringProperty(candidate, "MatchedField") ?? "match";
                var matchedValue = ReadStringProperty(candidate, "MatchedValue") ?? displayLabel;
                var playerAlias = ReadStringProperty(candidate, "PlayerAlias") ?? "";
                var fromDiscordSearch = candidate.TryGetProperty("FromDiscordSearch", out var searchProperty) &&
                                        searchProperty.ValueKind == JsonValueKind.True;

                matches.Add(new DiscordMemberGuessMatch(
                    userId,
                    displayLabel,
                    mention,
                    score,
                    strength,
                    matchedField,
                    matchedValue,
                    playerAlias,
                    fromDiscordSearch));
            }
        }
        catch
        {
            // Best-effort parsing for lifecycle metadata from older or malformed cards.
        }

        return matches;
    }

    private static void SetPromotionDiscordRankActionSelectionMetadata(
        LifecycleEvent postedEvent,
        ulong selectedUserId,
        string selectedLabel,
        bool isCustomSelection)
    {
        var metadata = ParseLifecycleMetadataObject(postedEvent.MetadataJson);
        metadata["DiscordRankActionSelectedUserId"] = selectedUserId;
        metadata["DiscordRankActionSelectedLabel"] = selectedLabel;
        metadata["DiscordRankActionSelectionIsCustom"] = isCustomSelection;
        postedEvent.MetadataJson = metadata.ToJsonString();
    }

    private static void SetPromotionDiscordRankActionMetadata(
        LifecycleEvent postedEvent,
        bool available,
        bool completed,
        bool? success,
        string? details)
    {
        var metadata = ParseLifecycleMetadataObject(postedEvent.MetadataJson);
        metadata["DiscordRankActionAvailable"] = available;
        metadata["DiscordRankActionCompleted"] = completed;
        if (success.HasValue)
        {
            metadata["DiscordRankActionSuccess"] = success.Value;
        }
        else
        {
            metadata.Remove("DiscordRankActionSuccess");
        }

        if (details is null)
        {
            metadata.Remove("DiscordRankActionDetails");
        }
        else
        {
            metadata["DiscordRankActionDetails"] = details;
        }

        postedEvent.MetadataJson = metadata.ToJsonString();
    }

    private static JsonObject ParseLifecycleMetadataObject(string metadataJson)
    {
        try
        {
            return JsonNode.Parse(metadataJson) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private async Task UpdateComponentMessageAsync(SocketMessageComponent component, Embed embed)
    {
        if (component.Message is IUserMessage userMessage)
        {
            await userMessage.ModifyAsync(props =>
            {
                props.Components = new ComponentBuilder().Build();
                props.Embed = embed;
            });
        }
    }

    private async Task TryUpdatePromotionCardHandledAsync(
        SocketMessageComponent component,
        string handledText,
        string action,
        MessageComponent? components = null)
    {
        var embed = BuildHandledEmbed(component.Message.Embeds.FirstOrDefault(), handledText, action);
        var resolvedComponents = components ?? new ComponentBuilder().Build();
        try
        {
            if (!component.HasResponded)
            {
                await component.UpdateAsync(props =>
                {
                    props.Components = resolvedComponents;
                    props.Embed = embed;
                });
                return;
            }

            if (component.Message is IUserMessage userMessage)
            {
                await userMessage.ModifyAsync(props =>
                {
                    props.Components = resolvedComponents;
                    props.Embed = embed;
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to apply immediate promotion card handled update for message {MessageId}.", component.Message.Id);
        }
    }

    private async Task TryUpdatePromotionCardHandledAsync(
        IMessageChannel channel,
        ulong messageId,
        string handledText,
        string action,
        MessageComponent? components = null)
    {
        var resolvedComponents = components ?? new ComponentBuilder().Build();
        try
        {
            var message = await channel.GetMessageAsync(messageId);
            if (message is not IUserMessage userMessage) return;

            await userMessage.ModifyAsync(props =>
            {
                props.Components = resolvedComponents;
                props.Embed = BuildHandledEmbed(userMessage.Embeds.FirstOrDefault(), handledText, action);
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update promotion card message {MessageId} after Discord member selection.", messageId);
        }
    }

    private async Task HandleMissingTempleButtonAsync(SocketMessageComponent component, string[] parts)
    {
        var action = parts[1];
        if (!int.TryParse(parts[2], out var playerId)) return;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var player = await db.Players.FirstOrDefaultAsync(x => x.Id == playerId);
        if (player is null)
        {
            await RespondToComponentAsync(component, "Player not found.", ephemeral: true);
            return;
        }
        var renameDetection = await TryPrepareRenameReviewBeforeSensitiveActionAsync(
            db,
            expectedPreviousPlayerId: player.Id,
            expectedNewUsername: null,
            expectedEitherUsername: null,
            ct: CancellationToken.None);
        if (renameDetection is not null)
        {
            await BlockSensitiveActionForRenameAsync(db, component, renameDetection, player.Id, CancellationToken.None);
            return;
        }
        if (action == "add")
        {
            var templeOk = await AddPlayerToTempleAsync(player.Username);
            var womOk = await AddPlayerToWomAsync(player.Username);
            if (!templeOk)
            {
                await RespondToComponentAsync(component, "Failed to add player to Temple/WiseOldMan.", ephemeral: true);
                return;
            }

            if (!womOk)
            {
                var womGroupId = configuration.GetValue<int?>("WiseOldMan:GroupId") ?? 0;
                var alreadyInWom = womGroupId > 0 && await IsPlayerInWiseOldManGroupAsync(player.Username, womGroupId);
                if (!alreadyInWom)
                {
                    await RespondToComponentAsync(component, "Temple add succeeded, but WiseOldMan add failed.", ephemeral: true);
                    return;
                }
            }

            player.Status = PlayerStatus.ACTIVE;
            await CloseOpenLifecycleEventsAsync(db, player.Id,
                "NEW_PLAYER",
                "MERGE_SUGGESTED",
                "DISCORD_MARK_RENAME_SUSPECT",
                "MISSING_IN_ROSTER",
                "TEMPLE_MISSING_ACTION_REQUIRED",
                "WOM_MISSING_ACTION_REQUIRED");
        }
        else if (action == "remove")
        {
            var womRemoved = await RemovePlayerFromWomAsync(player.Username);
            if (!womRemoved)
            {
                var womGroupId = configuration.GetValue<int?>("WiseOldMan:GroupId") ?? 0;
                var stillInWom = womGroupId > 0 && await IsPlayerInWiseOldManGroupAsync(player.Username, womGroupId);
                if (stillInWom)
                {
                    await RespondToComponentAsync(component, "Failed to remove player from WiseOldMan.", ephemeral: true);
                    return;
                }
            }
            player.Status = PlayerStatus.REMOVED_CONFIRMED;
            await CloseOpenLifecycleEventsAsync(db, player.Id,
                "NEW_PLAYER",
                "MERGE_SUGGESTED",
                "MERGE_ACTION_REQUIRED",
                "MISSING_IN_ROSTER",
                "TEMPLE_MISSING_ACTION_REQUIRED",
                "WOM_MISSING_ACTION_REQUIRED",
                "WOM_ONLY_ACTION_REQUIRED");
            var pendingPromotions = await db.PromotionCandidates
                .Where(x => x.PlayerId == player.Id && x.Status == PromotionStatus.PENDING)
                .ToListAsync();
            foreach (var pendingPromotion in pendingPromotions)
            {
                pendingPromotion.Status = PromotionStatus.DISMISSED;
            }
        }
        else
        {
            await RespondToComponentAsync(component, "Unknown action.", ephemeral: true);
            return;
        }

        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = playerId,
            EventType = "TEMPLE_MISSING_ACTION_APPLIED",
            MetadataJson = JsonUtil.Serialize(new
            {
                Player = player.Username,
                Action = action,
                HandledBy = component.User.Username,
                HandledByDiscordUserId = component.User.Id,
                Source = "discord",
                ChannelId = component.Channel.Id,
                DiscordMessageId = component.Message.Id
            }),
            Status = "DONE",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var handled = $"Handled by {component.User.Username} ({action})";
        await UpdateComponentMessageAsync(component, BuildHandledEmbed(component.Message.Embeds.FirstOrDefault(), handled, action == "add" ? "approve" : "dismiss"));

        ScheduleChannelMessageDelete(
            db,
            playerId,
            component.Channel.Id,
            component.Message.Id,
            "TEMPLE_MISSING_DISCORD_DELETE_SCHEDULED",
            new { Reason = "temple-missing-action-handled", Action = action });
        await db.SaveChangesAsync();
    }

    private async Task HandleMissingWomButtonAsync(SocketMessageComponent component, string[] parts)
    {
        var action = parts[1];
        if (!int.TryParse(parts[2], out var playerId)) return;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var player = await db.Players.FirstOrDefaultAsync(x => x.Id == playerId);
        if (player is null)
        {
            await RespondToComponentAsync(component, "Player not found.", ephemeral: true);
            return;
        }

        var renameDetection = await TryPrepareRenameReviewBeforeSensitiveActionAsync(
            db,
            expectedPreviousPlayerId: player.Id,
            expectedNewUsername: null,
            expectedEitherUsername: null,
            ct: CancellationToken.None);
        if (renameDetection is not null)
        {
            await BlockSensitiveActionForRenameAsync(db, component, renameDetection, player.Id, CancellationToken.None);
            return;
        }

        if (action == "reinstate")
        {
            var womOk = await AddPlayerToWomAsync(player.Username);
            if (!womOk)
            {
                var womGroupId = configuration.GetValue<int?>("WiseOldMan:GroupId") ?? 0;
                var alreadyInWom = womGroupId > 0 && await IsPlayerInWiseOldManGroupAsync(player.Username, womGroupId);
                if (!alreadyInWom)
                {
                    await RespondToComponentAsync(component, "Failed to reinstate player in WiseOldMan.", ephemeral: true);
                    return;
                }
            }
            player.Status = PlayerStatus.ACTIVE;
            await CloseOpenLifecycleEventsAsync(db, player.Id,
                "NEW_PLAYER",
                "MERGE_SUGGESTED",
                "DISCORD_MARK_RENAME_SUSPECT",
                "MISSING_IN_ROSTER",
                "TEMPLE_MISSING_ACTION_REQUIRED",
                "WOM_MISSING_ACTION_REQUIRED");
        }
        else if (action == "remove")
        {
            var templeOk = await RemovePlayerFromTempleAsync(player.Username);
            if (!templeOk)
            {
                var templeGroupId = configuration.GetValue<int?>("TempleOsrs:GroupId") ?? 449;
                var stillInTemple = await IsPlayerInTempleGroupAsync(player.Username, templeGroupId);
                if (stillInTemple)
                {
                    await RespondToComponentAsync(component, "Failed to remove player from TempleOSRS.", ephemeral: true);
                    return;
                }
            }

            player.Status = PlayerStatus.REMOVED_CONFIRMED;
            await CloseOpenLifecycleEventsAsync(db, player.Id,
                "NEW_PLAYER",
                "MERGE_SUGGESTED",
                "MERGE_ACTION_REQUIRED",
                "MISSING_IN_ROSTER",
                "TEMPLE_MISSING_ACTION_REQUIRED",
                "WOM_MISSING_ACTION_REQUIRED",
                "WOM_ONLY_ACTION_REQUIRED");
            var pendingPromotions = await db.PromotionCandidates
                .Where(x => x.PlayerId == player.Id && x.Status == PromotionStatus.PENDING)
                .ToListAsync();
            foreach (var pendingPromotion in pendingPromotions)
            {
                pendingPromotion.Status = PromotionStatus.DISMISSED;
            }
        }
        else
        {
            await RespondToComponentAsync(component, "Unknown action.", ephemeral: true);
            return;
        }

        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = playerId,
            EventType = "WOM_MISSING_ACTION_APPLIED",
            MetadataJson = JsonUtil.Serialize(new
            {
                Player = player.Username,
                Action = action,
                HandledBy = component.User.Username,
                HandledByDiscordUserId = component.User.Id,
                Source = "discord",
                ChannelId = component.Channel.Id,
                DiscordMessageId = component.Message.Id
            }),
            Status = "DONE",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var handled = $"Handled by {component.User.Username} ({action})";
        await UpdateComponentMessageAsync(component, BuildHandledEmbed(component.Message.Embeds.FirstOrDefault(), handled, action == "reinstate" ? "approve" : "dismiss"));

        ScheduleChannelMessageDelete(
            db,
            playerId,
            component.Channel.Id,
            component.Message.Id,
            "WOM_MISSING_DISCORD_DELETE_SCHEDULED",
            new { Reason = "wom-missing-action-handled", Action = action });
        await db.SaveChangesAsync();
    }

    private async Task HandleSelectMenuAsync(SocketMessageComponent component)
    {
        try
        {
            if (!component.HasResponded) await component.DeferAsync(ephemeral: true);
            var customId = component.Data.CustomId ?? "";
            if (customId.StartsWith("promo:discord-user:", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasDiscordAdminRole(component.User))
                {
                    await RespondToComponentAsync(component, "You need the admin role for this action.", ephemeral: true);
                    return;
                }

                await HandlePromotionDiscordUserSelectAsync(component, customId);
                return;
            }

            if (customId.StartsWith("promo:", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasDiscordAdminRole(component.User))
                {
                    await RespondToComponentAsync(component, "You need the admin role for this action.", ephemeral: true);
                    return;
                }

                await HandlePromotionDiscordMemberSelectAsync(component, customId);
                return;
            }

            if (customId.StartsWith("drankreview:", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasDiscordAdminRole(component.User))
                {
                    await RespondToComponentAsync(component, "You need the admin role for this action.", ephemeral: true);
                    return;
                }

                await HandleDiscordRankReviewManualSelectAsync(component, customId);
                return;
            }

            if (!customId.StartsWith("mergepick:", StringComparison.OrdinalIgnoreCase)) return;
            if (!HasDiscordAdminRole(component.User))
            {
                await RespondToComponentAsync(component, "You need the admin role for this action.", ephemeral: true);
                return;
            }

            var parts = customId.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var playerId))
            {
                await RespondToComponentAsync(component, "Malformed selection.", ephemeral: true);
                return;
            }

            var selected = component.Data.Values.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(selected))
            {
                await RespondToComponentAsync(component, "No candidate selected.", ephemeral: true);
                return;
            }
            await ApplyMergeActionAsync(component, playerId, "reassign", selected);
            try
            {
                await component.Message.DeleteAsync();
            }
            catch
            {
                // best effort cleanup of one-off candidate picker prompt
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling merge select menu.");
        }
    }

    private async Task HandleModalSubmittedAsync(SocketModal modal)
    {
        try
        {
            if (!modal.HasResponded) await modal.DeferAsync(ephemeral: true);
            if (!modal.Data.CustomId.StartsWith("mergemanual:", StringComparison.OrdinalIgnoreCase)) return;
            if (!HasDiscordAdminRole(modal.User))
            {
                await RespondModalAsync(modal, "You need the admin role for this action.", true);
                return;
            }
            var parts = modal.Data.CustomId.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var playerId))
            {
                await RespondModalAsync(modal, "Malformed manual rename request.", true);
                return;
            }
            var previous = modal.Data.Components.FirstOrDefault(x => x.CustomId == "previous")?.Value?.Trim();
            await ApplyMergeActionAsync(modal, playerId, "manual", previous ?? "");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling merge modal.");
        }
    }

    private async Task HandleWomOnlyButtonAsync(SocketMessageComponent component, string[] parts)
    {
        var action = parts[1];
        if (action is not ("add" or "ignore"))
        {
            await RespondToComponentAsync(component, "Unknown action.", ephemeral: true);
            return;
        }

        if (!int.TryParse(parts[2], out var requiredEventId))
        {
            await RespondToComponentAsync(component, "Unknown action.", ephemeral: true);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();

        var requiredEvent = await db.LifecycleEvents
            .FirstOrDefaultAsync(x => x.Id == requiredEventId && x.EventType == "WOM_ONLY_ACTION_REQUIRED");
        if (requiredEvent is null)
        {
            await RespondToComponentAsync(component, "This alert is no longer valid.", ephemeral: true);
            return;
        }

        var metadata = ReadLifecycleMetadata(requiredEvent.MetadataJson);
        var username = PickLifecycleValue(metadata, "Username", "Player");
        var actualWomRole = PickLifecycleValue(metadata, "ActualWomRole") ?? "Unknown";
        if (string.IsNullOrWhiteSpace(username))
        {
            requiredEvent.Status = "DONE";
            await db.SaveChangesAsync();
            await RespondToComponentAsync(component, "This alert is malformed and was closed.", ephemeral: true);
            return;
        }

        var normalizedUsername = NormalizeUsername(username);
        if (requiredEvent.Status != "OPEN")
        {
            await RespondToComponentAsync(component, "This alert was already handled.", ephemeral: true);
            return;
        }

        var renameDetection = await TryPrepareRenameReviewBeforeSensitiveActionAsync(
            db,
            expectedPreviousPlayerId: null,
            expectedNewUsername: normalizedUsername,
            expectedEitherUsername: null,
            ct: CancellationToken.None);
        if (renameDetection is not null)
        {
            await BlockSensitiveActionForRenameAsync(db, component, renameDetection, requiredEvent.PlayerId, CancellationToken.None);
            return;
        }

        if (action == "add")
        {
            var templeRequestAccepted = await AddPlayerToTempleAsync(normalizedUsername);
            var templeGroupId = configuration.GetValue<int?>("TempleOsrs:GroupId") ?? 449;
            var templeAdded = await IsPlayerInTempleGroupAsync(normalizedUsername, templeGroupId);

            if (!templeAdded)
            {
                var failureText = templeRequestAccepted
                    ? "Temple add request was accepted, but membership could not be confirmed yet. Try again shortly."
                    : "Failed to add player to Temple and membership could not be confirmed.";
                await RespondToComponentAsync(component, failureText, ephemeral: true);
                return;
            }
        }

        requiredEvent.Status = "DONE";
        await CloseOpenWomOnlyRequiredEventsAsync(db, normalizedUsername);

        if (action == "ignore")
        {
            await EnsureOpenWomOnlyIgnoredEventAsync(db, requiredEvent.PlayerId, normalizedUsername, actualWomRole);
        }

        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = requiredEvent.PlayerId,
            EventType = "WOM_ONLY_ACTION_APPLIED",
            MetadataJson = JsonUtil.Serialize(new
            {
                Username = normalizedUsername,
                ActualWomRole = actualWomRole,
                Action = action,
                RequiredEventId = requiredEvent.Id,
                HandledBy = component.User.Username,
                HandledByDiscordUserId = component.User.Id,
                Source = "discord",
                ChannelId = component.Channel.Id,
                DiscordMessageId = component.Message.Id
            }),
            Status = "DONE",
            CreatedAt = DateTimeOffset.UtcNow
        });

        var handledText = action switch
        {
            "add" => $"Added to Temple by {component.User.Username}",
            "ignore" => $"Ignored by {component.User.Username}",
            _ => $"Handled by {component.User.Username}"
        };

        await UpdateComponentMessageAsync(
            component,
            BuildHandledEmbed(component.Message.Embeds.FirstOrDefault(), handledText, action == "add" ? "approve" : "dismiss"));

        ScheduleChannelMessageDelete(
            db,
            requiredEvent.PlayerId,
            component.Channel.Id,
            component.Message.Id,
            "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED",
            new
            {
                Reason = "wom-only-action-handled",
                Action = action,
                MessageDescription = $"WOM-only review card for {normalizedUsername}"
            });

        await db.SaveChangesAsync();
    }

    private async Task HandleTempleNameChangeButtonAsync(SocketMessageComponent component, string[] parts)
    {
        var action = parts[1];
        if (action is not ("confirm" or "decline"))
        {
            await RespondToComponentAsync(component, "Unknown action.", ephemeral: true);
            return;
        }
        if (!int.TryParse(parts[2], out var requiredEventId))
        {
            await RespondToComponentAsync(component, "Unknown action.", ephemeral: true);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var requiredEvent = await db.LifecycleEvents.FirstOrDefaultAsync(x =>
            x.Id == requiredEventId &&
            x.EventType == TempleNameChangeReviewEventTypes.Required);
        if (requiredEvent is null)
        {
            await RespondToComponentAsync(component, "This Temple name-change review no longer exists.", ephemeral: true);
            return;
        }
        if (requiredEvent.Status != "OPEN")
        {
            await RespondToComponentAsync(component, "This Temple name-change review was already handled.", ephemeral: true);
            return;
        }

        var metadata = ReadLifecycleMetadata(requiredEvent.MetadataJson);
        var previousUsername = NormalizeUsername(PickLifecycleValue(metadata, "PreviousUsername") ?? "");
        var newUsername = NormalizeUsername(PickLifecycleValue(metadata, "NewUsername") ?? "");
        var previousPlayerId = ExtractInt(requiredEvent.MetadataJson, "PreviousPlayerId") ?? requiredEvent.PlayerId;
        if (string.IsNullOrWhiteSpace(previousUsername) || string.IsNullOrWhiteSpace(newUsername))
        {
            requiredEvent.Status = "DONE";
            await db.SaveChangesAsync();
            await RespondToComponentAsync(component, "This Temple name-change review was malformed and has been closed.", ephemeral: true);
            return;
        }

        if (action == "confirm")
        {
            await CloseOpenLifecycleEventsAsync(db, previousPlayerId,
                "MISSING_IN_ROSTER",
                "TEMPLE_MISSING_ACTION_REQUIRED",
                "WOM_MISSING_ACTION_REQUIRED");
            await CloseOpenWomOnlyRequiredEventsAsync(db, newUsername);

            requiredEvent.MetadataJson = JsonUtil.Serialize(new
            {
                PreviousUsername = previousUsername,
                NewUsername = newUsername,
                PreviousPlayerId = previousPlayerId,
                Rank = PickLifecycleValue(metadata, "Rank"),
                WomRole = PickLifecycleValue(metadata, "WomRole"),
                WomMissingEventId = ExtractInt(requiredEvent.MetadataJson, "WomMissingEventId"),
                TempleMissingEventId = ExtractInt(requiredEvent.MetadataJson, "TempleMissingEventId"),
                WomOnlyEventId = ExtractInt(requiredEvent.MetadataJson, "WomOnlyEventId"),
                ConfirmedAt = DateTimeOffset.UtcNow,
                ConfirmedBy = component.User.Username,
                ConfirmedByDiscordUserId = component.User.Id,
                Source = "discord"
            });
        }
        else
        {
            requiredEvent.Status = "DONE";
        }

        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = previousPlayerId,
            EventType = TempleNameChangeReviewEventTypes.ActionApplied,
            MetadataJson = JsonUtil.Serialize(new
            {
                PreviousUsername = previousUsername,
                NewUsername = newUsername,
                Action = action,
                RequiredEventId = requiredEvent.Id,
                HandledBy = component.User.Username,
                HandledByDiscordUserId = component.User.Id,
                Source = "discord",
                ChannelId = component.Channel.Id,
                DiscordMessageId = component.Message.Id
            }),
            Status = "DONE",
            CreatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();

        var handledText = action == "confirm"
            ? $"Confirmed by {component.User.Username}; TempleOSRS manual name update is now expected."
            : $"Declined by {component.User.Username}; normal review cards will resume.";
        await UpdateComponentMessageAsync(
            component,
            BuildHandledEmbed(component.Message.Embeds.FirstOrDefault(), handledText, action == "confirm" ? "approve" : "dismiss"));

        ScheduleChannelMessageDelete(
            db,
            previousPlayerId,
            component.Channel.Id,
            component.Message.Id,
            "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED",
            new
            {
                Reason = "temple-name-change-action-handled",
                Action = action,
                PreviousUsername = previousUsername,
                NewUsername = newUsername
            });
        await db.SaveChangesAsync();
    }

    private async Task HandleMergeButtonAsync(SocketMessageComponent component, string[] parts)
    {
        var action = parts[1];
        if (!int.TryParse(parts[2], out var playerId))
        {
            await RespondToComponentAsync(component, "Malformed merge action.", ephemeral: true);
            return;
        }

        if (action == "choose")
        {
            var options = await GetMergeCandidateOptionsAsync(playerId);
            if (options.Count == 0)
            {
                await RespondToComponentAsync(component, "No alternate candidates available.", ephemeral: true);
                return;
            }

            var menu = new SelectMenuBuilder()
                .WithCustomId($"mergepick:{playerId}")
                .WithPlaceholder("Select previous player")
                .WithMinValues(1)
                .WithMaxValues(1);
            foreach (var option in options.Take(25))
            {
                menu.AddOption(option, option);
            }
            var builder = new ComponentBuilder().WithSelectMenu(menu);
            await component.FollowupAsync("Pick the previous player to merge into.", components: builder.Build(), ephemeral: true);
            return;
        }

        if (action == "manual")
        {
            var modal = new ModalBuilder()
                .WithTitle("Manual Previous Name")
                .WithCustomId($"mergemanual:{playerId}")
                .AddTextInput("Previous username", "previous", TextInputStyle.Short, placeholder: "e.g. Zymzalabim", maxLength: 64, required: true);
            await component.RespondWithModalAsync(modal.Build());
            return;
        }

        if (action is "confirm" or "abort")
        {
            await ApplyMergeActionAsync(component, playerId, action, null);
            return;
        }

        await RespondToComponentAsync(component, "Unknown merge action.", ephemeral: true);
    }

    private async Task ApplyMergeActionAsync(SocketMessageComponent component, int playerId, string action, string? previousUsername)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var mergeService = scope.ServiceProvider.GetRequiredService<IMergeReviewService>();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();

        MergeActionResult result = action switch
        {
            "confirm" => await mergeService.ConfirmSuggestedAsync(playerId, component.User.Username, "discord", CancellationToken.None),
            "abort" => await mergeService.AbortAsync(playerId, component.User.Username, "discord", CancellationToken.None),
            _ => await mergeService.ReassignAsync(playerId, previousUsername ?? "", component.User.Username, "discord", CancellationToken.None)
        };
        if (!result.Success)
        {
            await RespondToComponentAsync(component, result.Message, ephemeral: true);
            return;
        }

        try
        {
            var response = await RespondToComponentAsync(component, result.Message, ephemeral: false);
            var ownerId = await ResolveLifecycleOwnerPlayerIdAsync(db, playerId, CancellationToken.None);
            if (!ownerId.HasValue)
            {
                logger.LogWarning(
                    "Merge action succeeded for playerId {PlayerId}, but no valid lifecycle owner row exists. Skipping delete scheduling for Discord messages {OriginalMessageId} and {ResponseMessageId}.",
                    playerId,
                    component.Message.Id,
                    response?.Id);
            }
            else
            {
                ScheduleChannelMessageDelete(db, ownerId.Value, component.Channel.Id, component.Message.Id, "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED", new { Reason = "merge-action-handled", Action = action });
                if (response is not null)
                {
                    ScheduleChannelMessageDelete(db, ownerId.Value, component.Channel.Id, response.Id, "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED", new { Reason = "merge-action-result", Action = action });
                }
                await db.SaveChangesAsync();
            }

            var handled = $"Handled by {component.User.Username} ({action})";
            try
            {
                await UpdateComponentMessageAsync(component, BuildHandledEmbed(component.Message.Embeds.FirstOrDefault(), handled, action == "abort" ? "dismiss" : "approve"));
            }
            catch (Exception uiEx)
            {
                logger.LogWarning(uiEx, "Merge action succeeded and delete schedule persisted, but Discord card update failed for playerId {PlayerId}.", playerId);
            }
        }
        catch (Exception ex)
        {
            // Merge action already succeeded. Do not bubble a false failure.
            logger.LogWarning(ex, "Merge action succeeded for playerId {PlayerId} but Discord UI update failed.", playerId);
        }
    }

    private async Task ApplyMergeActionAsync(SocketModal modal, int playerId, string action, string? previousUsername)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var mergeService = scope.ServiceProvider.GetRequiredService<IMergeReviewService>();
        MergeActionResult result = action switch
        {
            "abort" => await mergeService.AbortAsync(playerId, modal.User.Username, "discord", CancellationToken.None),
            _ => await mergeService.ReassignAsync(playerId, previousUsername ?? "", modal.User.Username, "discord", CancellationToken.None)
        };
        await RespondModalAsync(modal, result.Message, true);
    }

    private async Task RespondModalAsync(SocketModal modal, string message, bool ephemeral)
    {
        if (modal.HasResponded)
        {
            await modal.FollowupAsync(message, ephemeral: ephemeral);
        }
        else
        {
            await modal.RespondAsync(message, ephemeral: ephemeral);
        }
    }

    private async Task HandleWomRankMismatchButtonAsync(SocketMessageComponent component, string[] parts)
    {
        var action = parts[1];
        if (action is not ("dismiss" or "ignore" or "sync_wom_to_db" or "sync_db_to_wom"))
        {
            await RespondToComponentAsync(component, "Unknown action.", ephemeral: true);
            return;
        }
        if (!int.TryParse(parts[2], out var playerId)) return;
        int? requiredEventId = null;
        if (parts.Length == 4)
        {
            if (!int.TryParse(parts[3], out var parsedRequiredEventId))
            {
                await RespondToComponentAsync(component, "Unknown action.", ephemeral: true);
                return;
            }
            requiredEventId = parsedRequiredEventId;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var wiseOldMan = scope.ServiceProvider.GetRequiredService<IWiseOldManClient>();

        var player = await db.Players.FirstOrDefaultAsync(x => x.Id == playerId);
        var openMismatches = await db.LifecycleEvents
            .Where(x => x.PlayerId == playerId && x.EventType == "WOM_RANK_MISMATCH_REQUIRED" && x.Status == "OPEN")
            .OrderBy(x => x.CreatedAt)
            .ToListAsync();
        var postedEvents = await db.LifecycleEvents
            .Where(x => x.PlayerId == playerId && x.EventType == "WOM_RANK_MISMATCH_DISCORD_POSTED")
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();
        var clickedPostedEvent = FindLifecycleEventByDiscordMessageId(postedEvents, component.Message.Id);
        var latestPostedEvent = postedEvents.FirstOrDefault();
        var isLegacyStaleClick = requiredEventId is null &&
            latestPostedEvent is not null &&
            clickedPostedEvent?.Id != latestPostedEvent.Id;

        var matchingOpenMismatches = requiredEventId.HasValue
            ? openMismatches.Where(x => x.Id == requiredEventId.Value).ToList()
            : isLegacyStaleClick ? [] : openMismatches;
        var metadataSource = matchingOpenMismatches.LastOrDefault() ??
            openMismatches.LastOrDefault() ??
            clickedPostedEvent ??
            latestPostedEvent;
        var metadata = ReadLifecycleMetadata(metadataSource?.MetadataJson ?? "{}");
        var playerName = player?.Username ?? PickLifecycleValue(metadata, "Player", "Username") ?? "Unknown player";
        var expectedRank = PickLifecycleValue(metadata, "ExpectedRank") ?? player?.CurrentRank ?? "Unknown";
        var actualWomRole = PickLifecycleValue(metadata, "ActualWomRole") ?? "Unknown";
        var requestedRole = expectedRank;
        var updatedRole = (string?)null;
        int? womPlayerId = null;
        string? womDisplayName = null;
        int? womHttpStatus = null;
        string? womDetails = null;
        var dbRankBefore = player?.CurrentRank ?? expectedRank;
        var dbRankAfter = dbRankBefore;
        var womRankBefore = actualWomRole;
        var womRankAfter = actualWomRole;
        var closedRequiredEventIds = matchingOpenMismatches.Select(x => x.Id).ToArray();
        var shouldCloseMismatch = action is not "sync_db_to_wom";
        foreach (var ev in matchingOpenMismatches)
        {
            if (shouldCloseMismatch)
            {
                ev.Status = "DONE";
            }
        }
        var closedActiveMismatch = shouldCloseMismatch && closedRequiredEventIds.Length > 0;

        if (action == "sync_wom_to_db")
        {
            if (player is null)
            {
                await RespondToComponentAsync(component, "Player not found.", ephemeral: true);
                return;
            }

            player.CurrentRank = actualWomRole;
            dbRankAfter = player.CurrentRank;

            // Re-enable mismatch tracking after an explicit sync decision.
            await CloseOpenLifecycleEventsAsync(db, player.Id, "WOM_RANK_MISMATCH_IGNORED");
        }
        else if (action == "sync_db_to_wom")
        {
            var womUpdate = await ExecuteWomRoleUpdateForPlayerAsync(playerName, requestedRole);
            womHttpStatus = womUpdate.HttpStatus;
            womDetails = womUpdate.Details;
            updatedRole = womUpdate.UpdatedRole;
            womPlayerId = womUpdate.WomPlayerId;
            womDisplayName = womUpdate.DisplayName;
            womRankAfter = womUpdate.UpdatedRole ?? womRankBefore;
            if (!womUpdate.Success)
            {
                await RespondToComponentAsync(component, $"Failed to update WiseOldMan role: {womUpdate.Details}", ephemeral: true);
                closedActiveMismatch = false;
            }
            else
            {
                foreach (var ev in matchingOpenMismatches)
                {
                    ev.Status = "DONE";
                }
                closedActiveMismatch = closedRequiredEventIds.Length > 0;
                if (player is not null)
                {
                    await CloseOpenLifecycleEventsAsync(db, player.Id, "WOM_RANK_MISMATCH_IGNORED");
                }
            }
        }

        if (action == "ignore")
        {
            var hasOpenIgnore = await db.LifecycleEvents.AnyAsync(x =>
                x.PlayerId == playerId &&
                x.EventType == "WOM_RANK_MISMATCH_IGNORED" &&
                x.Status == "OPEN");
            if (closedActiveMismatch && player is not null && !hasOpenIgnore)
            {
                db.LifecycleEvents.Add(new LifecycleEvent
                {
                    PlayerId = playerId,
                    EventType = "WOM_RANK_MISMATCH_IGNORED",
                    MetadataJson = JsonUtil.Serialize(new
                    {
                        Player = player.Username,
                        ExpectedRank = expectedRank,
                        ActualWomRole = actualWomRole,
                        IgnoredBy = component.User.Username,
                        HandledBy = component.User.Username,
                        HandledByDiscordUserId = component.User.Id,
                        IgnoredAt = DateTimeOffset.UtcNow
                    }),
                    Status = "OPEN",
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
        }

        var ownerId = player?.Id ?? await ResolveLifecycleOwnerPlayerIdAsync(db, playerId, CancellationToken.None);
        if (ownerId.HasValue)
        {
            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = ownerId.Value,
                EventType = "WOM_RANK_MISMATCH_ACTION_APPLIED",
                MetadataJson = JsonUtil.Serialize(new
                {
                    Player = playerName,
                    ExpectedRank = expectedRank,
                    ActualWomRole = actualWomRole,
                    Action = action,
                    Direction = GetWomRankMismatchDirection(expectedRank, actualWomRole),
                    DbRankBefore = dbRankBefore,
                    DbRankAfter = dbRankAfter,
                    WomRankBefore = womRankBefore,
                    WomRankAfter = womRankAfter,
                    RequestedRole = requestedRole,
                    UpdatedRole = updatedRole,
                    WiseOldManPlayerId = womPlayerId,
                    WiseOldManDisplayName = womDisplayName,
                    HttpStatus = womHttpStatus,
                    Details = womDetails,
                    HandledBy = component.User.Username,
                    HandledByDiscordUserId = component.User.Id,
                    Source = "discord",
                    RequiredEventId = requiredEventId,
                    ClosedRequiredEventIds = closedRequiredEventIds,
                    ChannelId = component.Channel.Id,
                    DiscordMessageId = component.Message.Id,
                    Stale = !closedActiveMismatch
                }),
                Status = "DONE",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        await wiseOldMan.InvalidateCacheAsync(CancellationToken.None);
        await db.SaveChangesAsync();

        var handled = (action, closedActiveMismatch) switch
        {
            ("ignore", true) => $"Allowed/ignored by {component.User.Username}",
            ("ignore", false) => $"Stale WOM rank mismatch alert cleaned up by {component.User.Username}",
            ("sync_wom_to_db", true) => $"Synced both sides to WOM rank by {component.User.Username}",
            ("sync_wom_to_db", false) => $"Database sync requested by {component.User.Username}, but alert was stale",
            ("sync_db_to_wom", true) => $"Synced both sides to database rank by {component.User.Username}",
            ("sync_db_to_wom", false) => $"WiseOldMan sync attempt by {component.User.Username} did not resolve the active mismatch",
            ("dismiss", true) => $"Dismissed by {component.User.Username}; update the rank in game/WOM if it still mismatches",
            _ => $"Already handled; cleaned up by {component.User.Username}"
        };
        var handledActionStyle = action is "ignore" or "sync_wom_to_db" or "sync_db_to_wom" ? "approve" : "dismiss";
        await UpdateComponentMessageAsync(component, BuildHandledEmbed(component.Message.Embeds.FirstOrDefault(), handled, handledActionStyle));

        if (ownerId.HasValue)
        {
            ScheduleWomRankMismatchMessageDelete(
                db,
                ownerId.Value,
                component.Channel.Id,
                component.Message.Id,
                "wom-rank-mismatch-action-handled",
                $"WOM rank mismatch alert for {playerName}",
                action);
            await db.SaveChangesAsync();
        }
        else
        {
            try
            {
                await component.Message.DeleteAsync();
            }
            catch
            {
                // best effort if no valid player row exists for lifecycle scheduling
            }
        }
    }

    private void ScheduleDelete(int candidateId, int playerId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        ulong? channelId = null;
        ulong? messageId = null;
        var posted = db.LifecycleEvents
            .Where(x => x.EventType == "PROMOTION_DISCORD_POSTED")
            .OrderByDescending(x => x.CreatedAt)
            .AsEnumerable();
        foreach (var ev in posted)
        {
            try
            {
                using var doc = JsonDocument.Parse(ev.MetadataJson);
                if (!doc.RootElement.TryGetProperty("CandidateId", out var c) || c.GetInt32() != candidateId) continue;
                if (doc.RootElement.TryGetProperty("ChannelId", out var ch)) channelId = ch.GetUInt64();
                if (doc.RootElement.TryGetProperty("DiscordMessageId", out var m)) messageId = m.GetUInt64();
                break;
            }
            catch { }
        }

        if (!messageId.HasValue)
        {
            var candidateDeleteEvents = db.LifecycleEvents
                .Where(x => x.EventType == "PROMOTION_DISCORD_DELETE_SCHEDULED")
                .ToList();
            var hasAnyDeleteForCandidate = candidateDeleteEvents.Any(x =>
                MetadataIntEquals(x.MetadataJson, "CandidateId", candidateId));
            if (hasAnyDeleteForCandidate) return;
        }

        var now = DateTimeOffset.UtcNow;
        var deleteAfter = now.AddMinutes(_discordDeleteDelayMinutes);
        var hardDeleteAfter = now.AddMinutes(_discordDeleteHardCapMinutes);
        ScheduleChannelMessageDelete(
            db,
            playerId,
            channelId,
            messageId,
            "PROMOTION_DISCORD_DELETE_SCHEDULED",
            new
            {
                CandidateId = candidateId,
                Reason = "promotion-action-handled"
            },
            deleteAfter,
            hardDeleteAfter,
            extendExistingSchedule: true);
        db.SaveChanges();
    }

    private void ScheduleChannelMessageDelete(
        TrackerDbContext db,
        int playerId,
        ulong? channelId,
        ulong? messageId,
        string eventType,
        object extraMetadata,
        DateTimeOffset? deleteAfterUtc = null,
        DateTimeOffset? hardDeleteAfterUtc = null,
        bool dedupeCompletedSchedules = true,
        bool extendExistingSchedule = false)
    {
        var now = DateTimeOffset.UtcNow;
        var deleteAfter = deleteAfterUtc ?? now.AddMinutes(Math.Min(_discordDeleteDelayMinutes, _discordDeleteHardCapMinutes));
        var hardDeleteAfter = hardDeleteAfterUtc ?? now.AddMinutes(_discordDeleteHardCapMinutes);
        if (hardDeleteAfter < deleteAfter) hardDeleteAfter = deleteAfter;

        if (messageId.HasValue)
        {
            var scheduledDeleteEvents = db.LifecycleEvents
                .Where(x =>
                    (x.EventType == "PROMOTION_DISCORD_DELETE_SCHEDULED" ||
                     x.EventType == "TEMPLE_MISSING_DISCORD_DELETE_SCHEDULED" ||
                     x.EventType == "WOM_MISSING_DISCORD_DELETE_SCHEDULED" ||
                     x.EventType == "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED") &&
                    (dedupeCompletedSchedules || x.Status == "OPEN"))
                .ToList();
            var existingSchedule = scheduledDeleteEvents.FirstOrDefault(x =>
                MetadataUlongEquals(x.MetadataJson, "DiscordMessageId", messageId.Value));
            if (existingSchedule is not null)
            {
                if (extendExistingSchedule && existingSchedule.Status == "OPEN")
                {
                    ExtendMessageDeleteSchedule(existingSchedule, deleteAfter, hardDeleteAfter, extraMetadata);
                }

                return;
            }
        }

        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = playerId,
            EventType = eventType,
            MetadataJson = JsonUtil.Serialize(new
            {
                ChannelId = channelId,
                DiscordMessageId = messageId,
                DeleteAfterUtc = deleteAfter,
                HardDeleteAfterUtc = hardDeleteAfter,
                Extra = extraMetadata
            }),
            Status = "OPEN",
            CreatedAt = now
        });
    }

    private static void ExtendMessageDeleteSchedule(
        LifecycleEvent scheduledDelete,
        DateTimeOffset requestedDeleteAfterUtc,
        DateTimeOffset requestedHardDeleteAfterUtc,
        object extraMetadata)
    {
        var existingMetadata = ReadLifecycleMetadata(scheduledDelete.MetadataJson);
        var existingDeleteAfterUtc = PickLifecycleDateTimeOffset(existingMetadata, "DeleteAfterUtc");
        var existingHardDeleteAfterUtc = PickLifecycleDateTimeOffset(existingMetadata, "HardDeleteAfterUtc");
        var deleteAfterUtc = existingDeleteAfterUtc.HasValue && existingDeleteAfterUtc.Value > requestedDeleteAfterUtc
            ? existingDeleteAfterUtc.Value
            : requestedDeleteAfterUtc;
        var hardDeleteAfterUtc = existingHardDeleteAfterUtc.HasValue && existingHardDeleteAfterUtc.Value > requestedHardDeleteAfterUtc
            ? existingHardDeleteAfterUtc.Value
            : requestedHardDeleteAfterUtc;
        if (hardDeleteAfterUtc < deleteAfterUtc) hardDeleteAfterUtc = deleteAfterUtc;

        var metadata = ParseLifecycleMetadataObject(scheduledDelete.MetadataJson);
        metadata["DeleteAfterUtc"] = deleteAfterUtc;
        metadata["HardDeleteAfterUtc"] = hardDeleteAfterUtc;
        metadata["Extra"] = JsonSerializer.SerializeToNode(extraMetadata);
        scheduledDelete.MetadataJson = metadata.ToJsonString();
    }

    private void ScheduleWomRankMismatchMessageDelete(
        TrackerDbContext db,
        int playerId,
        ulong? channelId,
        ulong? messageId,
        string reason,
        string messageDescription,
        string? action = null,
        DateTimeOffset? deleteAfterUtc = null,
        DateTimeOffset? hardDeleteAfterUtc = null)
    {
        ScheduleChannelMessageDelete(
            db,
            playerId,
            channelId,
            messageId,
            "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED",
            new
            {
                Reason = reason,
                Action = action,
                MessageDescription = messageDescription
            },
            deleteAfterUtc,
            hardDeleteAfterUtc);
    }

    private async Task<(PostedMessageLookupState State, IUserMessage? Message, ulong? ChannelId, ulong? MessageId)> TryGetPostedUserMessageAsync(
        LifecycleEvent postedEvent,
        string context)
    {
        ulong? channelId = null;
        ulong? messageId = null;
        try
        {
            using var postedDoc = JsonDocument.Parse(postedEvent.MetadataJson);
            if (!TryReadUlong(postedDoc.RootElement, "ChannelId", out var parsedChannelId) ||
                !TryReadUlong(postedDoc.RootElement, "DiscordMessageId", out var parsedMessageId))
            {
                return (PostedMessageLookupState.Malformed, null, channelId, messageId);
            }

            channelId = parsedChannelId;
            messageId = parsedMessageId;
            var postedChannel = await ResolveMessageChannelAsync(parsedChannelId);
            if (postedChannel is null)
            {
                return (PostedMessageLookupState.Unknown, null, channelId, messageId);
            }

            var existingMessage = await postedChannel.GetMessageAsync(parsedMessageId);
            if (existingMessage is IUserMessage userMessage)
            {
                return (PostedMessageLookupState.Found, userMessage, channelId, messageId);
            }
            return (PostedMessageLookupState.Missing, null, channelId, messageId);
        }
        catch (Discord.Net.HttpException ex) when (IsDiscordMessageMissingOrInaccessible(ex))
        {
            return (PostedMessageLookupState.Missing, null, channelId, messageId);
        }
        catch
        {
            logger.LogWarning("Unable to resolve posted Discord message for context {Context}. LifecycleEventId={LifecycleEventId}.", context, postedEvent.Id);
            return (PostedMessageLookupState.Unknown, null, channelId, messageId);
        }
    }

    private bool IsLookupBackoffActive(string key)
    {
        if (!_lookupBackoffUntilByKey.TryGetValue(key, out var until)) return false;
        if (until > DateTimeOffset.UtcNow) return true;
        _lookupBackoffUntilByKey.TryRemove(key, out _);
        return false;
    }

    private void SetLookupBackoff(string key)
    {
        _lookupBackoffUntilByKey[key] = DateTimeOffset.UtcNow.AddSeconds(45);
    }

    private async Task RecordMissingTrackedMessageEventAsync(
        TrackerDbContext db,
        int playerId,
        string cardType,
        int? postedEventId,
        int? requiredEventId,
        ulong? channelId,
        ulong? messageId,
        string sourceContext,
        CancellationToken ct)
    {
        var hasRecent = await db.LifecycleEvents.AnyAsync(x =>
            x.PlayerId == playerId &&
            x.EventType == "DISCORD_POSTED_MESSAGE_MISSING" &&
            x.CreatedAt >= DateTimeOffset.UtcNow.AddMinutes(-5), ct);
        if (hasRecent)
        {
            var recentRows = await db.LifecycleEvents
                .Where(x =>
                    x.PlayerId == playerId &&
                    x.EventType == "DISCORD_POSTED_MESSAGE_MISSING" &&
                    x.CreatedAt >= DateTimeOffset.UtcNow.AddMinutes(-5))
                .ToListAsync(ct);
            hasRecent = recentRows.Any(x => MetadataStringEquals(x.MetadataJson, "CardType", cardType));
        }
        if (hasRecent) return;

        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = playerId,
            EventType = "DISCORD_POSTED_MESSAGE_MISSING",
            MetadataJson = JsonUtil.Serialize(new
            {
                CardType = cardType,
                PostedEventId = postedEventId,
                RequiredEventId = requiredEventId,
                ChannelId = channelId,
                DiscordMessageId = messageId,
                Source = sourceContext
            }),
            Status = "DONE",
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private async Task<LifecycleEvent?> TryAcquirePostLeaseAsync(
        TrackerDbContext db,
        int ownerPlayerId,
        string leaseKey,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var openLeases = await db.LifecycleEvents
            .Where(x => x.EventType == "DISCORD_POST_LEASE" && x.Status == "OPEN")
            .ToListAsync(ct);
        foreach (var openLease in openLeases)
        {
            try
            {
                using var doc = JsonDocument.Parse(openLease.MetadataJson);
                var key = doc.RootElement.TryGetProperty("Key", out var keyProp) ? keyProp.GetString() ?? "" : "";
                var hasLeaseUntil = TryReadDateTimeOffset(doc.RootElement, "LeaseUntilUtc", out var leaseUntilUtc);
                if (string.Equals(key, leaseKey, StringComparison.OrdinalIgnoreCase) && hasLeaseUntil && leaseUntilUtc <= now)
                {
                    openLease.Status = "DONE";
                }
            }
            catch
            {
                openLease.Status = "DONE";
            }
        }
        await db.SaveChangesAsync(ct);

        var openLeaseRows = await db.LifecycleEvents
            .Where(x =>
                x.EventType == "DISCORD_POST_LEASE" &&
                x.Status == "OPEN")
            .ToListAsync(ct);
        var hasOpenLease = openLeaseRows.Any(x =>
            x.EventType == "DISCORD_POST_LEASE" &&
            x.Status == "OPEN" &&
            MetadataStringEquals(x.MetadataJson, "Key", leaseKey));
        if (hasOpenLease) return null;

        var leaseEvent = new LifecycleEvent
        {
            PlayerId = ownerPlayerId,
            EventType = "DISCORD_POST_LEASE",
            MetadataJson = JsonUtil.Serialize(new
            {
                Key = leaseKey,
                LeaseUntilUtc = now.AddSeconds(45)
            }),
            Status = "OPEN",
            CreatedAt = now
        };
        db.LifecycleEvents.Add(leaseEvent);
        await db.SaveChangesAsync(ct);

        var contenders = (await db.LifecycleEvents
            .Where(x =>
                x.EventType == "DISCORD_POST_LEASE" &&
                x.Status == "OPEN")
            .ToListAsync(ct))
            .Where(x => MetadataStringEquals(x.MetadataJson, "Key", leaseKey))
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToList();
        if (contenders.Count == 0 || contenders[0].Id != leaseEvent.Id)
        {
            leaseEvent.Status = "DONE";
            await db.SaveChangesAsync(ct);
            return null;
        }

        return leaseEvent;
    }

    private static async Task<bool> HasWomRankMismatchActionForPostedEventAsync(
        TrackerDbContext db,
        int playerId,
        LifecycleEvent postedEvent,
        CancellationToken ct)
    {
        var postedMessageId = ExtractUlong(postedEvent.MetadataJson, "DiscordMessageId");
        var postedRequiredEventId = ExtractInt(postedEvent.MetadataJson, "RequiredEventId");
        var actions = await db.LifecycleEvents
            .Where(x =>
                x.PlayerId == playerId &&
                x.EventType == "WOM_RANK_MISMATCH_ACTION_APPLIED" &&
                x.CreatedAt >= postedEvent.CreatedAt)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

        foreach (var action in actions)
        {
            var actionMetadata = ReadLifecycleMetadata(action.MetadataJson);
            var actionMessageIdText = PickLifecycleValue(actionMetadata, "DiscordMessageId", "ClickedDiscordMessageId");
            if (postedMessageId.HasValue && ulong.TryParse(actionMessageIdText, out var actionMessageId))
            {
                if (actionMessageId == postedMessageId.Value) return true;
                continue;
            }

            var actionRequiredEventIdText = PickLifecycleValue(actionMetadata, "RequiredEventId");
            if (postedRequiredEventId.HasValue && int.TryParse(actionRequiredEventIdText, out var actionRequiredEventId))
            {
                if (actionRequiredEventId == postedRequiredEventId.Value) return true;
                continue;
            }

            return true;
        }

        return false;
    }

    private static async Task<bool> HasWomOnlyActionForPostedEventAsync(
        TrackerDbContext db,
        LifecycleEvent postedEvent,
        CancellationToken ct)
    {
        var postedMessageId = ExtractUlong(postedEvent.MetadataJson, "DiscordMessageId");
        var postedRequiredEventId = ExtractInt(postedEvent.MetadataJson, "RequiredEventId");

        var actions = await db.LifecycleEvents
            .Where(x =>
                x.PlayerId == postedEvent.PlayerId &&
                x.EventType == "WOM_ONLY_ACTION_APPLIED" &&
                x.CreatedAt >= postedEvent.CreatedAt)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

        foreach (var action in actions)
        {
            var actionMetadata = ReadLifecycleMetadata(action.MetadataJson);
            var actionMessageIdText = PickLifecycleValue(actionMetadata, "DiscordMessageId", "ClickedDiscordMessageId");
            if (postedMessageId.HasValue && ulong.TryParse(actionMessageIdText, out var actionMessageId))
            {
                if (actionMessageId == postedMessageId.Value) return true;
                continue;
            }

            var actionRequiredEventIdText = PickLifecycleValue(actionMetadata, "RequiredEventId");
            if (postedRequiredEventId.HasValue && int.TryParse(actionRequiredEventIdText, out var actionRequiredEventId))
            {
                if (actionRequiredEventId == postedRequiredEventId.Value) return true;
                continue;
            }
        }

        return false;
    }

    private static async Task<bool> HasOpenWomOnlyRequirementForPostedEventAsync(
        TrackerDbContext db,
        LifecycleEvent postedEvent,
        int? requiredEventId,
        CancellationToken ct)
    {
        if (requiredEventId.HasValue)
        {
            return await db.LifecycleEvents.AnyAsync(x =>
                x.Id == requiredEventId.Value &&
                x.EventType == "WOM_ONLY_ACTION_REQUIRED" &&
                x.Status == "OPEN", ct);
        }

        var postedMetadata = ReadLifecycleMetadata(postedEvent.MetadataJson);
        var postedUsername = PickLifecycleValue(postedMetadata, "Username", "Player");
        if (string.IsNullOrWhiteSpace(postedUsername))
        {
            return false;
        }

        var normalizedPostedUsername = NormalizeUsername(postedUsername);
        var openRequired = await db.LifecycleEvents
            .Where(x => x.EventType == "WOM_ONLY_ACTION_REQUIRED" && x.Status == "OPEN")
            .ToListAsync(ct);
        foreach (var required in openRequired)
        {
            var metadata = ReadLifecycleMetadata(required.MetadataJson);
            var requiredUsername = PickLifecycleValue(metadata, "Username", "Player");
            if (string.IsNullOrWhiteSpace(requiredUsername))
            {
                continue;
            }

            if (string.Equals(
                NormalizeUsername(requiredUsername),
                normalizedPostedUsername,
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task EnsureMessageDeleteScheduledOrDeletedAsync(
        TrackerDbContext db,
        int preferredPlayerId,
        ulong channelId,
        ulong messageId,
        string eventType,
        object extraMetadata,
        DateTimeOffset messageCreatedAt,
        CancellationToken ct,
        bool extendExistingSchedule = false)
    {
        var messageIdText = messageId.ToString(CultureInfo.InvariantCulture);
        var deleteRows = await db.LifecycleEvents
            .Where(x =>
            (x.EventType == "PROMOTION_DISCORD_DELETE_SCHEDULED" ||
             x.EventType == "TEMPLE_MISSING_DISCORD_DELETE_SCHEDULED" ||
             x.EventType == "WOM_MISSING_DISCORD_DELETE_SCHEDULED" ||
             x.EventType == "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED") &&
            (x.Status == "OPEN" || x.Status == "DONE") &&
            x.MetadataJson.Contains(messageIdText))
            .ToListAsync(ct);
        var matchingDeleteRows = deleteRows.Where(x =>
            DiscordDeleteScheduleMatcher.MatchesOpenOrCompletedSchedule(x, messageId));
        var hasScheduledDelete = matchingDeleteRows.Any();
        var hasCompletedDelete = matchingDeleteRows.Any(x => x.Status == "DONE");

        if (!hasScheduledDelete)
        {
            var trackedDeleteRows = db.ChangeTracker
                .Entries<LifecycleEvent>()
                .Where(x =>
                    x.State != EntityState.Deleted &&
                    DiscordDeleteScheduleMatcher.MatchesOpenOrCompletedSchedule(x.Entity, messageId))
                .Select(x => x.Entity)
                .ToList();
            hasScheduledDelete = trackedDeleteRows.Count > 0;
            hasCompletedDelete = trackedDeleteRows.Any(x => x.Status == "DONE");
        }

        if (hasCompletedDelete || (hasScheduledDelete && !extendExistingSchedule))
        {
            logger.LogDebug(
                "Skipping Discord delete reconciliation for message {MessageId}; an open or completed delete schedule already exists.",
                messageId);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var preferredDeleteAt = messageCreatedAt.AddMinutes(_discordDeleteDelayMinutes);
        var hardDeleteAt = messageCreatedAt.AddMinutes(_discordDeleteHardCapMinutes);
        var dueAt = preferredDeleteAt <= hardDeleteAt ? preferredDeleteAt : hardDeleteAt;

        if (dueAt <= now)
        {
            var channel = await ResolveMessageChannelAsync(channelId);
            if (channel is not null)
            {
                try
                {
                    await channel.DeleteMessageAsync(messageId);
                    await RecordCompletedMessageDeleteAsync(
                        db,
                        preferredPlayerId,
                        channelId,
                        messageId,
                        eventType,
                        extraMetadata,
                        ct);
                    return;
                }
                catch (Discord.Net.HttpException ex) when (IsDiscordMessageMissingOrInaccessible(ex))
                {
                    await RecordCompletedMessageDeleteAsync(
                        db,
                        preferredPlayerId,
                        channelId,
                        messageId,
                        eventType,
                        extraMetadata,
                        ct);
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to immediately delete Discord message {MessageId} in channel {ChannelId}; scheduling retry.",
                        messageId,
                        channelId);
                    // fall through to schedule immediate retry
                }
            }
            else
            {
                logger.LogWarning(
                    "Unable to resolve Discord channel {ChannelId} for due message delete {MessageId}; scheduling retry.",
                    channelId,
                    messageId);
            }
        }

        var ownerId = await ResolveLifecycleOwnerPlayerIdAsync(db, preferredPlayerId, ct);
        if (!ownerId.HasValue) return;

        var scheduleDue = dueAt <= now ? now : dueAt;
        var scheduleHard = hardDeleteAt <= now ? now : hardDeleteAt;
        if (scheduleHard < scheduleDue) scheduleHard = scheduleDue;

        ScheduleChannelMessageDelete(
            db,
            ownerId.Value,
            channelId,
            messageId,
            eventType,
            extraMetadata,
            scheduleDue,
            scheduleHard,
            extendExistingSchedule: extendExistingSchedule);
    }

    private async Task RecordCompletedMessageDeleteAsync(
        TrackerDbContext db,
        int preferredPlayerId,
        ulong channelId,
        ulong messageId,
        string eventType,
        object extraMetadata,
        CancellationToken ct)
    {
        var ownerId = await ResolveLifecycleOwnerPlayerIdAsync(db, preferredPlayerId, ct);
        if (!ownerId.HasValue)
        {
            logger.LogWarning(
                "Unable to record completed Discord delete for message {MessageId}; no valid player row exists for lifecycle ownership.",
                messageId);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = ownerId.Value,
            EventType = eventType,
            MetadataJson = JsonUtil.Serialize(new
            {
                ChannelId = channelId,
                DiscordMessageId = messageId,
                DeleteAfterUtc = now,
                HardDeleteAfterUtc = now,
                Extra = extraMetadata
            }),
            Status = "DONE",
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task<int?> ResolveLifecycleOwnerPlayerIdAsync(TrackerDbContext db, int preferredPlayerId, CancellationToken ct)
    {
        var preferredExists = await db.Players.AnyAsync(x => x.Id == preferredPlayerId, ct);
        if (preferredExists) return preferredPlayerId;
        var fallback = await db.Players.OrderBy(x => x.Id).Select(x => (int?)x.Id).FirstOrDefaultAsync(ct);
        return fallback;
    }

    private async Task<int?> ResolveSlashCommandOwnerPlayerIdAsync(TrackerDbContext db, SocketSlashCommand command, CancellationToken ct)
    {
        foreach (var target in ExtractSlashCommandPlayerTargets(command.Data.Options))
        {
            var normalized = NormalizeUsername(target);
            if (string.IsNullOrWhiteSpace(normalized)) continue;
            var lowered = normalized.ToLowerInvariant();
            var playerId = await db.Players
                .Where(x => x.Username.ToLower() == lowered)
                .Select(x => (int?)x.Id)
                .FirstOrDefaultAsync(ct);
            if (playerId.HasValue) return playerId;
        }

        return await ResolveLifecycleOwnerPlayerIdAsync(db, 0, ct);
    }

    private static IReadOnlyList<string> ExtractSlashCommandPlayerTargets(IReadOnlyCollection<SocketSlashCommandDataOption> options)
    {
        var targets = new List<string>();
        foreach (var option in options)
        {
            AddSlashCommandPlayerTargets(targets, option);
        }

        return targets
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddSlashCommandPlayerTargets(List<string> targets, SocketSlashCommandDataOption option)
    {
        if (IsPlayerTargetOption(option.Name))
        {
            var raw = option.Value?.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                targets.AddRange(raw.Split(',')
                    .Select(x => x.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
            }
        }

        if (option.Options is null) return;
        foreach (var nested in option.Options)
        {
            AddSlashCommandPlayerTargets(targets, nested);
        }
    }

    private static bool IsPlayerTargetOption(string optionName)
    {
        return string.Equals(optionName, "player", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(optionName, "players", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(optionName, "username", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ScheduleInteractionResponseDeleteAsync(SocketSlashCommand command, string? messageDescription = null)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var ownerId = await ResolveLifecycleOwnerPlayerIdAsync(db, 0, CancellationToken.None);
        if (!ownerId.HasValue)
        {
            logger.LogWarning("Unable to schedule interaction-response delete for command {CommandName}; no valid player row exists for lifecycle ownership.", command.CommandName);
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var deleteAfter = now.AddMinutes(Math.Min(_discordDeleteDelayMinutes, _discordDeleteHardCapMinutes));
        var hardDeleteAfter = now.AddMinutes(_discordDeleteHardCapMinutes);
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = ownerId.Value,
            EventType = "DISCORD_INTERACTION_RESPONSE_DELETE_SCHEDULED",
            MetadataJson = JsonUtil.Serialize(new
            {
                InteractionId = command.Id,
                ApplicationId = command.ApplicationId,
                InteractionToken = command.Token,
                DeleteAfterUtc = deleteAfter,
                HardDeleteAfterUtc = hardDeleteAfter,
                Extra = new
                {
                    Reason = $"slash-{command.CommandName}-interaction-response",
                    MessageDescription = BuildInteractionCleanupDescription(command, messageDescription)
                }
            }),
            Status = "OPEN",
            CreatedAt = now
        });
        await db.SaveChangesAsync();
    }

    private async Task ScheduleInteractionFollowupDeleteAsync(SocketMessageComponent component, ulong messageId, string? messageDescription = null)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var ownerId = await ResolveLifecycleOwnerPlayerIdAsync(db, 0, CancellationToken.None);
        if (!ownerId.HasValue) return;

        var now = DateTimeOffset.UtcNow;
        var deleteAfter = now.AddMinutes(Math.Min(_discordDeleteDelayMinutes, _discordDeleteHardCapMinutes));
        var hardDeleteAfter = now.AddMinutes(_discordDeleteHardCapMinutes);

        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = ownerId.Value,
            EventType = "DISCORD_INTERACTION_FOLLOWUP_DELETE_SCHEDULED",
            MetadataJson = JsonUtil.Serialize(new
            {
                InteractionId = component.Id,
                ApplicationId = component.ApplicationId,
                InteractionToken = component.Token,
                FollowupMessageId = messageId,
                DeleteAfterUtc = deleteAfter,
                HardDeleteAfterUtc = hardDeleteAfter,
                Extra = new
                {
                    Reason = "component-ephemeral-followup",
                    MessageDescription = NormalizeCleanupDescription(messageDescription, "component ephemeral followup")
                }
            }),
            Status = "OPEN",
            CreatedAt = now
        });
        await db.SaveChangesAsync();
    }

    private async Task ScheduleChannelResponseDeleteAsync(ulong channelId, ulong messageId, string reason, string? messageDescription = null)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var hasScheduledDelete = await db.LifecycleEvents.AnyAsync(x =>
            (x.EventType == "PROMOTION_DISCORD_DELETE_SCHEDULED" ||
             x.EventType == "TEMPLE_MISSING_DISCORD_DELETE_SCHEDULED" ||
             x.EventType == "WOM_MISSING_DISCORD_DELETE_SCHEDULED" ||
             x.EventType == "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED") &&
            x.Status == "OPEN");
        if (hasScheduledDelete)
        {
            var scheduledRows = await db.LifecycleEvents
                .Where(x =>
                    (x.EventType == "PROMOTION_DISCORD_DELETE_SCHEDULED" ||
                     x.EventType == "TEMPLE_MISSING_DISCORD_DELETE_SCHEDULED" ||
                     x.EventType == "WOM_MISSING_DISCORD_DELETE_SCHEDULED" ||
                     x.EventType == "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED") &&
                    x.Status == "OPEN")
                .ToListAsync();
            hasScheduledDelete = scheduledRows.Any(x => MetadataUlongEquals(x.MetadataJson, "DiscordMessageId", messageId));
        }
        if (hasScheduledDelete) return;

        var ownerId = await ResolveLifecycleOwnerPlayerIdAsync(db, 0, CancellationToken.None);
        if (!ownerId.HasValue)
        {
            logger.LogWarning("Unable to schedule channel-response delete for message {MessageId}; no valid player row exists for lifecycle ownership.", messageId);
            return;
        }

        ScheduleChannelMessageDelete(
            db,
            ownerId.Value,
            channelId,
            messageId,
            "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED",
            new
            {
                Reason = reason,
                MessageDescription = NormalizeCleanupDescription(messageDescription, reason)
            });
        await db.SaveChangesAsync();
    }

    private async Task ProcessScheduledDeletes(CancellationToken ct)
    {
        if (_client is null) return;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var now = DateTimeOffset.UtcNow;

        var scheduled = await db.LifecycleEvents
            .Where(x =>
                (x.EventType == "PROMOTION_DISCORD_DELETE_SCHEDULED" ||
                 x.EventType == "TEMPLE_MISSING_DISCORD_DELETE_SCHEDULED" ||
                 x.EventType == "WOM_MISSING_DISCORD_DELETE_SCHEDULED" ||
                 x.EventType == "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED" ||
                 x.EventType == "DISCORD_INTERACTION_RESPONSE_DELETE_SCHEDULED" ||
                 x.EventType == "DISCORD_INTERACTION_FOLLOWUP_DELETE_SCHEDULED") &&
                x.Status == "OPEN")
            .ToListAsync(ct);
        if (scheduled.Count == 0) return;

        foreach (var s in scheduled)
        {
            using var sd = JsonDocument.Parse(s.MetadataJson);
            if (!TryReadDateTimeOffset(sd.RootElement, "DeleteAfterUtc", out var due))
            {
                due = DateTimeOffset.MinValue;
            }
            if (TryReadDateTimeOffset(sd.RootElement, "HardDeleteAfterUtc", out var hardCap) && hardCap < due)
            {
                due = hardCap;
            }
            if (due > now) continue;

            if (s.EventType == "DISCORD_INTERACTION_RESPONSE_DELETE_SCHEDULED")
            {
                if (!TryReadUlong(sd.RootElement, "ApplicationId", out var appId) ||
                    !sd.RootElement.TryGetProperty("InteractionToken", out var tokenProp))
                {
                    s.Status = "DONE";
                    continue;
                }

                var token = tokenProp.GetString();
                if (string.IsNullOrWhiteSpace(token))
                {
                    s.Status = "DONE";
                    continue;
                }

                try
                {
                    var client = httpClientFactory.CreateClient();
                    var url = $"https://discord.com/api/v10/webhooks/{appId}/{token}/messages/@original";
                    using var req = new HttpRequestMessage(HttpMethod.Delete, url);
                    using var resp = await client.SendAsync(req, ct);
                    if (resp.IsSuccessStatusCode ||
                        resp.StatusCode == System.Net.HttpStatusCode.NotFound ||
                        resp.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                        resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        s.Status = "DONE";
                    }
                }
                catch
                {
                    logger.LogWarning(
                        "Scheduled Discord interaction delete {LifecycleEventId} is due but failed; it will be retried.",
                        s.Id);
                    // keep OPEN for retry on transient network failures
                }
                continue;
            }

            if (!TryReadUlong(sd.RootElement, "ChannelId", out var channelId) ||
                !TryReadUlong(sd.RootElement, "DiscordMessageId", out var messageId))
            {
                s.Status = "DONE";
                continue;
            }
            if (s.EventType == "DISCORD_INTERACTION_FOLLOWUP_DELETE_SCHEDULED")
            {
                if (!TryReadUlong(sd.RootElement, "ApplicationId", out var appId) ||
                    !sd.RootElement.TryGetProperty("InteractionToken", out var tokenProp) ||
                    !TryReadUlong(sd.RootElement, "FollowupMessageId", out var followupMessageId))
                {
                    s.Status = "DONE";
                    continue;
                }

                var token = tokenProp.GetString();
                if (string.IsNullOrWhiteSpace(token))
                {
                    s.Status = "DONE";
                    continue;
                }

                try
                {
                    var client = httpClientFactory.CreateClient();
                    var url = $"https://discord.com/api/v10/webhooks/{appId}/{token}/messages/{followupMessageId}";
                    using var req = new HttpRequestMessage(HttpMethod.Delete, url);
                    using var resp = await client.SendAsync(req, ct);
                    if (resp.IsSuccessStatusCode ||
                        resp.StatusCode == System.Net.HttpStatusCode.NotFound ||
                        resp.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                        resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        s.Status = "DONE";
                    }
                }
                catch
                {
                    logger.LogWarning("Scheduled Discord followup delete {LifecycleEventId} is due but failed; retrying.", s.Id);
                }
                continue;
            }

            if (s.EventType == "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED" &&
                RequiresWomOnlyActionForCleanup(sd.RootElement) &&
                !await HasWomOnlyActionForScheduledDeleteAsync(db, s, ct))
            {
                logger.LogWarning(
                    "Skipping WOM-only delete schedule {LifecycleEventId} for message {MessageId} because no WOM_ONLY_ACTION_APPLIED event was found.",
                    s.Id,
                    messageId);
                s.Status = "DONE";
                continue;
            }

            var channel = await ResolveMessageChannelAsync(channelId);
            if (channel is null)
            {
                logger.LogWarning(
                    "Scheduled Discord delete {LifecycleEventId} for message {MessageId} in channel {ChannelId} is due but the channel could not be resolved.",
                    s.Id,
                    messageId,
                    channelId);
                // keep OPEN so a later cycle can retry if cache/rest resolution failed transiently
                continue;
            }

            try
            {
                await channel.DeleteMessageAsync(messageId);
                s.Status = "DONE";
            }
            catch (Discord.Net.HttpException ex) when (IsDiscordMessageMissingOrInaccessible(ex))
            {
                // Already gone or inaccessible: treat as complete.
                s.Status = "DONE";
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Scheduled Discord delete {LifecycleEventId} for message {MessageId} in channel {ChannelId} failed; it will be retried.",
                    s.Id,
                    messageId,
                    channelId);
                // keep OPEN for retry on transient failures
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task ReconcileCompletedMessageDeletes(CancellationToken ct)
    {
        if (_client is null) return;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();

        await ReconcileSupersededMergeCardsAsync(db, ct);

        var promotionPosted = await db.LifecycleEvents
            .Where(x => x.EventType == "PROMOTION_DISCORD_POSTED")
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
        var promotionActions = await db.LifecycleEvents
            .Where(x => x.EventType == "PROMOTION_DISCORD_ACTION_APPLIED")
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
        foreach (var ev in promotionPosted)
        {
            try
            {
                using var doc = JsonDocument.Parse(ev.MetadataJson);
                if (!doc.RootElement.TryGetProperty("CandidateId", out var candProp)) continue;
                var candidateId = candProp.GetInt32();
                if (!TryReadUlong(doc.RootElement, "ChannelId", out var chId) ||
                    !TryReadUlong(doc.RootElement, "DiscordMessageId", out var msgId)) continue;

                var candidate = await db.PromotionCandidates.FirstOrDefaultAsync(x => x.Id == candidateId, ct);
                if (candidate is null || candidate.Status != PromotionStatus.PENDING)
                {
                    var handledEvent = promotionActions.FirstOrDefault(x =>
                        x.PlayerId == ev.PlayerId &&
                        MetadataIntEquals(x.MetadataJson, "CandidateId", candidateId));
                    var messageCreatedAt = handledEvent?.CreatedAt ?? ev.CreatedAt;

                    await EnsureMessageDeleteScheduledOrDeletedAsync(
                        db,
                        ev.PlayerId,
                        chId,
                        msgId,
                        "PROMOTION_DISCORD_DELETE_SCHEDULED",
                        new { CandidateId = candidateId, Reason = "reconcile-completed-promotion" },
                        messageCreatedAt,
                        ct,
                        extendExistingSchedule: true);
                }
            }
            catch
            {
                // ignore malformed old metadata
            }
        }

        var templePosted = await db.LifecycleEvents
            .Where(x => x.EventType == "TEMPLE_MISSING_DISCORD_POSTED")
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
        foreach (var ev in templePosted)
        {
            try
            {
                using var doc = JsonDocument.Parse(ev.MetadataJson);
                if (!TryReadUlong(doc.RootElement, "ChannelId", out var chId) ||
                    !TryReadUlong(doc.RootElement, "DiscordMessageId", out var msgId)) continue;

                var player = await db.Players.FirstOrDefaultAsync(x => x.Id == ev.PlayerId, ct);
                var stillNeedsAction = player is not null && player.Status == PlayerStatus.MISSING_PENDING_REVIEW;
                if (stillNeedsAction) continue;

                await EnsureMessageDeleteScheduledOrDeletedAsync(
                    db,
                    ev.PlayerId,
                    chId,
                    msgId,
                    "TEMPLE_MISSING_DISCORD_DELETE_SCHEDULED",
                    new { Reason = "reconcile-completed-temple-missing" },
                    ev.CreatedAt,
                    ct);
            }
            catch
            {
                // ignore malformed old metadata
            }
        }

        var womMissingPosted = await db.LifecycleEvents
            .Where(x => x.EventType == "WOM_MISSING_DISCORD_POSTED")
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
        foreach (var ev in womMissingPosted)
        {
            try
            {
                using var doc = JsonDocument.Parse(ev.MetadataJson);
                if (!TryReadUlong(doc.RootElement, "ChannelId", out var chId) ||
                    !TryReadUlong(doc.RootElement, "DiscordMessageId", out var msgId)) continue;

                var hasOpenRequired = await db.LifecycleEvents.AnyAsync(x =>
                    x.PlayerId == ev.PlayerId &&
                    x.EventType == "WOM_MISSING_ACTION_REQUIRED" &&
                    x.Status == "OPEN", ct);
                if (hasOpenRequired) continue;

                await EnsureMessageDeleteScheduledOrDeletedAsync(
                    db,
                    ev.PlayerId,
                    chId,
                    msgId,
                    "WOM_MISSING_DISCORD_DELETE_SCHEDULED",
                    new { Reason = "reconcile-completed-wom-missing" },
                    ev.CreatedAt,
                    ct);
            }
            catch
            {
                // ignore malformed old metadata
            }
        }

        var womOnlyPosted = await db.LifecycleEvents
            .Where(x => x.EventType == "WOM_ONLY_DISCORD_POSTED")
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
        var latestWomOnlyPostIds = womOnlyPosted
            .Select(x => new { EventId = x.Id, RequiredEventId = ExtractInt(x.MetadataJson, "RequiredEventId") })
            .Where(x => x.RequiredEventId.HasValue)
            .GroupBy(x => x.RequiredEventId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.EventId).First().EventId);
        foreach (var ev in womOnlyPosted)
        {
            try
            {
                using var doc = JsonDocument.Parse(ev.MetadataJson);
                if (!TryReadUlong(doc.RootElement, "ChannelId", out var chId) ||
                    !TryReadUlong(doc.RootElement, "DiscordMessageId", out var msgId))
                {
                    continue;
                }

                var requiredEventId = ExtractInt(ev.MetadataJson, "RequiredEventId");
                var isLatestForRequirement = requiredEventId.HasValue &&
                    latestWomOnlyPostIds.TryGetValue(requiredEventId.Value, out var latestPostedId) &&
                    latestPostedId == ev.Id;
                var hasActionApplied = await HasWomOnlyActionForPostedEventAsync(db, ev, ct);
                var hasOpenRequired = await HasOpenWomOnlyRequirementForPostedEventAsync(db, ev, requiredEventId, ct);
                if (!hasActionApplied && hasOpenRequired && isLatestForRequirement)
                {
                    continue;
                }

                var postedMetadata = ReadLifecycleMetadata(ev.MetadataJson);
                var username = PickLifecycleValue(postedMetadata, "Username", "Player") ?? "player";
                var reason = !hasOpenRequired
                    ? "wom-only-requirement-resolved"
                    : isLatestForRequirement
                        ? "wom-only-action-handled"
                        : "wom-only-action-handled-duplicate";

                await EnsureMessageDeleteScheduledOrDeletedAsync(
                    db,
                    ev.PlayerId,
                    chId,
                    msgId,
                    "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED",
                    new { Reason = reason, MessageDescription = $"WOM-only review card for {username}" },
                    ev.CreatedAt,
                    ct);
            }
            catch
            {
                // ignore malformed old metadata
            }
        }

        var womRankMismatchPosted = await db.LifecycleEvents
            .Where(x => x.EventType == "WOM_RANK_MISMATCH_DISCORD_POSTED")
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
        var latestWomRankMismatchPostIds = womRankMismatchPosted
            .GroupBy(x => x.PlayerId)
            .Select(x => x.OrderByDescending(ev => ev.CreatedAt).First().Id)
            .ToHashSet();
        foreach (var ev in womRankMismatchPosted)
        {
            try
            {
                using var doc = JsonDocument.Parse(ev.MetadataJson);
                if (!TryReadUlong(doc.RootElement, "ChannelId", out var chId) ||
                    !TryReadUlong(doc.RootElement, "DiscordMessageId", out var msgId)) continue;

                var player = await db.Players.FirstOrDefaultAsync(x => x.Id == ev.PlayerId, ct);
                if (!latestWomRankMismatchPostIds.Contains(ev.Id))
                {
                    await EnsureMessageDeleteScheduledOrDeletedAsync(
                        db,
                        ev.PlayerId,
                        chId,
                        msgId,
                        "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED",
                        new
                        {
                            Reason = "wom-rank-mismatch-duplicate",
                            MessageDescription = $"WOM rank mismatch alert for {player?.Username ?? "player"}"
                        },
                        ev.CreatedAt,
                        ct);
                    continue;
                }

                var hasOpenMismatch = await db.LifecycleEvents.AnyAsync(x =>
                    x.PlayerId == ev.PlayerId &&
                    x.EventType == "WOM_RANK_MISMATCH_REQUIRED" &&
                    x.Status == "OPEN", ct);
                var isIgnored = await db.LifecycleEvents.AnyAsync(x =>
                    x.PlayerId == ev.PlayerId &&
                    x.EventType == "WOM_RANK_MISMATCH_IGNORED" &&
                    x.Status == "OPEN", ct);
                if (hasOpenMismatch && !isIgnored && player is not null) continue;

                await EnsureMessageDeleteScheduledOrDeletedAsync(
                    db,
                    ev.PlayerId,
                    chId,
                    msgId,
                    "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED",
                    new { Reason = "wom-rank-mismatch-resolved", MessageDescription = "WOM rank mismatch alert" },
                    ev.CreatedAt,
                    ct);
            }
            catch
            {
                // ignore malformed old metadata
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task DeletePostedMessageIfFoundAsync(ulong channelId, ulong messageId)
    {
        var channel = await ResolveMessageChannelAsync(channelId);
        if (channel is null) return;

        try
        {
            var message = await channel.GetMessageAsync(messageId);
            if (message is IUserMessage userMessage)
            {
                await userMessage.DeleteAsync();
            }
        }
        catch (Discord.Net.HttpException ex) when (IsDiscordMessageMissingOrInaccessible(ex))
        {
            // Missing or inaccessible historical messages do not need cleanup rows.
        }
    }

    private async Task UpdatePetHiscoresMessages(CancellationToken ct)
    {
        if (_client is null) return;
        if (_options.PetHiscoresChannelId == 0)
        {
            return;
        }

        var channel = await ResolveMessageChannelAsync(_options.PetHiscoresChannelId);
        if (channel is null) return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var ownerId = await db.Players
            .OrderBy(x => x.Id)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(ct);
        if (!ownerId.HasValue)
        {
            logger.LogInformation("Skipping pet hiscore update because there are no players in database.");
            return;
        }

        var postedEvents = await db.LifecycleEvents
            .Where(x => x.EventType == "PET_HISCORES_DISCORD_POSTED")
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);
        var postedForChannel = postedEvents
            .Where(x => ExtractUlong(x.MetadataJson, "ChannelId") == _options.PetHiscoresChannelId)
            .ToList();

        var bannerState = await GetPetHiscoresBannerStateAsync(db, channel, ct);
        if (bannerState == TrackedMessageState.Missing && postedForChannel.Count > 0)
        {
            foreach (var ev in postedForChannel)
            {
                var msgId = ExtractUlong(ev.MetadataJson, "DiscordMessageId");
                if (msgId.HasValue)
                {
                    try
                    {
                        var existing = await channel.GetMessageAsync(msgId.Value);
                        if (existing is IUserMessage userMessage)
                        {
                            await userMessage.DeleteAsync();
                        }
                    }
                    catch
                    {
                        // ignore and continue cleanup
                    }
                }
            }

            db.LifecycleEvents.RemoveRange(postedForChannel);
            await db.SaveChangesAsync(ct);
            postedForChannel = [];
        }
        else if (bannerState == TrackedMessageState.Unknown)
        {
            logger.LogWarning("Pet hiscore banner state could not be resolved for channel {ChannelId}; skipping cleanup/recreate to avoid duplicates.", _options.PetHiscoresChannelId);
        }

        await EnsurePetHiscoresBannerMessageAsync(db, channel, ownerId.Value, ct);

        var rowsRaw = await db.Players
            .Select(x => new
            {
                x.Username,
                x.StoredPetCount,
                x.ManualPetOverride
            })
            .ToListAsync(ct);

        var rows = rowsRaw
            .Select(x => new
            {
                x.Username,
                Pets = Math.Max(x.StoredPetCount, x.ManualPetOverride ?? 0)
            })
            .Where(x => x.Pets >= 10)
            .OrderByDescending(x => x.Pets)
            .ThenBy(x => x.Username)
            .ToList();

        var pages = BuildPetHiscorePages(rows.Select((x, i) => (Rank: i + 1, x.Username, x.Pets)).ToList());
        var pageFingerprints = pages
            .Select(page => ComputeRenderFingerprint(new { Content = page, Flags = MessageFlags.SuppressEmbeds.ToString() }))
            .ToList();

        if (postedForChannel.Count == 0)
        {
            var ownerPlayerId = await db.Players
                .OrderBy(x => x.Id)
                .Select(x => (int?)x.Id)
                .FirstOrDefaultAsync(ct);
            if (!ownerPlayerId.HasValue)
            {
                logger.LogInformation("Skipping pet hiscore message bootstrap because no players exist yet.");
                return;
            }

            for (var i = 0; i < pages.Count; i++)
            {
                var msg = await channel.SendMessageAsync(pages[i], flags: MessageFlags.SuppressEmbeds);
                db.LifecycleEvents.Add(new LifecycleEvent
                {
                    PlayerId = ownerPlayerId.Value,
                    EventType = "PET_HISCORES_DISCORD_POSTED",
                    MetadataJson = JsonUtil.Serialize(new
                    {
                        ChannelId = _options.PetHiscoresChannelId,
                        DiscordMessageId = msg.Id,
                        Page = i,
                        RenderFingerprint = pageFingerprints[i]
                    }),
                    Status = "DONE",
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
            await db.SaveChangesAsync(ct);
            return;
        }

        var mapped = postedForChannel
            .Select(x => new
            {
                Event = x,
                Page = ExtractInt(x.MetadataJson, "Page") ?? 0,
                MessageId = ExtractUlong(x.MetadataJson, "DiscordMessageId"),
                RenderFingerprint = ExtractString(x.MetadataJson, "RenderFingerprint")
            })
            .Where(x => x.MessageId.HasValue)
            .GroupBy(x => x.Page)
            .Select(g => g.OrderByDescending(x => x.Event.CreatedAt).First())
            .OrderBy(x => x.Page)
            .ToList();

        if (mapped.Count == 0)
        {
            for (var i = 0; i < pages.Count; i++)
            {
                var msg = await channel.SendMessageAsync(pages[i], flags: MessageFlags.SuppressEmbeds);
                db.LifecycleEvents.Add(new LifecycleEvent
                {
                    PlayerId = ownerId.Value,
                    EventType = "PET_HISCORES_DISCORD_POSTED",
                    MetadataJson = JsonUtil.Serialize(new
                    {
                        ChannelId = _options.PetHiscoresChannelId,
                        DiscordMessageId = msg.Id,
                        Page = i,
                        RenderFingerprint = pageFingerprints[i]
                    }),
                    Status = "DONE",
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
            await db.SaveChangesAsync(ct);
            return;
        }

        for (var i = 0; i < pages.Count; i++)
        {
            var existing = mapped.FirstOrDefault(x => x.Page == i);
            var pageFingerprint = pageFingerprints[i];
            if (existing is not null && existing.MessageId.HasValue)
            {
                if (string.Equals(existing.RenderFingerprint, pageFingerprint, StringComparison.Ordinal))
                {
                    continue;
                }

                var (messageState, userMessage) = await TryGetTrackedUserMessageAsync(channel, existing.MessageId.Value);
                if (messageState == TrackedMessageState.Found && userMessage is not null)
                {
                    await userMessage.ModifyAsync(p =>
                    {
                        p.Content = pages[i];
                        p.Flags = MessageFlags.SuppressEmbeds;
                    });
                    existing.Event.MetadataJson = JsonUtil.Serialize(new
                    {
                        ChannelId = _options.PetHiscoresChannelId,
                        DiscordMessageId = userMessage.Id,
                        Page = i,
                        RenderFingerprint = pageFingerprint
                    });
                    continue;
                }
                if (messageState == TrackedMessageState.Unknown)
                {
                    logger.LogWarning("Pet hiscore page message lookup was inconclusive for page {Page} in channel {ChannelId}; skipping recreate this cycle to avoid duplicates.", i, _options.PetHiscoresChannelId);
                    continue;
                }
            }

            var newMsg = await channel.SendMessageAsync(pages[i], flags: MessageFlags.SuppressEmbeds);
            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = ownerId.Value,
                EventType = "PET_HISCORES_DISCORD_POSTED",
                MetadataJson = JsonUtil.Serialize(new
                {
                    ChannelId = _options.PetHiscoresChannelId,
                    DiscordMessageId = newMsg.Id,
                    Page = i,
                    RenderFingerprint = pageFingerprint
                }),
                Status = "DONE",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        if (mapped.Count > pages.Count)
        {
            const string emptyPageContent = "Pet Hiscores\n\nNo entries for this page.";
            var emptyPageFingerprint = ComputeRenderFingerprint(new { Content = emptyPageContent, Flags = MessageFlags.SuppressEmbeds.ToString() });
            for (var i = pages.Count; i < mapped.Count; i++)
            {
                if (string.Equals(mapped[i].RenderFingerprint, emptyPageFingerprint, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    var msg = await channel.GetMessageAsync(mapped[i].MessageId!.Value);
                    if (msg is IUserMessage userMessage)
                    {
                        await userMessage.ModifyAsync(p =>
                        {
                            p.Content = emptyPageContent;
                            p.Flags = MessageFlags.SuppressEmbeds;
                        });
                        mapped[i].Event.MetadataJson = JsonUtil.Serialize(new
                        {
                            ChannelId = _options.PetHiscoresChannelId,
                            DiscordMessageId = userMessage.Id,
                            Page = mapped[i].Page,
                            RenderFingerprint = emptyPageFingerprint
                        });
                    }
                }
                catch { }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<TrackedMessageState> GetPetHiscoresBannerStateAsync(TrackerDbContext db, IMessageChannel channel, CancellationToken ct)
    {
        var bannerEvent = await db.LifecycleEvents
            .Where(x => x.EventType == "PET_HISCORES_BANNER_POSTED")
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (bannerEvent is null) return TrackedMessageState.Missing;

        var trackedChannel = ExtractUlong(bannerEvent.MetadataJson, "ChannelId");
        var trackedMessage = ExtractUlong(bannerEvent.MetadataJson, "DiscordMessageId");
        if (trackedChannel != _options.PetHiscoresChannelId || !trackedMessage.HasValue) return TrackedMessageState.Missing;
        var (state, _) = await TryGetTrackedUserMessageAsync(channel, trackedMessage.Value);
        return state;
    }

    private async Task EnsurePetHiscoresBannerMessageAsync(
        TrackerDbContext db,
        IMessageChannel channel,
        int ownerPlayerId,
        CancellationToken ct)
    {
        var bannerEvent = await db.LifecycleEvents
            .Where(x => x.EventType == "PET_HISCORES_BANNER_POSTED")
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (bannerEvent is not null)
        {
            var trackedChannel = ExtractUlong(bannerEvent.MetadataJson, "ChannelId");
            var trackedMessage = ExtractUlong(bannerEvent.MetadataJson, "DiscordMessageId");
            if (trackedChannel == _options.PetHiscoresChannelId && trackedMessage.HasValue)
            {
                var (state, _) = await TryGetTrackedUserMessageAsync(channel, trackedMessage.Value);
                if (state == TrackedMessageState.Found)
                {
                    return;
                }
                if (state == TrackedMessageState.Unknown)
                {
                    logger.LogWarning("Pet hiscore banner lookup was inconclusive for channel {ChannelId}; skipping banner recreate this cycle to avoid duplicates.", _options.PetHiscoresChannelId);
                    return;
                }
            }
        }

        var bannerPath = Path.Combine(AppContext.BaseDirectory, "Assets", "catch_em_all_banner.png");
        if (!File.Exists(bannerPath))
        {
            logger.LogWarning("Pet hiscore banner file not found at {Path}", bannerPath);
            return;
        }

        var posted = await channel.SendFileAsync(bannerPath, text: "");
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = ownerPlayerId,
            EventType = "PET_HISCORES_BANNER_POSTED",
            MetadataJson = JsonUtil.Serialize(new
            {
                ChannelId = _options.PetHiscoresChannelId,
                DiscordMessageId = posted.Id
            }),
            Status = "DONE",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task<(TrackedMessageState State, IUserMessage? Message)> TryGetTrackedUserMessageAsync(IMessageChannel channel, ulong messageId)
    {
        try
        {
            var msg = await channel.GetMessageAsync(messageId);
            if (msg is IUserMessage userMessage) return (TrackedMessageState.Found, userMessage);
            return (TrackedMessageState.Missing, null);
        }
        catch (Discord.Net.HttpException ex) when (IsDiscordMessageMissingOrInaccessible(ex))
        {
            return (TrackedMessageState.Missing, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve tracked Discord message {MessageId} in channel {ChannelId}", messageId, _options.PetHiscoresChannelId);
            return (TrackedMessageState.Unknown, null);
        }
    }

    private List<string> BuildPetHiscorePages(List<(int Rank, string Username, int Pets)> entries)
    {
        var pages = new List<string>();
        var intro =
            "Samtliga medlemmar i Swedes med 10+ pets har möjligheten att bli addade till ⁠pet-hiscores. " +
            "Ladda ner pluginen **\"TempleOSRS\"**, kryssa i **\"Collection Log Update Button\"** och **\"Automatically Sync Collection Log\"** - gå sedan in på er collection log och klicka på **\"Temple\"** i det övre högra hörnet. " +
            "Om ni behöver hjälp med detta så pma <@214909384617099264> eller <@193851480422219777>. " +
            "Om du har 30+ pets så har du chansen att få dina pets tillagda på din Templeprofil via Petcord <https://discord.gg/petcord>, Alice (sugarbunny.) är den som lägger till på din profil.\n\n**Pet Leaderboards**\n";
        const string continuedHeader = "";
        if (entries.Count == 0)
        {
            pages.Add($"{intro}> Inga spelare med 10+ pets just nu.");
            return pages;
        }

        const int maxChars = 1800;
        var current = new StringBuilder(intro);
        foreach (var e in entries)
        {
            var prefix = e.Rank switch
            {
                1 => "🥇 ",
                2 => "🥈 ",
                3 => "🥉 ",
                _ => $"{e.Rank}. "
            };
            var line = $"> {prefix}{e.Username} - {e.Pets}\n";
            if (current.Length + line.Length > maxChars)
            {
                pages.Add(current.ToString().TrimEnd());
                current.Clear();
                current.Append(continuedHeader);
            }
            current.Append(line);
        }

        if (current.Length > 0)
        {
            pages.Add(current.ToString().TrimEnd());
        }

        return pages;
    }

    private static int? ExtractInt(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(property, out var prop)) return null;
            return prop.GetInt32();
        }
        catch
        {
            return null;
        }
    }

    private static List<int> ExtractIntArray(string json, string property)
    {
        var values = new List<int>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(property, out var prop) || prop.ValueKind != JsonValueKind.Array)
            {
                return values;
            }

            foreach (var item in prop.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n))
                {
                    values.Add(n);
                    continue;
                }

                if (item.ValueKind == JsonValueKind.String && int.TryParse(item.GetString(), out var parsed))
                {
                    values.Add(parsed);
                }
            }
        }
        catch
        {
            // best effort
        }

        return values.Distinct().OrderBy(x => x).ToList();
    }

    private static string? ExtractString(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(property, out var prop)) return null;
            return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static ulong? ExtractUlong(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(property, out var prop)) return null;
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetUInt64(out var n)) return n;
            if (prop.ValueKind == JsonValueKind.String && ulong.TryParse(prop.GetString(), out var s)) return s;
            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task ReconcileOrphanTrackerCards(CancellationToken ct)
    {
        if (_client is null) return;
        var channel = await ResolveMessageChannelAsync(_options.ChannelId);
        if (channel is null) return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();

        var recentLifecycleEvents = await db.LifecycleEvents
            .Where(x =>
                x.EventType.EndsWith("_DISCORD_POSTED") ||
                x.EventType.EndsWith("_DISCORD_DELETE_SCHEDULED") ||
                x.EventType == "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED")
            .OrderByDescending(x => x.CreatedAt)
            .Take(5000)
            .ToListAsync(ct);

        var trackedMessageIds = new HashSet<ulong>();
        foreach (var row in recentLifecycleEvents)
        {
            var msgId = ExtractUlong(row.MetadataJson, "DiscordMessageId");
            if (msgId.HasValue) trackedMessageIds.Add(msgId.Value);
            var followupId = ExtractUlong(row.MetadataJson, "FollowupMessageId");
            if (followupId.HasValue) trackedMessageIds.Add(followupId.Value);
        }

        var graceMinutes = Math.Max(2, configuration.GetValue<int?>("Tracker:DiscordOrphanGraceMinutes") ?? 5);
        var olderThan = DateTimeOffset.UtcNow.AddMinutes(-graceMinutes);

        const int scanLimit = 200;
        var recentMessages = await channel.GetMessagesAsync(limit: scanLimit).FlattenAsync();
        foreach (var message in recentMessages)
        {
            if (ct.IsCancellationRequested) break;
            if (message is not IUserMessage userMessage) continue;
            if (_client.CurrentUser is null || userMessage.Author.Id != _client.CurrentUser.Id) continue;
            if (userMessage.Timestamp >= olderThan) continue;
            if (!IsTrackerCardMessage(userMessage)) continue;
            if (trackedMessageIds.Contains(userMessage.Id)) continue;

            try
            {
                await channel.DeleteMessageAsync(userMessage.Id);
                logger.LogInformation(
                    "Deleted orphan tracker card message {MessageId} in channel {ChannelId}.",
                    userMessage.Id,
                    channel.Id);
            }
            catch (Discord.Net.HttpException ex) when (IsDiscordMessageMissingOrInaccessible(ex))
            {
                // already gone or inaccessible
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed deleting orphan tracker card message {MessageId} in channel {ChannelId}.",
                    userMessage.Id,
                    channel.Id);
            }
        }
    }

    private static bool IsTrackerCardMessage(IUserMessage userMessage)
    {
        var components = userMessage.Components;
        if (components is null || components.Count == 0) return false;

        foreach (var component in components)
        {
            var customId = component switch
            {
                ButtonComponent b => b.CustomId,
                SelectMenuComponent s => s.CustomId,
                _ => null
            };
            if (string.IsNullOrWhiteSpace(customId)) continue;

            if (customId.StartsWith("promo:", StringComparison.OrdinalIgnoreCase) ||
                customId.StartsWith("missing:", StringComparison.OrdinalIgnoreCase) ||
                customId.StartsWith("wommissing:", StringComparison.OrdinalIgnoreCase) ||
                customId.StartsWith("womonly:", StringComparison.OrdinalIgnoreCase) ||
                customId.StartsWith("womrank:", StringComparison.OrdinalIgnoreCase) ||
                customId.StartsWith("merge:", StringComparison.OrdinalIgnoreCase) ||
                customId.StartsWith("mergepick:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasPromotionActionButtons(IUserMessage userMessage)
    {
        var components = userMessage.Components;
        if (components is null || components.Count == 0) return false;

        foreach (var component in components)
        {
            var customId = component switch
            {
                ButtonComponent b => b.CustomId,
                SelectMenuComponent s => s.CustomId,
                _ => null
            };
            if (string.IsNullOrWhiteSpace(customId)) continue;
            if (customId.StartsWith("promo:", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private async Task<PromotionStatus?> GetCurrentPromotionCandidateStatusAsync(TrackerDbContext db, int candidateId, CancellationToken ct)
    {
        return await db.PromotionCandidates
            .Where(x => x.Id == candidateId)
            .Select(x => (PromotionStatus?)x.Status)
            .FirstOrDefaultAsync(ct);
    }

    private async Task DismissPromotionCandidateAlreadyCurrentRankAsync(
        TrackerDbContext db,
        int playerId,
        int candidateId,
        string playerName,
        string currentRank,
        string candidateNewRank,
        string source,
        CancellationToken ct)
    {
        var candidate = await db.PromotionCandidates.FirstOrDefaultAsync(x => x.Id == candidateId, ct);
        if (candidate is null || candidate.Status != PromotionStatus.PENDING) return;

        candidate.Status = PromotionStatus.DISMISSED;
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = playerId,
            EventType = "PROMOTION_CANDIDATE_ALREADY_CURRENT_RANK",
            MetadataJson = JsonUtil.Serialize(new
            {
                CandidateId = candidateId,
                Username = playerName,
                CurrentRank = currentRank,
                CandidateNewRank = candidateNewRank,
                Source = source
            }),
            Status = "DONE",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task SchedulePromotionCandidateCleanupAsync(
        TrackerDbContext db,
        int playerId,
        int candidateId,
        LifecycleEvent postedEvent,
        ulong? channelId,
        ulong? messageId,
        string reason,
        CancellationToken ct)
    {
        channelId ??= ExtractUlong(postedEvent.MetadataJson, "ChannelId");
        messageId ??= ExtractUlong(postedEvent.MetadataJson, "DiscordMessageId");
        if (!channelId.HasValue || !messageId.HasValue) return;

        var now = DateTimeOffset.UtcNow;
        var hasPendingDiscordRankAction =
            string.Equals(
                PickLifecycleValue(ReadLifecycleMetadata(postedEvent.MetadataJson), "DiscordRankActionAvailable"),
                "true",
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                PickLifecycleValue(ReadLifecycleMetadata(postedEvent.MetadataJson), "DiscordRankActionCompleted"),
                "true",
                StringComparison.OrdinalIgnoreCase);
        ScheduleChannelMessageDelete(
            db,
            playerId,
            channelId.Value,
            messageId.Value,
            "PROMOTION_DISCORD_DELETE_SCHEDULED",
            new
            {
                CandidateId = candidateId,
                Reason = hasPendingDiscordRankAction
                    ? "promotion-discord-rank-action-pending"
                    : reason
            },
            now.AddMinutes(_discordDeleteDelayMinutes),
            now.AddMinutes(_discordDeleteHardCapMinutes),
            extendExistingSchedule: true);
        await db.SaveChangesAsync(ct);
    }

    private static bool MetadataIntEquals(string json, string property, int expected)
        => LifecycleMetadataMatcher.HasIntProperty(json, property, expected);

    private static bool MetadataUlongEquals(string json, string property, ulong expected)
        => LifecycleMetadataMatcher.HasUlongProperty(json, property, expected);

    private static bool MetadataStringEquals(string json, string property, string expected, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
        => LifecycleMetadataMatcher.HasStringProperty(json, property, expected, comparison);

    private static LifecycleEvent? FindLifecycleEventByDiscordMessageId(IEnumerable<LifecycleEvent> events, ulong messageId)
    {
        return events.FirstOrDefault(x => ExtractUlong(x.MetadataJson, "DiscordMessageId") == messageId);
    }

    private static bool IsPromotionPostedSupersededByMerge(LifecycleEvent postedEvent)
    {
        var metadata = ReadLifecycleMetadata(postedEvent.MetadataJson);
        var value = PickLifecycleValue(metadata, "SupersededByMerge");
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPromotionReenableRequested(LifecycleEvent postedEvent)
    {
        var metadata = ReadLifecycleMetadata(postedEvent.MetadataJson);
        return string.Equals(
            PickLifecycleValue(metadata, "ReenableAfterMergeAbort"),
            "true",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMergeEventRelatedToPlayer(LifecycleEvent mergeEvent, int playerId, string username)
    {
        if (mergeEvent.PlayerId == playerId ||
            ExtractInt(mergeEvent.MetadataJson, "PreviousPlayerId") == playerId ||
            ExtractInt(mergeEvent.MetadataJson, "NewPlayerId") == playerId)
        {
            return true;
        }

        var metadata = ReadLifecycleMetadata(mergeEvent.MetadataJson);
        var normalizedUsername = NormalizeUsername(username);
        return string.Equals(
                   NormalizeUsername(PickLifecycleValue(metadata, "SuggestedPrevious") ?? ""),
                   normalizedUsername,
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   NormalizeUsername(PickLifecycleValue(metadata, "NewPlayer", "Username", "Player") ?? ""),
                   normalizedUsername,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTempleNameChangeConfirmed(Dictionary<string, string> metadata)
    {
        return !string.IsNullOrWhiteSpace(PickLifecycleValue(metadata, "ConfirmedAt"));
    }

    private static bool IsOpenTempleNameChangeForPreviousUsername(IEnumerable<LifecycleEvent> requirements, string username)
    {
        var normalized = NormalizeUsername(username);
        return requirements.Any(x =>
        {
            var metadata = ReadLifecycleMetadata(x.MetadataJson);
            return string.Equals(
                NormalizeUsername(PickLifecycleValue(metadata, "PreviousUsername") ?? ""),
                normalized,
                StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool IsOpenTempleNameChangeForNewUsername(IEnumerable<LifecycleEvent> requirements, string username)
    {
        var normalized = NormalizeUsername(username);
        return requirements.Any(x =>
        {
            var metadata = ReadLifecycleMetadata(x.MetadataJson);
            return string.Equals(
                NormalizeUsername(PickLifecycleValue(metadata, "NewUsername") ?? ""),
                normalized,
                StringComparison.OrdinalIgnoreCase);
        });
    }

    private static Dictionary<string, string> ReadLifecycleMetadata(string metadataJson)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return values;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                values[prop.Name] = JsonValueToString(prop.Value);
                if (string.Equals(prop.Name, "Options", StringComparison.OrdinalIgnoreCase))
                {
                    AddSlashCommandOptionMetadata(values, prop.Value);
                }
            }
        }
        catch
        {
            // best-effort metadata for Discord display/action handling
        }
        return values;
    }

    private static void AddSlashCommandOptionMetadata(Dictionary<string, string> values, JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Array) return;

        var index = 0;
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object) continue;
            var name = option.TryGetProperty("Name", out var nameProp) ? JsonValueToString(nameProp) : "";
            var value = option.TryGetProperty("Value", out var valueProp) ? JsonValueToString(valueProp) : "";
            if (!string.IsNullOrWhiteSpace(name))
            {
                values[$"Option.{name}"] = value;
            }

            values[$"Option{index}.Name"] = name;
            values[$"Option{index}.Value"] = value;
            index++;

            if (option.TryGetProperty("Options", out var nestedOptions))
            {
                AddSlashCommandOptionMetadata(values, nestedOptions);
            }
        }
    }

    private static string? PickLifecycleValue(Dictionary<string, string> metadata, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
    }

    private static DateTimeOffset? PickLifecycleDateTimeOffset(Dictionary<string, string> metadata, params string[] keys)
    {
        var value = PickLifecycleValue(metadata, keys);
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static string JsonValueToString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "",
            _ => value.GetRawText()
        };
    }

    private static string TrimMessage(string content)
    {
        if (content.Length <= 1900) return content;
        return content[..1900];
    }

    private static bool TryReadDateTimeOffset(JsonElement root, string property, out DateTimeOffset value)
    {
        value = default;
        if (!root.TryGetProperty(property, out var prop)) return false;
        if (prop.ValueKind != JsonValueKind.String) return false;
        return DateTimeOffset.TryParse(prop.GetString(), out value);
    }

    private static bool TryReadUlong(JsonElement root, string property, out ulong value)
    {
        value = 0;
        if (!root.TryGetProperty(property, out var prop)) return false;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetUInt64(out var n))
        {
            value = n;
            return true;
        }
        if (prop.ValueKind == JsonValueKind.String && ulong.TryParse(prop.GetString(), out var s))
        {
            value = s;
            return true;
        }
        return false;
    }

    private async Task<HashSet<string>> ReadOpenWomOnlyIgnoredUsernamesAsync(TrackerDbContext db, CancellationToken ct)
    {
        var openIgnores = await db.LifecycleEvents
            .Where(x => x.EventType == "WOM_ONLY_IGNORED" && x.Status == "OPEN")
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);

        var usernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ignoreEvent in openIgnores)
        {
            var metadata = ReadLifecycleMetadata(ignoreEvent.MetadataJson);
            var username = PickLifecycleValue(metadata, "Username", "Player");
            if (string.IsNullOrWhiteSpace(username))
            {
                ignoreEvent.Status = "DONE";
                continue;
            }

            var normalized = NormalizeUsername(username);
            if (!usernames.Add(normalized))
            {
                ignoreEvent.Status = "DONE";
            }
        }

        return usernames;
    }

    private async Task<bool> IsStillLiveWomOnlyRequirementAsync(
        TrackerDbContext db,
        LifecycleEvent requiredEvent,
        string normalizedUsername,
        string actualWomRole,
        CancellationToken ct)
    {
        var lowerUsername = normalizedUsername.ToLowerInvariant();
        var existsInDatabase = await db.Players.AnyAsync(
            x => x.Username.ToLower() == lowerUsername &&
                 x.Status != PlayerStatus.REMOVED_CONFIRMED,
            ct);
        if (existsInDatabase)
        {
            await CloseStaleWomOnlyRequirementAsync(
                db,
                requiredEvent,
                normalizedUsername,
                actualWomRole,
                "database",
                "Present",
                "Player already exists in tracker database.",
                ct);
            return false;
        }

        var womGroupId = configuration.GetValue<int?>("WiseOldMan:GroupId") ?? 7173;
        var isInWom = womGroupId > 0 && await IsPlayerInWiseOldManGroupAsync(normalizedUsername, womGroupId);
        if (!isInWom)
        {
            await CloseStaleWomOnlyRequirementAsync(
                db,
                requiredEvent,
                normalizedUsername,
                actualWomRole,
                "wom",
                "Missing",
                "Player is no longer present in Wise Old Man.",
                ct);
            return false;
        }

        var templeGroupId = configuration.GetValue<int?>("TempleOsrs:GroupId") ?? 449;
        var isInTemple = await IsPlayerInTempleGroupAsync(normalizedUsername, templeGroupId);
        if (isInTemple)
        {
            await CloseStaleWomOnlyRequirementAsync(
                db,
                requiredEvent,
                normalizedUsername,
                actualWomRole,
                "temple",
                "Present",
                "Player is already present in Temple.",
                ct);
            return false;
        }

        return true;
    }

    private static async Task CloseStaleWomOnlyRequirementAsync(
        TrackerDbContext db,
        LifecycleEvent requiredEvent,
        string normalizedUsername,
        string actualWomRole,
        string liveSource,
        string liveState,
        string reason,
        CancellationToken ct)
    {
        requiredEvent.Status = "DONE";
        await CloseOpenWomOnlyRequiredEventsAsync(db, normalizedUsername);
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = requiredEvent.PlayerId,
            EventType = "WOM_ONLY_SUPPRESSED_BY_LIVE_CHECK",
            MetadataJson = JsonUtil.Serialize(new
            {
                Username = normalizedUsername,
                ActualWomRole = actualWomRole,
                Source = "discord-post-guard",
                LiveSource = liveSource,
                LiveState = liveState,
                Reason = reason
            }),
            Status = "DONE",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    private static async Task CloseOpenWomOnlyRequiredEventsAsync(TrackerDbContext db, string normalizedUsername)
    {
        var openRequired = await db.LifecycleEvents
            .Where(x => x.EventType == "WOM_ONLY_ACTION_REQUIRED" && x.Status == "OPEN")
            .ToListAsync();
        foreach (var requiredEvent in openRequired)
        {
            var metadata = ReadLifecycleMetadata(requiredEvent.MetadataJson);
            var username = PickLifecycleValue(metadata, "Username", "Player");
            if (string.IsNullOrWhiteSpace(username)) continue;
            if (!string.Equals(NormalizeUsername(username), normalizedUsername, StringComparison.OrdinalIgnoreCase)) continue;
            requiredEvent.Status = "DONE";
        }
    }

    private static async Task EnsureOpenWomOnlyIgnoredEventAsync(
        TrackerDbContext db,
        int playerId,
        string normalizedUsername,
        string actualWomRole)
    {
        var openIgnores = await db.LifecycleEvents
            .Where(x => x.EventType == "WOM_ONLY_IGNORED" && x.Status == "OPEN")
            .ToListAsync();

        foreach (var existingIgnore in openIgnores)
        {
            var existingMetadata = ReadLifecycleMetadata(existingIgnore.MetadataJson);
            var existingUsername = PickLifecycleValue(existingMetadata, "Username", "Player");
            if (string.IsNullOrWhiteSpace(existingUsername)) continue;
            if (!string.Equals(NormalizeUsername(existingUsername), normalizedUsername, StringComparison.OrdinalIgnoreCase)) continue;

            existingIgnore.PlayerId = playerId;
            existingIgnore.MetadataJson = JsonUtil.Serialize(new
            {
                Username = normalizedUsername,
                ActualWomRole = actualWomRole,
                Source = "discord"
            });
            return;
        }

        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = playerId,
            EventType = "WOM_ONLY_IGNORED",
            MetadataJson = JsonUtil.Serialize(new
            {
                Username = normalizedUsername,
                ActualWomRole = actualWomRole,
                Source = "discord"
            }),
            Status = "OPEN",
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private async Task ProcessMessageActionUpdates(CancellationToken ct)
    {
        if (_client is null) return;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();

        var updates = await db.LifecycleEvents
            .Where(x => x.EventType == "PROMOTION_DISCORD_ACTION_APPLIED" && x.Status == "OPEN")
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);
        if (updates.Count == 0) return;

        var posted = await db.LifecycleEvents
            .Where(x => x.EventType == "PROMOTION_DISCORD_POSTED")
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

        foreach (var u in updates)
        {
            try
            {
                using var ud = JsonDocument.Parse(u.MetadataJson);
                if (!ud.RootElement.TryGetProperty("CandidateId", out var candProp)) { u.Status = "DONE"; continue; }
                var candidateId = candProp.GetInt32();
                var action = ud.RootElement.TryGetProperty("Action", out var a) ? a.GetString() ?? "unknown" : "unknown";
                var handledBy = ud.RootElement.TryGetProperty("HandledBy", out var h) ? h.GetString() ?? "web-admin" : "web-admin";
                var source = ud.RootElement.TryGetProperty("Source", out var s) ? s.GetString() ?? "web" : "web";

                var candidate = await db.PromotionCandidates
                    .Where(x => x.Id == candidateId)
                    .Select(x => new
                    {
                        x.PlayerId,
                        x.Status,
                        x.NewRank,
                        x.Player.Username
                    })
                    .FirstOrDefaultAsync(ct);
                var candidatePostedEvents = posted
                    .Where(x => MetadataIntEquals(x.MetadataJson, "CandidateId", candidateId))
                    .ToList();
                if (candidate is not null)
                {
                    var ownerSpecific = candidatePostedEvents
                        .Where(x => x.PlayerId == candidate.PlayerId)
                        .ToList();
                    if (ownerSpecific.Count > 0)
                    {
                        candidatePostedEvents = ownerSpecific;
                    }
                }

                var postEvent = candidatePostedEvents.FirstOrDefault(x => !IsPromotionPostedSupersededByMerge(x));
                postEvent ??= candidatePostedEvents.FirstOrDefault();
                if (postEvent is null) { u.Status = "DONE"; continue; }

                var hasDiscordRankAction = false;
                var discordRankTargetUserId = (ulong?)null;
                IReadOnlyList<DiscordMemberGuessMatch> discordMatchOptions = Array.Empty<DiscordMemberGuessMatch>();
                var completedDiscordRankAction = false;
                string? completedDiscordRankActionDetails = null;
                if (string.Equals(action, "approve", StringComparison.OrdinalIgnoreCase) &&
                    candidate is not null &&
                    candidate.Status == PromotionStatus.APPROVED)
                {
                    completedDiscordRankAction = string.Equals(
                        PickLifecycleValue(ReadLifecycleMetadata(postEvent.MetadataJson), "DiscordRankActionCompleted"),
                        "true",
                        StringComparison.OrdinalIgnoreCase);
                    completedDiscordRankActionDetails = PickLifecycleValue(
                        ReadLifecycleMetadata(postEvent.MetadataJson),
                        "DiscordRankActionDetails");
                    if (!completedDiscordRankAction)
                    {
                        discordMatchOptions = await ResolvePromotionDiscordMatchOptionsAsync(
                            db,
                            postEvent,
                            candidate.Username,
                            ct);
                        discordRankTargetUserId = await ResolvePromotionDiscordRankTargetUserIdAsync(
                            db,
                            postEvent,
                            candidate.Username,
                            ct);
                        hasDiscordRankAction = discordMatchOptions.Count > 0;
                        SetPromotionDiscordRankActionMetadata(
                            postEvent,
                            available: hasDiscordRankAction,
                            completed: false,
                            success: null,
                            details: null);
                    }
                }

                using var postDoc = JsonDocument.Parse(postEvent.MetadataJson);
                if (!postDoc.RootElement.TryGetProperty("ChannelId", out var channelIdProp) ||
                    !postDoc.RootElement.TryGetProperty("DiscordMessageId", out var messageIdProp))
                {
                    u.Status = "DONE";
                    continue;
                }

                var channel = _client.GetChannel(channelIdProp.GetUInt64()) as IMessageChannel;
                if (channel is null) { u.Status = "DONE"; continue; }
                var msg = await channel.GetMessageAsync(messageIdProp.GetUInt64());
                var deleteNow = DateTimeOffset.UtcNow;
                ScheduleChannelMessageDelete(
                    db,
                    u.PlayerId,
                    channelIdProp.GetUInt64(),
                    messageIdProp.GetUInt64(),
                    "PROMOTION_DISCORD_DELETE_SCHEDULED",
                    new
                    {
                        CandidateId = candidateId,
                        Reason = hasDiscordRankAction
                            ? "promotion-action-handled-discord-rank-pending"
                            : "promotion-action-handled-web"
                    },
                    deleteNow.AddMinutes(_discordDeleteDelayMinutes),
                    deleteNow.AddMinutes(_discordDeleteHardCapMinutes),
                    extendExistingSchedule: true);
                if (msg is IUserMessage userMessage)
                {
                    var handled = $"Handled by {handledBy} ({action}) via {source}";
                    if (completedDiscordRankAction && !string.IsNullOrWhiteSpace(completedDiscordRankActionDetails))
                    {
                        handled += $". {completedDiscordRankActionDetails}";
                    }
                    else if (hasDiscordRankAction && discordRankTargetUserId.HasValue)
                    {
                        handled += ". Exact Discord match selected; use the button below to set the rank role.";
                    }
                    else if (hasDiscordRankAction)
                    {
                        handled += ". Possible Discord matches found; choose a member before setting the rank role.";
                    }
                    else if (string.Equals(action, "approve", StringComparison.OrdinalIgnoreCase))
                    {
                        handled += ". No likely Discord match was found; use /discord-rank-set manually.";
                    }

                    var components = discordRankTargetUserId.HasValue
                        ? BuildPromotionDiscordRankActionComponents(candidateId, candidate?.NewRank ?? "Unknown")
                        : hasDiscordRankAction
                            ? BuildPromotionDiscordMemberPickerButtonComponents(candidateId)
                        : new ComponentBuilder().Build();
                    try
                    {
                        await userMessage.ModifyAsync(props =>
                        {
                            props.Components = components;
                            props.Embed = BuildHandledEmbed(userMessage.Embeds.FirstOrDefault(), handled, action);
                        });
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Promotion action message update timed out for lifecycle event {LifecycleEventId}; delete is still scheduled.", u.Id);
                    }
                }

                u.Status = "DONE";
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Retrying promotion Discord action update later for lifecycle event {LifecycleEventId}.", u.Id);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task PostTempleMissingActionMessages(CancellationToken ct)
    {
        if (_client is null) return;
        var channel = await ResolveMessageChannelAsync(_options.ChannelId);
        if (channel is null) return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var openTempleNameChanges = await db.LifecycleEvents
            .Where(x => x.EventType == TempleNameChangeReviewEventTypes.Required && x.Status == "OPEN")
            .ToListAsync(ct);

        // Self-heal: ensure every missing player has an actionable lifecycle event.
        var missingPlayers = await db.Players
            .Where(x => x.Status == PlayerStatus.MISSING_PENDING_REVIEW)
            .Select(x => new { x.Id, x.Username })
            .ToListAsync(ct);
        var templeGroupIdForSelfHeal = configuration.GetValue<int?>("TempleOsrs:GroupId") ?? 449;
        foreach (var mp in missingPlayers)
        {
            if (await HasOpenMergePendingForPreviousUsernameAsync(db, mp.Username, ct))
            {
                await CloseOpenLifecycleEventsAsync(db, mp.Id, "MISSING_IN_ROSTER", "TEMPLE_MISSING_ACTION_REQUIRED");
                continue;
            }
            var isMissingInTemple = !await IsPlayerInTempleGroupAsync(mp.Username, templeGroupIdForSelfHeal);
            if (!isMissingInTemple) continue;

            var hasPendingAction = await db.LifecycleEvents.AnyAsync(x =>
                x.PlayerId == mp.Id &&
                x.EventType == "TEMPLE_MISSING_ACTION_REQUIRED" &&
                x.Status == "OPEN", ct);
            if (!hasPendingAction)
            {
                db.LifecycleEvents.Add(new LifecycleEvent
                {
                    PlayerId = mp.Id,
                    EventType = "TEMPLE_MISSING_ACTION_REQUIRED",
                    MetadataJson = JsonUtil.Serialize(new { mp.Username, MissingAt = DateTimeOffset.UtcNow, Source = "discord-self-heal" }),
                    Status = "OPEN",
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
        }
        if (missingPlayers.Count > 0) await db.SaveChangesAsync(ct);

        var pending = await db.LifecycleEvents
            .Where(x => x.EventType == "TEMPLE_MISSING_ACTION_REQUIRED" && x.Status == "OPEN")
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);
        logger.LogInformation("Temple missing action scan found {PendingCount} pending lifecycle events.", pending.Count);
        if (pending.Count == 0) return;

        foreach (var ev in pending)
        {
            await db.Entry(ev).ReloadAsync(ct);
            if (ev.Status != "OPEN") continue;

            var player = await db.Players.FirstOrDefaultAsync(x => x.Id == ev.PlayerId, ct);
            if (player is null)
            {
                ev.Status = "DONE";
                continue;
            }
            if (IsOpenTempleNameChangeForPreviousUsername(openTempleNameChanges, player.Username))
            {
                continue;
            }
            if (await HasOpenMergePendingForPreviousUsernameAsync(db, player.Username, ct))
            {
                await CloseOpenLifecycleEventsAsync(db, player.Id, "MISSING_IN_ROSTER", "TEMPLE_MISSING_ACTION_REQUIRED");
                ev.Status = "DONE";
                continue;
            }

            var womGroupId = configuration.GetValue<int?>("WiseOldMan:GroupId") ?? 7173;
            var womAdded = womGroupId > 0 && await IsPlayerInWiseOldManGroupAsync(player.Username, womGroupId);
            var discordGuess = await GuessDiscordMemberForPlayerAsync(db, player.Id, player.Username, ct);

            var embedBuilder = new EmbedBuilder()
                .WithTitle("Temple Membership Missing")
                .WithColor(new Color(245, 158, 11))
                .AddField("Player", player.Username, true)
                .AddField("Current Rank", player.CurrentRank, true)
                .AddField("Status", player.Status.ToString(), true)
                .AddField("Temple", "Missing", true)
                .AddField("WiseOldMan", womAdded ? "Added" : "Missing", true)
                .AddField("Pets", (player.ManualPetOverride ?? player.StoredPetCount) > 0 ? (player.ManualPetOverride ?? player.StoredPetCount).ToString() : "N/A", true);
            AddDiscordGuessField(embedBuilder, "Player", discordGuess);
            var embed = embedBuilder
                .AddField("Last Synced (Swedish Time)", FormatSwedishTime(player.LastSynced), false)
                .Build();
            var renderFingerprint = ComputeRenderFingerprint(new
            {
                Type = "temple-missing-card",
                EventId = ev.Id,
                PlayerId = player.Id,
                player.Username,
                player.CurrentRank,
                PlayerStatus = player.Status.ToString(),
                Temple = "Missing",
                Wom = womAdded ? "Added" : "Missing",
                DiscordGuess = FormatDiscordGuessForFingerprint(discordGuess),
                Pets = player.ManualPetOverride ?? player.StoredPetCount,
                LastSynced = FormatSwedishTime(player.LastSynced)
            });

            var buttons = new ComponentBuilder()
                .WithButton("Add back to Temple", $"missing:add:{player.Id}", ButtonStyle.Success)
                .WithButton("Remove from DB", $"missing:remove:{player.Id}", ButtonStyle.Danger);

            var postedEvent = await db.LifecycleEvents
                .Where(x => x.EventType == "TEMPLE_MISSING_DISCORD_POSTED" && x.PlayerId == ev.PlayerId)
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (postedEvent is not null)
            {
                var lookupKey = $"temple-missing:{ev.Id}";
                if (IsLookupBackoffActive(lookupKey)) continue;
                var (lookupState, liveDiscordMessage, channelId, messageId) = await TryGetPostedUserMessageAsync(postedEvent, lookupKey);
                if (lookupState == PostedMessageLookupState.Unknown)
                {
                    SetLookupBackoff(lookupKey);
                    continue;
                }
                if (lookupState == PostedMessageLookupState.Malformed)
                {
                    postedEvent.Status = "DONE";
                    continue;
                }
                if (lookupState == PostedMessageLookupState.Missing)
                {
                    await RecordMissingTrackedMessageEventAsync(
                        db,
                        ev.PlayerId,
                        "temple-missing",
                        postedEvent.Id,
                        ev.Id,
                        channelId,
                        messageId,
                        "post-temple-missing",
                        ct);
                }
                if (lookupState == PostedMessageLookupState.Found && liveDiscordMessage is not null)
                {
                    if (ShouldSkipMessagePatch(liveDiscordMessage.Id, renderFingerprint))
                    {
                        continue;
                    }
                    await liveDiscordMessage.ModifyAsync(props =>
                    {
                        props.Embed = embed;
                        props.Components = buttons.Build();
                    });
                    RecordMessagePatched(liveDiscordMessage.Id, renderFingerprint);
                    postedEvent.MetadataJson = JsonUtil.Serialize(new
                    {
                        Player = player.Username,
                        ChannelId = liveDiscordMessage.Channel.Id,
                        DiscordMessageId = liveDiscordMessage.Id,
                        RenderFingerprint = renderFingerprint
                    });
                    continue;
                }
            }

            var lease = await TryAcquirePostLeaseAsync(db, ev.PlayerId, $"temple-missing:{ev.Id}", ct);
            if (lease is null) continue;
            try
            {
                await db.Entry(ev).ReloadAsync(ct);
                if (ev.Status != "OPEN") continue;

                var msg = await channel.SendMessageAsync(embed: embed, components: buttons.Build());
            logger.LogInformation(
                "Posted Temple missing action message for player {Player} (playerId: {PlayerId}, discordMessageId: {MessageId}).",
                player.Username, player.Id, msg.Id);
            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = player.Id,
                EventType = "TEMPLE_MISSING_DISCORD_POSTED",
                MetadataJson = JsonUtil.Serialize(new { Player = player.Username, ChannelId = _options.ChannelId, DiscordMessageId = msg.Id, RenderFingerprint = renderFingerprint }),
                Status = "DONE",
                CreatedAt = DateTimeOffset.UtcNow
            });
            RecordMessagePatched(msg.Id, renderFingerprint);
            }
            finally
            {
                lease.Status = "DONE";
                await db.SaveChangesAsync(ct);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task PostWomMissingActionMessages(CancellationToken ct)
    {
        if (_client is null) return;
        var channel = await ResolveMessageChannelAsync(_options.ChannelId);
        if (channel is null) return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var openTempleNameChanges = await db.LifecycleEvents
            .Where(x => x.EventType == TempleNameChangeReviewEventTypes.Required && x.Status == "OPEN")
            .ToListAsync(ct);

        // Self-heal: ensure WOM-missing players have actionable events.
        var womGroupIdForSelfHeal = configuration.GetValue<int?>("WiseOldMan:GroupId") ?? 7173;
        var templeGroupIdForSelfHeal = configuration.GetValue<int?>("TempleOsrs:GroupId") ?? 449;
        var missingPlayers = await db.Players
            .Where(x => x.Status == PlayerStatus.MISSING_PENDING_REVIEW)
            .Select(x => new { x.Id, x.Username })
            .ToListAsync(ct);
        foreach (var mp in missingPlayers)
        {
            var inTemple = await IsPlayerInTempleGroupAsync(mp.Username, templeGroupIdForSelfHeal);
            var inWom = womGroupIdForSelfHeal > 0 && await IsPlayerInWiseOldManGroupAsync(mp.Username, womGroupIdForSelfHeal);
            if (!inTemple || inWom) continue;

            var hasPendingAction = await db.LifecycleEvents.AnyAsync(x =>
                x.PlayerId == mp.Id &&
                x.EventType == "WOM_MISSING_ACTION_REQUIRED" &&
                x.Status == "OPEN", ct);
            if (!hasPendingAction)
            {
                db.LifecycleEvents.Add(new LifecycleEvent
                {
                    PlayerId = mp.Id,
                    EventType = "WOM_MISSING_ACTION_REQUIRED",
                    MetadataJson = JsonUtil.Serialize(new { mp.Username, MissingAt = DateTimeOffset.UtcNow, Source = "discord-self-heal" }),
                    Status = "OPEN",
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
        }
        if (missingPlayers.Count > 0) await db.SaveChangesAsync(ct);

        var pending = await db.LifecycleEvents
            .Where(x => x.EventType == "WOM_MISSING_ACTION_REQUIRED" && x.Status == "OPEN")
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);
        if (pending.Count == 0) return;

        foreach (var ev in pending)
        {
            await db.Entry(ev).ReloadAsync(ct);
            if (ev.Status != "OPEN") continue;

            var player = await db.Players.FirstOrDefaultAsync(x => x.Id == ev.PlayerId, ct);
            if (player is null)
            {
                ev.Status = "DONE";
                continue;
            }
            if (IsOpenTempleNameChangeForPreviousUsername(openTempleNameChanges, player.Username))
            {
                continue;
            }

            var templeGroupId = configuration.GetValue<int?>("TempleOsrs:GroupId") ?? 449;
            var templeAdded = await IsPlayerInTempleGroupAsync(player.Username, templeGroupId);
            var womGroupId = configuration.GetValue<int?>("WiseOldMan:GroupId") ?? 7173;
            var womAdded = womGroupId > 0 && await IsPlayerInWiseOldManGroupAsync(player.Username, womGroupId);
            if (womAdded)
            {
                await CloseOpenLifecycleEventsAsync(db, player.Id, "WOM_MISSING_ACTION_REQUIRED");
                if (templeAdded && player.Status == PlayerStatus.MISSING_PENDING_REVIEW)
                {
                    player.Status = PlayerStatus.ACTIVE;
                    await CloseOpenLifecycleEventsAsync(db, player.Id,
                        "MISSING_IN_ROSTER",
                        "TEMPLE_MISSING_ACTION_REQUIRED");
                }
                ev.Status = "DONE";
                db.LifecycleEvents.Add(new LifecycleEvent
                {
                    PlayerId = player.Id,
                    EventType = "WOM_MISSING_SUPPRESSED_BY_LIVE_CHECK",
                    MetadataJson = JsonUtil.Serialize(new
                    {
                        player.Username,
                        Source = "discord-post-guard",
                        Temple = templeAdded ? "Added" : "Missing",
                        Wom = "Added"
                    }),
                    Status = "DONE",
                    CreatedAt = DateTimeOffset.UtcNow
                });
                continue;
            }
            var discordGuess = await GuessDiscordMemberForPlayerAsync(db, player.Id, player.Username, ct);

            var embedBuilder = new EmbedBuilder()
                .WithTitle("WiseOldMan Membership Missing")
                .WithColor(new Color(249, 115, 22))
                .AddField("Player", player.Username, true)
                .AddField("Current Rank", player.CurrentRank, true)
                .AddField("Status", player.Status.ToString(), true)
                .AddField("Temple", templeAdded ? "Added" : "Missing", true)
                .AddField("WiseOldMan", "Missing", true)
                .AddField("Pets", (player.ManualPetOverride ?? player.StoredPetCount) > 0 ? (player.ManualPetOverride ?? player.StoredPetCount).ToString() : "N/A", true);
            AddDiscordGuessField(embedBuilder, "Player", discordGuess);
            var embed = embedBuilder
                .AddField("Last Synced (Swedish Time)", FormatSwedishTime(player.LastSynced), false)
                .Build();
            var renderFingerprint = ComputeRenderFingerprint(new
            {
                Type = "wom-missing-card",
                EventId = ev.Id,
                PlayerId = player.Id,
                player.Username,
                player.CurrentRank,
                PlayerStatus = player.Status.ToString(),
                Temple = templeAdded ? "Added" : "Missing",
                Wom = "Missing",
                DiscordGuess = FormatDiscordGuessForFingerprint(discordGuess),
                Pets = player.ManualPetOverride ?? player.StoredPetCount,
                LastSynced = FormatSwedishTime(player.LastSynced)
            });

            var buttons = new ComponentBuilder()
                .WithButton("Reinstate in WiseOldMan", $"wommissing:reinstate:{player.Id}", ButtonStyle.Success)
                .WithButton("Remove from Temple + DB", $"wommissing:remove:{player.Id}", ButtonStyle.Danger);

            var postedEvent = await db.LifecycleEvents
                .Where(x => x.EventType == "WOM_MISSING_DISCORD_POSTED" && x.PlayerId == ev.PlayerId)
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (postedEvent is not null)
            {
                var lookupKey = $"wom-missing:{ev.Id}";
                if (IsLookupBackoffActive(lookupKey)) continue;
                var (lookupState, liveDiscordMessage, channelId, messageId) = await TryGetPostedUserMessageAsync(postedEvent, lookupKey);
                if (lookupState == PostedMessageLookupState.Unknown)
                {
                    SetLookupBackoff(lookupKey);
                    continue;
                }
                if (lookupState == PostedMessageLookupState.Malformed)
                {
                    postedEvent.Status = "DONE";
                    continue;
                }
                if (lookupState == PostedMessageLookupState.Missing)
                {
                    await RecordMissingTrackedMessageEventAsync(
                        db,
                        ev.PlayerId,
                        "wom-missing",
                        postedEvent.Id,
                        ev.Id,
                        channelId,
                        messageId,
                        "post-wom-missing",
                        ct);
                }
                if (lookupState == PostedMessageLookupState.Found && liveDiscordMessage is not null)
                {
                    if (ShouldSkipMessagePatch(liveDiscordMessage.Id, renderFingerprint))
                    {
                        continue;
                    }
                    await liveDiscordMessage.ModifyAsync(props =>
                    {
                        props.Embed = embed;
                        props.Components = buttons.Build();
                    });
                    RecordMessagePatched(liveDiscordMessage.Id, renderFingerprint);
                    postedEvent.MetadataJson = JsonUtil.Serialize(new
                    {
                        Player = player.Username,
                        ChannelId = liveDiscordMessage.Channel.Id,
                        DiscordMessageId = liveDiscordMessage.Id,
                        RenderFingerprint = renderFingerprint
                    });
                    continue;
                }
            }

            var lease = await TryAcquirePostLeaseAsync(db, ev.PlayerId, $"wom-missing:{ev.Id}", ct);
            if (lease is null) continue;
            try
            {
                await db.Entry(ev).ReloadAsync(ct);
                if (ev.Status != "OPEN") continue;

                var msg = await channel.SendMessageAsync(embed: embed, components: buttons.Build());
            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = player.Id,
                EventType = "WOM_MISSING_DISCORD_POSTED",
                MetadataJson = JsonUtil.Serialize(new { Player = player.Username, ChannelId = _options.ChannelId, DiscordMessageId = msg.Id, RenderFingerprint = renderFingerprint }),
                Status = "DONE",
                CreatedAt = DateTimeOffset.UtcNow
            });
            RecordMessagePatched(msg.Id, renderFingerprint);
            }
            finally
            {
                lease.Status = "DONE";
                await db.SaveChangesAsync(ct);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static bool RequiresWomOnlyActionForCleanup(JsonElement root)
    {
        if (!root.TryGetProperty("Extra", out var extra) || extra.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!extra.TryGetProperty("Reason", out var reasonProperty) || reasonProperty.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var reason = reasonProperty.GetString() ?? "";
        return string.Equals(reason, "wom-only-action-handled", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> HasWomOnlyActionForScheduledDeleteAsync(
        TrackerDbContext db,
        LifecycleEvent scheduledDeleteEvent,
        CancellationToken ct)
    {
        var scheduledMessageId = ExtractUlong(scheduledDeleteEvent.MetadataJson, "DiscordMessageId");
        if (!scheduledMessageId.HasValue)
        {
            return false;
        }

        var actionEvents = await db.LifecycleEvents
            .Where(x =>
                x.PlayerId == scheduledDeleteEvent.PlayerId &&
                x.EventType == "WOM_ONLY_ACTION_APPLIED")
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

        foreach (var actionEvent in actionEvents)
        {
            var metadata = ReadLifecycleMetadata(actionEvent.MetadataJson);
            var actionMessageIdText = PickLifecycleValue(metadata, "DiscordMessageId", "ClickedDiscordMessageId");
            if (ulong.TryParse(actionMessageIdText, out var actionMessageId) &&
                actionMessageId == scheduledMessageId.Value)
            {
                return true;
            }
        }

        return false;
    }

    private async Task PostWomOnlyActionMessages(CancellationToken ct)
    {
        if (_client is null) return;
        var channel = await ResolveMessageChannelAsync(_options.ChannelId);
        if (channel is null) return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var openTempleNameChanges = await db.LifecycleEvents
            .Where(x => x.EventType == TempleNameChangeReviewEventTypes.Required && x.Status == "OPEN")
            .ToListAsync(ct);

        var pending = await db.LifecycleEvents
            .Where(x => x.EventType == "WOM_ONLY_ACTION_REQUIRED" && x.Status == "OPEN")
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);
        if (pending.Count == 0) return;

        var ignoredUsernames = await ReadOpenWomOnlyIgnoredUsernamesAsync(db, ct);
        var posted = await db.LifecycleEvents
            .Where(x => x.EventType == "WOM_ONLY_DISCORD_POSTED")
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
        var seenUsernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ev in pending)
        {
            await db.Entry(ev).ReloadAsync(ct);
            if (ev.Status != "OPEN") continue;

            var metadata = ReadLifecycleMetadata(ev.MetadataJson);
            var username = PickLifecycleValue(metadata, "Username", "Player");
            var womRole = PickLifecycleValue(metadata, "ActualWomRole") ?? "Unknown";
            if (string.IsNullOrWhiteSpace(username))
            {
                ev.Status = "DONE";
                continue;
            }

            var normalizedUsername = NormalizeUsername(username);
            if (!seenUsernames.Add(normalizedUsername))
            {
                ev.Status = "DONE";
                continue;
            }

            if (ignoredUsernames.Contains(normalizedUsername))
            {
                ev.Status = "DONE";
                continue;
            }
            if (IsOpenTempleNameChangeForNewUsername(openTempleNameChanges, normalizedUsername))
            {
                continue;
            }

            if (DateTimeOffset.UtcNow - ev.CreatedAt < WomOnlyPostGracePeriod)
            {
                continue;
            }

            if (!await IsStillLiveWomOnlyRequirementAsync(db, ev, normalizedUsername, womRole, ct))
            {
                continue;
            }

            var postedEventsForRequirement = posted
                .Where(x => ExtractInt(x.MetadataJson, "RequiredEventId") == ev.Id)
                .ToList();
            var renderFingerprint = ComputeRenderFingerprint(new
            {
                Type = "wom-only-card",
                EventId = ev.Id,
                Username = normalizedUsername,
                ActualWomRole = womRole
            });
            var latestPostedEvent = postedEventsForRequirement.FirstOrDefault();
            if (latestPostedEvent is not null)
            {
                var lookupKey = $"wom-only:{ev.Id}";
                if (IsLookupBackoffActive(lookupKey)) continue;
                var (lookupState, liveDiscordMessage, channelId, messageId) = await TryGetPostedUserMessageAsync(latestPostedEvent, lookupKey);
                if (lookupState == PostedMessageLookupState.Unknown)
                {
                    SetLookupBackoff(lookupKey);
                    continue;
                }
                if (lookupState == PostedMessageLookupState.Malformed)
                {
                    latestPostedEvent.Status = "DONE";
                    continue;
                }
                if (lookupState == PostedMessageLookupState.Missing)
                {
                    await RecordMissingTrackedMessageEventAsync(
                        db,
                        ev.PlayerId,
                        "wom-only",
                        latestPostedEvent.Id,
                        ev.Id,
                        channelId,
                        messageId,
                        "post-wom-only",
                        ct);
                }
                if (lookupState == PostedMessageLookupState.Found && liveDiscordMessage is not null)
                {
                    if (ShouldSkipMessagePatch(liveDiscordMessage.Id, renderFingerprint))
                    {
                        continue;
                    }
                    await liveDiscordMessage.ModifyAsync(props =>
                    {
                        props.Embed = BuildWomOnlyRequiredEmbed(normalizedUsername, womRole);
                        props.Components = BuildWomOnlyRequiredComponents(ev.Id);
                    });
                    RecordMessagePatched(liveDiscordMessage.Id, renderFingerprint);
                    latestPostedEvent.MetadataJson = JsonUtil.Serialize(new
                    {
                        Username = normalizedUsername,
                        ActualWomRole = womRole,
                        RequiredEventId = ev.Id,
                        ChannelId = liveDiscordMessage.Channel.Id,
                        DiscordMessageId = liveDiscordMessage.Id,
                        RenderFingerprint = renderFingerprint
                    });
                    continue;
                }
            }

            var lease = await TryAcquirePostLeaseAsync(db, ev.PlayerId, $"wom-only:{ev.Id}", ct);
            if (lease is null) continue;
            try
            {
                await db.Entry(ev).ReloadAsync(ct);
                if (ev.Status != "OPEN") continue;

                var msg = await channel.SendMessageAsync(
                    embed: BuildWomOnlyRequiredEmbed(normalizedUsername, womRole),
                    components: BuildWomOnlyRequiredComponents(ev.Id));

                db.LifecycleEvents.Add(new LifecycleEvent
                {
                    PlayerId = ev.PlayerId,
                    EventType = "WOM_ONLY_DISCORD_POSTED",
                    MetadataJson = JsonUtil.Serialize(new
                    {
                        Username = normalizedUsername,
                        ActualWomRole = womRole,
                        RequiredEventId = ev.Id,
                        ChannelId = _options.ChannelId,
                        DiscordMessageId = msg.Id,
                        RenderFingerprint = renderFingerprint
                    }),
                    Status = "DONE",
                    CreatedAt = DateTimeOffset.UtcNow
                });
                RecordMessagePatched(msg.Id, renderFingerprint);
            }
            finally
            {
                lease.Status = "DONE";
                await db.SaveChangesAsync(ct);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task PostWomRankMismatchMessages(CancellationToken ct)
    {
        if (_client is null) return;
        var channel = await ResolveMessageChannelAsync(_options.ChannelId);
        if (channel is null) return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();

        var pending = await db.LifecycleEvents
            .Where(x => x.EventType == "WOM_RANK_MISMATCH_REQUIRED" && x.Status == "OPEN")
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);
        if (pending.Count == 0) return;

        var duplicatePendingIds = pending
            .GroupBy(x => x.PlayerId)
            .SelectMany(x => x.OrderBy(ev => ev.CreatedAt).Skip(1))
            .Select(x => x.Id)
            .ToHashSet();
        foreach (var duplicate in pending.Where(x => duplicatePendingIds.Contains(x.Id)))
        {
            duplicate.Status = "DONE";
        }

        foreach (var ev in pending.Where(x => !duplicatePendingIds.Contains(x.Id)))
        {
            var ignored = await db.LifecycleEvents.AnyAsync(x =>
                x.PlayerId == ev.PlayerId &&
                x.EventType == "WOM_RANK_MISMATCH_IGNORED" &&
                x.Status == "OPEN", ct);
            if (ignored)
            {
                ev.Status = "DONE";
                continue;
            }

            var player = await db.Players.FirstOrDefaultAsync(x => x.Id == ev.PlayerId, ct);
            if (player is null)
            {
                ev.Status = "DONE";
                continue;
            }

            var metadata = ReadLifecycleMetadata(ev.MetadataJson);
            var expectedRank = PickLifecycleValue(metadata, "ExpectedRank") ?? player.CurrentRank;
            var actualWomRole = PickLifecycleValue(metadata, "ActualWomRole") ?? "Unknown";
            var direction = GetWomRankMismatchDirection(expectedRank, actualWomRole);
            var discordGuess = await GuessDiscordMemberForPlayerAsync(db, player.Id, player.Username, ct);
            var embed = BuildWomRankMismatchEmbed(player.Username, expectedRank, actualWomRole, direction, discordGuess);
            var components = BuildWomRankMismatchComponents(player.Id, ev.Id, expectedRank, actualWomRole);
            var renderFingerprint = ComputeRenderFingerprint(new
            {
                Type = "wom-rank-mismatch-card",
                EventId = ev.Id,
                PlayerId = player.Id,
                player.Username,
                ExpectedRank = expectedRank,
                ActualWomRole = actualWomRole,
                Direction = direction,
                DiscordGuess = FormatDiscordGuessForFingerprint(discordGuess)
            });

            var postedEvents = await db.LifecycleEvents
                .Where(x => x.EventType == "WOM_RANK_MISMATCH_DISCORD_POSTED" && x.PlayerId == ev.PlayerId)
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync(ct);
            var postedEvent = postedEvents.FirstOrDefault();
            if (postedEvent is not null)
            {
                var lookupKey = $"wom-rank-mismatch:{ev.Id}";
                if (IsLookupBackoffActive(lookupKey)) continue;
                var (lookupState, liveDiscordMessage, postedChannelId, postedMessageId) = await TryGetPostedUserMessageAsync(postedEvent, lookupKey);
                if (lookupState == PostedMessageLookupState.Unknown)
                {
                    SetLookupBackoff(lookupKey);
                    continue;
                }
                if (lookupState == PostedMessageLookupState.Malformed)
                {
                    postedEvent.Status = "DONE";
                    continue;
                }
                if (lookupState == PostedMessageLookupState.Missing)
                {
                    await RecordMissingTrackedMessageEventAsync(
                        db,
                        ev.PlayerId,
                        "wom-rank-mismatch",
                        postedEvent.Id,
                        ev.Id,
                        postedChannelId,
                        postedMessageId,
                        "post-wom-rank-mismatch",
                        ct);
                }
                var postedWasHandled = await HasWomRankMismatchActionForPostedEventAsync(db, ev.PlayerId, postedEvent, ct);
                if (lookupState == PostedMessageLookupState.Found && liveDiscordMessage is not null)
                {
                    if (postedWasHandled)
                    {
                        ScheduleWomRankMismatchMessageDelete(
                            db,
                            ev.PlayerId,
                            postedChannelId,
                            postedMessageId,
                            "wom-rank-mismatch-action-handled",
                            $"WOM rank mismatch alert for {player.Username}");
                        continue;
                    }

                    if (ShouldSkipMessagePatch(liveDiscordMessage.Id, renderFingerprint))
                    {
                        continue;
                    }

                    await liveDiscordMessage.ModifyAsync(props =>
                    {
                        props.Embed = embed;
                        props.Components = components;
                    });
                    RecordMessagePatched(liveDiscordMessage.Id, renderFingerprint);
                    postedEvent.MetadataJson = JsonUtil.Serialize(new
                    {
                        Player = player.Username,
                        ExpectedRank = expectedRank,
                        ActualWomRole = actualWomRole,
                        Direction = direction,
                        RequiredEventId = ev.Id,
                        ChannelId = liveDiscordMessage.Channel.Id,
                        DiscordMessageId = liveDiscordMessage.Id,
                        RenderFingerprint = renderFingerprint
                    });
                    continue;
                }

                if (postedWasHandled && postedChannelId.HasValue && postedMessageId.HasValue)
                {
                    var openDeleteRows = await db.LifecycleEvents
                        .Where(x =>
                            x.Status == "OPEN" &&
                            x.EventType == "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED")
                        .ToListAsync(ct);
                    var hasOpenDeleteForPostedMessage = openDeleteRows.Any(x =>
                        MetadataUlongEquals(x.MetadataJson, "DiscordMessageId", postedMessageId.Value));
                    if (hasOpenDeleteForPostedMessage)
                    {
                        continue;
                    }
                }

                foreach (var duplicatePostedEvent in postedEvents.Skip(1))
                {
                    try
                    {
                        using var duplicateDoc = JsonDocument.Parse(duplicatePostedEvent.MetadataJson);
                        if (!TryReadUlong(duplicateDoc.RootElement, "ChannelId", out var duplicateChannelId) ||
                            !TryReadUlong(duplicateDoc.RootElement, "DiscordMessageId", out var duplicateMessageId))
                        {
                            continue;
                        }

                        await EnsureMessageDeleteScheduledOrDeletedAsync(
                            db,
                            duplicatePostedEvent.PlayerId,
                            duplicateChannelId,
                            duplicateMessageId,
                            "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED",
                            new { Reason = "wom-rank-mismatch-duplicate", MessageDescription = $"WOM rank mismatch alert for {player.Username}" },
                            duplicatePostedEvent.CreatedAt,
                            ct);
                    }
                    catch
                    {
                        // ignore malformed old metadata
                    }
                }
            }

            var lease = await TryAcquirePostLeaseAsync(db, ev.PlayerId, $"wom-rank-mismatch:{ev.Id}", ct);
            if (lease is null) continue;
            try
            {
                await db.Entry(ev).ReloadAsync(ct);
                if (ev.Status != "OPEN") continue;

                var msg = await channel.SendMessageAsync(embed: embed, components: components);
                db.LifecycleEvents.Add(new LifecycleEvent
                {
                    PlayerId = player.Id,
                    EventType = "WOM_RANK_MISMATCH_DISCORD_POSTED",
                    MetadataJson = JsonUtil.Serialize(new
                    {
                        Player = player.Username,
                        ExpectedRank = expectedRank,
                        ActualWomRole = actualWomRole,
                        Direction = direction,
                        RequiredEventId = ev.Id,
                        ChannelId = _options.ChannelId,
                        DiscordMessageId = msg.Id,
                        RenderFingerprint = renderFingerprint
                    }),
                    Status = "DONE",
                    CreatedAt = DateTimeOffset.UtcNow
                });
                RecordMessagePatched(msg.Id, renderFingerprint);
            }
            finally
            {
                lease.Status = "DONE";
                await db.SaveChangesAsync(ct);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task ProcessTempleMissingActionUpdates(CancellationToken ct)
    {
        if (_client is null) return;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var updates = await db.LifecycleEvents
            .Where(x => x.EventType == "TEMPLE_MISSING_ACTION_APPLIED" && x.Status == "OPEN")
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);
        if (updates.Count == 0) return;

        var posted = await db.LifecycleEvents
            .Where(x => x.EventType == "TEMPLE_MISSING_DISCORD_POSTED")
            .ToListAsync(ct);

        foreach (var update in updates)
        {
            using var ud = JsonDocument.Parse(update.MetadataJson);
            var action = ud.RootElement.TryGetProperty("Action", out var a) ? a.GetString() ?? "unknown" : "unknown";
            var handledBy = ud.RootElement.TryGetProperty("HandledBy", out var h) ? h.GetString() ?? "web-admin" : "web-admin";
            var source = ud.RootElement.TryGetProperty("Source", out var s) ? s.GetString() ?? "web" : "web";

            var post = posted.FirstOrDefault(x => x.PlayerId == update.PlayerId);
            if (post is null)
            {
                update.Status = "DONE";
                continue;
            }

            using var pd = JsonDocument.Parse(post.MetadataJson);
            if (!pd.RootElement.TryGetProperty("ChannelId", out var chProp) ||
                !pd.RootElement.TryGetProperty("DiscordMessageId", out var msgProp))
            {
                update.Status = "DONE";
                continue;
            }
            var channel = await ResolveMessageChannelAsync(chProp.GetUInt64());
            if (channel is null)
            {
                update.Status = "DONE";
                continue;
            }
            var msg = await channel.GetMessageAsync(msgProp.GetUInt64());
            if (msg is IUserMessage userMessage)
            {
                var handled = $"Handled by {handledBy} ({action}) via {source}";
                await userMessage.ModifyAsync(props =>
                {
                    props.Components = new ComponentBuilder().Build();
                    props.Embed = BuildHandledEmbed(userMessage.Embeds.FirstOrDefault(), handled, action == "add" ? "approve" : "dismiss");
                });
                ScheduleChannelMessageDelete(
                    db,
                    update.PlayerId,
                    chProp.GetUInt64(),
                    msgProp.GetUInt64(),
                    "TEMPLE_MISSING_DISCORD_DELETE_SCHEDULED",
                    new { Reason = "temple-missing-action-handled-web", Action = action });
            }
            await CloseOpenLifecycleEventsAsync(db, update.PlayerId,
                "NEW_PLAYER",
                "MERGE_SUGGESTED",
                "DISCORD_MARK_RENAME_SUSPECT",
                "MISSING_IN_ROSTER",
                "TEMPLE_MISSING_ACTION_REQUIRED",
                "WOM_MISSING_ACTION_REQUIRED");
            update.Status = "DONE";
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task ProcessReviewCardRequeueRequests(CancellationToken ct)
    {
        if (_client is null) return;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();

        var requests = await db.LifecycleEvents
            .Where(x => x.EventType == "DISCORD_REVIEW_REQUEUE_REQUESTED" && x.Status == "OPEN")
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);
        if (requests.Count == 0) return;

        foreach (var request in requests)
        {
            var postedEvents = await db.LifecycleEvents
                .Where(x =>
                    x.PlayerId == request.PlayerId &&
                    (x.EventType == "TEMPLE_MISSING_DISCORD_POSTED" ||
                     x.EventType == "WOM_MISSING_DISCORD_POSTED" ||
                     x.EventType == "WOM_ONLY_DISCORD_POSTED" ||
                     x.EventType == "WOM_RANK_MISMATCH_DISCORD_POSTED" ||
                     x.EventType == "MERGE_DISCORD_POSTED"))
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync(ct);

            foreach (var postedEvent in postedEvents)
            {
                try
                {
                    using var doc = JsonDocument.Parse(postedEvent.MetadataJson);
                    if (!TryReadUlong(doc.RootElement, "ChannelId", out var channelId) ||
                        !TryReadUlong(doc.RootElement, "DiscordMessageId", out var messageId))
                    {
                        postedEvent.Status = "DONE";
                        continue;
                    }

                    await DeletePostedMessageIfFoundAsync(channelId, messageId);
                    await RecordMissingTrackedMessageEventAsync(
                        db,
                        request.PlayerId,
                        "manual-requeue",
                        postedEvent.Id,
                        ExtractInt(postedEvent.MetadataJson, "RequiredEventId"),
                        channelId,
                        messageId,
                        "manual-requeue-request",
                        ct);
                }
                catch
                {
                    postedEvent.Status = "DONE";
                }
            }

            request.Status = "DONE";
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task ReconcileConfirmedTempleNameChangesAsync(TrackerDbContext db, CancellationToken ct)
    {
        var openRequirements = await db.LifecycleEvents
            .Where(x => x.EventType == TempleNameChangeReviewEventTypes.Required && x.Status == "OPEN")
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);
        if (openRequirements.Count == 0) return;

        var confirmedRequirements = openRequirements
            .Where(x => IsTempleNameChangeConfirmed(ReadLifecycleMetadata(x.MetadataJson)))
            .ToList();
        if (confirmedRequirements.Count == 0) return;

        var templeGroupId = configuration.GetValue<int?>("TempleOsrs:GroupId") ?? 449;
        var templeRoster = await TryGetTempleGroupUsernamesAsync(templeGroupId, ct);
        if (!templeRoster.Succeeded) return;

        foreach (var requiredEvent in confirmedRequirements)
        {
            var metadata = ReadLifecycleMetadata(requiredEvent.MetadataJson);
            if (!IsTempleNameChangeConfirmed(metadata)) continue;

            var previousUsername = NormalizeUsername(PickLifecycleValue(metadata, "PreviousUsername") ?? "");
            var newUsername = NormalizeUsername(PickLifecycleValue(metadata, "NewUsername") ?? "");
            if (string.IsNullOrWhiteSpace(previousUsername) || string.IsNullOrWhiteSpace(newUsername)) continue;

            // Do not resolve based on a failed/partial HTTP response. The new name must
            // be present and the old name must be absent in the same successful roster read.
            if (!templeRoster.Usernames.Contains(newUsername) || templeRoster.Usernames.Contains(previousUsername)) continue;

            await ReconcileVerifiedTempleNameChangeAsync(
                db,
                requiredEvent,
                metadata,
                previousUsername,
                newUsername,
                ct);
        }
    }

    private async Task ReconcileVerifiedTempleNameChangeAsync(
        TrackerDbContext db,
        LifecycleEvent requiredEvent,
        Dictionary<string, string> metadata,
        string previousUsername,
        string newUsername,
        CancellationToken ct)
    {
        var previousPlayerId = ExtractInt(requiredEvent.MetadataJson, "PreviousPlayerId") ?? requiredEvent.PlayerId;
        var previousPlayer = await db.Players.FirstOrDefaultAsync(x => x.Id == previousPlayerId, ct);
        if (previousPlayer is null)
        {
            previousPlayer = await db.Players.FirstOrDefaultAsync(
                x => x.Username.ToLower() == previousUsername.ToLower(),
                ct);
        }

        // A confirmed rename without its historical row cannot be reconciled safely.
        // Keep the requirement open, but do not re-post it as a reminder.
        if (previousPlayer is null)
        {
            logger.LogWarning(
                "Temple rename verification found {NewUsername}, but historical player {PreviousUsername} was not found. Leaving requirement {RequiredEventId} open.",
                newUsername,
                previousUsername,
                requiredEvent.Id);
            return;
        }
        var matchingNewPlayers = await db.Players
            .Where(x =>
                x.Id != previousPlayer.Id &&
                x.Status != PlayerStatus.REMOVED_CONFIRMED &&
                x.Username.ToLower() == newUsername.ToLower())
            .OrderBy(x => x.Id)
            .ToListAsync(ct);

        // Multiple rows with the same current name require a human decision. Do not
        // guess which history should be merged.
        if (matchingNewPlayers.Count > 1)
        {
            logger.LogWarning(
                "Temple rename verification found multiple tracker rows for {NewUsername}; leaving requirement {RequiredEventId} open.",
                newUsername,
                requiredEvent.Id);
            return;
        }

        var newPlayer = matchingNewPlayers.SingleOrDefault();
        if (newPlayer is not null)
        {
            // Reuse the existing merge-review action. The live check only creates the
            // review; the existing Confirm rename button remains the data-changing step.
            newPlayer.Status = PlayerStatus.MERGE_SUGGESTED;
            await CloseOpenLifecycleEventsAsync(db, newPlayer.Id, "NEW_PLAYER", "WOM_ONLY_ACTION_REQUIRED");
            await EnsureOpenVerifiedRenameMergeReviewAsync(
                db,
                previousPlayer,
                newPlayer,
                requiredEvent,
                previousUsername,
                newUsername,
                ct);
            await SupersedePendingPromotionCardsForMergeReviewAsync(
                db,
                previousPlayer,
                newPlayer,
                "merge-review-created",
                ct);
            await CloseOpenTempleNameChangeForMergeAsync(db, previousUsername, newUsername, newPlayer.Id, ct);
            return;
        }

        if (previousPlayer.Status == PlayerStatus.REMOVED_CONFIRMED)
        {
            logger.LogWarning(
                "Temple rename verification found {NewUsername}, but historical player {PreviousUsername} is already removed and no duplicate row exists. Leaving requirement {RequiredEventId} open.",
                newUsername,
                previousUsername,
                requiredEvent.Id);
            return;
        }

        // If sync never created a second row, retain the historical snapshots by
        // updating that one row in place after the same explicit confirmation + live check.
        previousPlayer.Username = newUsername;
        previousPlayer.Status = PlayerStatus.ACTIVE;
        previousPlayer.LastSeen = DateTimeOffset.UtcNow;
        await CloseOpenLifecycleEventsAsync(
            db,
            previousPlayer.Id,
            "MISSING_IN_ROSTER",
            "TEMPLE_MISSING_ACTION_REQUIRED",
            "WOM_MISSING_ACTION_REQUIRED");
        await CloseOpenWomOnlyRequiredEventsAsync(db, newUsername);

        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = previousPlayer.Id,
            EventType = TempleNameChangeResolvedByLiveCheckEventType,
            MetadataJson = JsonUtil.Serialize(new
            {
                PreviousUsername = previousUsername,
                NewUsername = newUsername,
                RequiredEventId = requiredEvent.Id,
                Source = "temple-live-check"
            }),
            Status = "DONE",
            CreatedAt = DateTimeOffset.UtcNow
        });
        MarkTempleNameChangeResolvedByLiveCheck(requiredEvent, metadata);
    }

    private async Task SupersedePendingPromotionCardsForMergeReviewAsync(
        TrackerDbContext db,
        Player previousPlayer,
        Player newPlayer,
        string source,
        CancellationToken ct)
    {
        var playerIds = new[] { previousPlayer.Id, newPlayer.Id }.Distinct().ToArray();
        var pendingCandidates = await db.PromotionCandidates
            .Where(x => x.Status == PromotionStatus.PENDING && playerIds.Contains(x.PlayerId))
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);
        if (pendingCandidates.Count == 0) return;

        var postedEvents = await db.LifecycleEvents
            .Where(x => x.EventType == "PROMOTION_DISCORD_POSTED")
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

        foreach (var candidate in pendingCandidates)
        {
            foreach (var postedEvent in postedEvents.Where(x => MetadataIntEquals(x.MetadataJson, "CandidateId", candidate.Id)))
            {
                if (IsPromotionPostedSupersededByMerge(postedEvent)) continue;

                await SupersedePromotionCardForMergeAsync(
                    db,
                    postedEvent,
                    candidate.Id,
                    candidate.PlayerId,
                    source,
                    null,
                    ct);
            }
        }
    }

    private static async Task EnsureOpenVerifiedRenameMergeReviewAsync(
        TrackerDbContext db,
        Player previousPlayer,
        Player newPlayer,
        LifecycleEvent requiredEvent,
        string previousUsername,
        string newUsername,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var metadata = JsonUtil.Serialize(new
        {
            NewPlayer = newUsername,
            SuggestedPrevious = previousUsername,
            PreviousPlayerId = previousPlayer.Id,
            NewPlayerId = newPlayer.Id,
            TempleNameChangeRequiredEventId = requiredEvent.Id,
            Source = "temple-name-change-live-verification",
            ConfirmedAt = PickLifecycleValue(ReadLifecycleMetadata(requiredEvent.MetadataJson), "ConfirmedAt"),
            DetectedAt = now
        });

        var suggestedEvent = await db.LifecycleEvents.FirstOrDefaultAsync(x =>
            x.PlayerId == newPlayer.Id &&
            x.EventType == "MERGE_SUGGESTED" &&
            x.Status == "OPEN", ct);
        if (suggestedEvent is null)
        {
            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = newPlayer.Id,
                EventType = "MERGE_SUGGESTED",
                MetadataJson = metadata,
                Status = "OPEN",
                CreatedAt = now
            });
        }
        else
        {
            suggestedEvent.MetadataJson = metadata;
        }

        var requiredMergeEvent = await db.LifecycleEvents.FirstOrDefaultAsync(x =>
            x.PlayerId == newPlayer.Id &&
            x.EventType == "MERGE_ACTION_REQUIRED" &&
            x.Status == "OPEN", ct);
        if (requiredMergeEvent is null)
        {
            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = newPlayer.Id,
                EventType = "MERGE_ACTION_REQUIRED",
                MetadataJson = metadata,
                Status = "OPEN",
                CreatedAt = now
            });
        }
        else
        {
            requiredMergeEvent.MetadataJson = metadata;
        }
    }

    private static void MarkTempleNameChangeResolvedByLiveCheck(
        LifecycleEvent requiredEvent,
        Dictionary<string, string> metadata)
    {
        var verifiedAt = DateTimeOffset.UtcNow;
        metadata["TempleVerifiedAt"] = verifiedAt.ToString("O", CultureInfo.InvariantCulture);
        metadata["ResolutionSource"] = "temple-live-check";
        requiredEvent.MetadataJson = JsonUtil.Serialize(metadata);
        requiredEvent.Status = "DONE";
    }

    private async Task PostTempleNameChangeNeededMessages(CancellationToken ct)
    {
        if (_client is null) return;
        var channel = await ResolveMessageChannelAsync(_options.ChannelId);
        if (channel is null) return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();

        // A confirmed rename is only considered complete after Temple confirms the new
        // name is present and the old name is absent. This also gives us a safe point
        // to reconcile the two tracker rows created by a historical rename.
        await ReconcileConfirmedTempleNameChangesAsync(db, ct);
        await db.SaveChangesAsync(ct);

        var detectionInput = await BuildTempleNameChangeDetectionInputAsync(db, ct);
        var detection = DetectUniqueTempleNameChangeCandidate(detectionInput);
        if (detection is null)
        {
            var openRequirements = await db.LifecycleEvents
                .Where(x => x.EventType == TempleNameChangeReviewEventTypes.Required && x.Status == "OPEN")
                .ToListAsync(ct);
            foreach (var openRequirement in openRequirements)
            {
                await SuppressTempleNameChangeNoisyCardsAsync(db, openRequirement, ct);
            }
            if (openRequirements.Count > 0) await db.SaveChangesAsync(ct);
            return;
        }

        var requiredEvent = await UpsertTempleNameChangeRequiredEventAsync(db, detection, ct);
        if (requiredEvent is null) return;

        await db.SaveChangesAsync(ct);
        await SuppressTempleNameChangeNoisyCardsAsync(db, requiredEvent, ct);

        var requiredMetadata = ReadLifecycleMetadata(requiredEvent.MetadataJson);
        if (IsTempleNameChangeConfirmed(requiredMetadata))
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        var renderFingerprint = ComputeRenderFingerprint(new
        {
            Type = "temple-name-change-needed-card",
            RequiredEventId = requiredEvent.Id,
            detection.PreviousPlayerId,
            detection.PreviousUsername,
            detection.NewUsername,
            detection.Rank,
            detection.WomRole,
            detection.WomMissingEventId,
            detection.TempleMissingEventId,
            detection.WomOnlyEventId
        });

        var postedEvents = await db.LifecycleEvents
            .Where(x => x.EventType == TempleNameChangeReviewEventTypes.DiscordPosted)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
        var postedEvent = postedEvents.FirstOrDefault(x => MetadataIntEquals(x.MetadataJson, "RequiredEventId", requiredEvent.Id));
        var previousDiscordGuess = await GuessDiscordMemberForNamesAsync([detection.PreviousUsername], ct);
        var newDiscordGuess = await GuessDiscordMemberForNamesAsync([detection.NewUsername], ct);
        renderFingerprint = ComputeRenderFingerprint(new
        {
            Type = "temple-name-change-needed-card",
            RequiredEventId = requiredEvent.Id,
            detection.PreviousPlayerId,
            detection.PreviousUsername,
            detection.NewUsername,
            detection.Rank,
            detection.WomRole,
            detection.WomMissingEventId,
            detection.TempleMissingEventId,
            detection.WomOnlyEventId,
            PreviousDiscordGuess = FormatDiscordGuessForFingerprint(previousDiscordGuess),
            NewDiscordGuess = FormatDiscordGuessForFingerprint(newDiscordGuess)
        });
        var embed = BuildTempleNameChangeNeededEmbed(
            detection.PreviousUsername,
            detection.NewUsername,
            detection.Rank,
            detection.WomRole,
            previousDiscordGuess,
            newDiscordGuess);
        var components = BuildTempleNameChangeNeededComponents(requiredEvent.Id);

        if (postedEvent is not null)
        {
            var lookupKey = $"temple-name-change:{requiredEvent.Id}";
            if (IsLookupBackoffActive(lookupKey)) return;
            var (lookupState, liveDiscordMessage, channelId, messageId) = await TryGetPostedUserMessageAsync(postedEvent, lookupKey);
            if (lookupState == PostedMessageLookupState.Unknown)
            {
                SetLookupBackoff(lookupKey);
                return;
            }
            if (lookupState == PostedMessageLookupState.Malformed)
            {
                postedEvent.Status = "DONE";
            }
            else if (lookupState == PostedMessageLookupState.Found && liveDiscordMessage is not null)
            {
                if (!ShouldSkipMessagePatch(liveDiscordMessage.Id, renderFingerprint))
                {
                    await liveDiscordMessage.ModifyAsync(props =>
                    {
                        props.Embed = embed;
                        props.Components = components;
                    });
                    RecordMessagePatched(liveDiscordMessage.Id, renderFingerprint);
                }
                postedEvent.MetadataJson = JsonUtil.Serialize(new
                {
                    RequiredEventId = requiredEvent.Id,
                    PreviousUsername = detection.PreviousUsername,
                    NewUsername = detection.NewUsername,
                    PreviousDiscordGuess = FormatDiscordGuessForFingerprint(previousDiscordGuess),
                    NewDiscordGuess = FormatDiscordGuessForFingerprint(newDiscordGuess),
                    ChannelId = liveDiscordMessage.Channel.Id,
                    DiscordMessageId = liveDiscordMessage.Id,
                    RenderFingerprint = renderFingerprint
                });
                await db.SaveChangesAsync(ct);
                return;
            }
            else if (lookupState == PostedMessageLookupState.Missing)
            {
                await RecordMissingTrackedMessageEventAsync(
                    db,
                    requiredEvent.PlayerId,
                    "temple-name-change",
                    postedEvent.Id,
                    requiredEvent.Id,
                    channelId,
                    messageId,
                    "post-temple-name-change",
                    ct);
            }
        }

        var lease = await TryAcquirePostLeaseAsync(db, requiredEvent.PlayerId, $"temple-name-change:{requiredEvent.Id}", ct);
        if (lease is null) return;
        try
        {
            await db.Entry(requiredEvent).ReloadAsync(ct);
            if (requiredEvent.Status != "OPEN") return;
            var latestMetadata = ReadLifecycleMetadata(requiredEvent.MetadataJson);
            if (IsTempleNameChangeConfirmed(latestMetadata)) return;

            var msg = await channel.SendMessageAsync(embed: embed, components: components);
            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = requiredEvent.PlayerId,
                EventType = TempleNameChangeReviewEventTypes.DiscordPosted,
                MetadataJson = JsonUtil.Serialize(new
                {
                    RequiredEventId = requiredEvent.Id,
                    PreviousUsername = detection.PreviousUsername,
                    NewUsername = detection.NewUsername,
                    PreviousDiscordGuess = FormatDiscordGuessForFingerprint(previousDiscordGuess),
                    NewDiscordGuess = FormatDiscordGuessForFingerprint(newDiscordGuess),
                    ChannelId = _options.ChannelId,
                    DiscordMessageId = msg.Id,
                    RenderFingerprint = renderFingerprint
                }),
                Status = "DONE",
                CreatedAt = DateTimeOffset.UtcNow
            });
            RecordMessagePatched(msg.Id, renderFingerprint);
        }
        finally
        {
            lease.Status = "DONE";
            await db.SaveChangesAsync(ct);
        }
    }

    private static TempleNameChangeDetection? DetectUniqueTempleNameChangeCandidate(TempleNameChangeDetectionInput input)
    {
        var detections = DetectTempleNameChangeCandidates(input);
        return detections.Count == 1 ? detections[0] : null;
    }

    private static IReadOnlyList<TempleNameChangeDetection> DetectTempleNameChangeCandidates(TempleNameChangeDetectionInput input)
    {
        var detections = input.WomOnlyCandidates
            .Select(candidate => TempleNameChangeDetector.Detect(input with { WomOnlyCandidates = [candidate] }))
            .Where(x => x is not null)
            .Select(x => x!)
            .GroupBy(x => $"{x.PreviousPlayerId}:{NormalizeUsername(x.NewUsername).ToLowerInvariant()}")
            .Select(x => x.First())
            .ToList();

        var ambiguousPreviousPlayerIds = detections
            .GroupBy(x => x.PreviousPlayerId)
            .Where(x => x.Select(candidate => NormalizeUsername(candidate.NewUsername)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(x => x.Key)
            .ToHashSet();

        return detections
            .Where(x => !ambiguousPreviousPlayerIds.Contains(x.PreviousPlayerId))
            .ToList();
    }

    private async Task<TempleNameChangeDetection?> TryPrepareRenameReviewBeforeSensitiveActionAsync(
        TrackerDbContext db,
        int? expectedPreviousPlayerId,
        string? expectedNewUsername,
        string? expectedEitherUsername,
        CancellationToken ct)
    {
        var input = await BuildTempleNameChangeDetectionInputAsync(db, ct);
        var normalizedNewUsername = NormalizeUsername(expectedNewUsername ?? "");
        var normalizedEitherUsername = NormalizeUsername(expectedEitherUsername ?? "");
        var detection = DetectTempleNameChangeCandidates(input).FirstOrDefault(x =>
            (expectedPreviousPlayerId.HasValue && x.PreviousPlayerId == expectedPreviousPlayerId.Value) ||
            (!string.IsNullOrWhiteSpace(normalizedNewUsername) &&
             string.Equals(NormalizeUsername(x.NewUsername), normalizedNewUsername, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(normalizedEitherUsername) &&
             (string.Equals(NormalizeUsername(x.PreviousUsername), normalizedEitherUsername, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(NormalizeUsername(x.NewUsername), normalizedEitherUsername, StringComparison.OrdinalIgnoreCase))));
        if (detection is null) return null;

        await UpsertTempleNameChangeRequiredEventAsync(db, detection, ct);
        await db.SaveChangesAsync(ct);
        return detection;
    }

    private async Task BlockSensitiveActionForRenameAsync(
        TrackerDbContext db,
        SocketMessageComponent component,
        TempleNameChangeDetection detection,
        int playerId,
        CancellationToken ct)
    {
        var message = BuildRenameGuardMessage(detection);
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = detection.PreviousPlayerId,
            EventType = "RENAME_ACTION_GUARD_BLOCKED",
            MetadataJson = JsonUtil.Serialize(new
            {
                PreviousUsername = detection.PreviousUsername,
                NewUsername = detection.NewUsername,
                PreviousPlayerId = detection.PreviousPlayerId,
                BlockedActionPlayerId = playerId,
                Source = "discord-action-guard",
                ChannelId = component.Channel.Id,
                DiscordMessageId = component.Message.Id
            }),
            Status = "DONE",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);

        await UpdateComponentMessageAsync(
            component,
            BuildHandledEmbed(component.Message.Embeds.FirstOrDefault(), message, "rename"));
        ScheduleChannelMessageDelete(
            db,
            playerId,
            component.Channel.Id,
            component.Message.Id,
            "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED",
            new { Reason = "rename-candidate-guarded-action" });
        await db.SaveChangesAsync(ct);
    }

    private async Task<TempleNameChangeDetection?> TryPrepareRenameReviewForCommandAsync(
        IReadOnlyList<string> usernames,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        foreach (var username in usernames)
        {
            var detection = await TryPrepareRenameReviewBeforeSensitiveActionAsync(
                db,
                expectedPreviousPlayerId: null,
                expectedNewUsername: null,
                expectedEitherUsername: username,
                ct);
            if (detection is not null) return detection;
        }

        return null;
    }

    private static string BuildRenameGuardMessage(TempleNameChangeDetection detection) =>
        $"Possible rename detected: `{detection.PreviousUsername}` -> `{detection.NewUsername}`. No action was applied; check the existing rename review.";

    private async Task<TempleNameChangeDetectionInput> BuildTempleNameChangeDetectionInputAsync(TrackerDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var recentCutoff = now - TempleNameChangeDetectionWindow;
        var recentEvents = await db.LifecycleEvents
            .Where(x =>
                x.CreatedAt >= recentCutoff &&
                (x.EventType == "WOM_MISSING_ACTION_REQUIRED" ||
                 x.EventType == "TEMPLE_MISSING_ACTION_REQUIRED" ||
                 x.EventType == "MISSING_IN_ROSTER" ||
                 x.EventType == "WOM_ONLY_ACTION_REQUIRED" ||
                 x.EventType == "WOM_ONLY_IGNORED" ||
                 x.EventType == "MERGE_ACTION_REQUIRED" ||
                 x.EventType == "WOM_MISSING_ACTION_APPLIED" ||
                 x.EventType == "TEMPLE_MISSING_ACTION_APPLIED"))
            .ToListAsync(ct);

        var oldCandidateEvents = recentEvents
            .Where(x => x.EventType is "WOM_MISSING_ACTION_REQUIRED" or "TEMPLE_MISSING_ACTION_REQUIRED" or "MISSING_IN_ROSTER")
            .ToList();
        var removedPlayerIds = recentEvents
            .Where(x => x.EventType is "WOM_MISSING_ACTION_APPLIED" or "TEMPLE_MISSING_ACTION_APPLIED")
            .Where(x => string.Equals(PickLifecycleValue(ReadLifecycleMetadata(x.MetadataJson), "Action"), "remove", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.PlayerId)
            .ToHashSet();
        var playerIds = oldCandidateEvents
            .Where(x => x.EventType is "WOM_MISSING_ACTION_REQUIRED" or "TEMPLE_MISSING_ACTION_REQUIRED")
            .Select(x => x.PlayerId)
            .Concat(recentEvents
                .Where(x => x.EventType == "MISSING_IN_ROSTER")
                .Select(x => x.PlayerId))
            .Distinct()
            .ToList();
        var players = await db.Players
            .Where(x => playerIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Username, x.CurrentRank, x.Status })
            .ToListAsync(ct);

        var oldCandidates = players
            .Where(x =>
                x.Status == PlayerStatus.MISSING_PENDING_REVIEW ||
                (x.Status == PlayerStatus.REMOVED_CONFIRMED && removedPlayerIds.Contains(x.Id)))
            .Select(x =>
            {
                var womMissing = oldCandidateEvents
                    .Where(ev => ev.PlayerId == x.Id && ev.EventType == "WOM_MISSING_ACTION_REQUIRED")
                    .OrderByDescending(ev => ev.CreatedAt)
                    .FirstOrDefault();
                var templeMissing = oldCandidateEvents
                    .Where(ev => ev.PlayerId == x.Id && ev.EventType == "TEMPLE_MISSING_ACTION_REQUIRED")
                    .OrderByDescending(ev => ev.CreatedAt)
                    .FirstOrDefault();
                var rosterMissing = oldCandidateEvents
                    .Where(ev => ev.PlayerId == x.Id && ev.EventType == "MISSING_IN_ROSTER")
                    .OrderByDescending(ev => ev.CreatedAt)
                    .FirstOrDefault();
                return new TempleNameChangeOldCandidate(
                    x.Id,
                    x.Username,
                    x.CurrentRank,
                    womMissing?.Id,
                    womMissing?.CreatedAt ?? rosterMissing?.CreatedAt,
                    templeMissing?.Id,
                    templeMissing?.CreatedAt ?? rosterMissing?.CreatedAt);
            })
            .Where(x =>
                (x.WomMissingCreatedAt.HasValue && x.WomMissingCreatedAt >= recentCutoff) ||
                (x.TempleMissingCreatedAt.HasValue && x.TempleMissingCreatedAt >= recentCutoff))
            .ToList();

        var ignoredUsernames = recentEvents
            .Where(x => x.EventType == "WOM_ONLY_IGNORED" && x.Status == "OPEN")
            .Select(x => NormalizeUsername(PickLifecycleValue(ReadLifecycleMetadata(x.MetadataJson), "Username", "Player") ?? ""))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var womOnlyCandidates = recentEvents
            .Where(x => x.EventType == "WOM_ONLY_ACTION_REQUIRED")
            .Select(x =>
            {
                var metadata = ReadLifecycleMetadata(x.MetadataJson);
                var username = PickLifecycleValue(metadata, "Username", "Player") ?? "";
                var role = PickLifecycleValue(metadata, "ActualWomRole") ?? "";
                return new TempleNameChangeWomOnlyCandidate(x.Id, username, role, x.CreatedAt);
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Username))
            .Where(x => !ignoredUsernames.Contains(NormalizeUsername(x.Username)))
            .ToList();

        var openMerges = recentEvents
            .Where(x => x.EventType == "MERGE_ACTION_REQUIRED" && x.Status == "OPEN")
            .Select(x =>
            {
                var metadata = ReadLifecycleMetadata(x.MetadataJson);
                return new TempleNameChangeOpenMerge(
                    PickLifecycleValue(metadata, "SuggestedPrevious", "PreviousPlayer"),
                    PickLifecycleValue(metadata, "NewPlayer", "Username", "Player"));
            })
            .ToList();

        var handledEvents = await db.LifecycleEvents
            .Where(x => x.EventType == TempleNameChangeReviewEventTypes.ActionApplied && x.CreatedAt >= recentCutoff)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
        var handledPairs = handledEvents
            .Select(x =>
            {
                var metadata = ReadLifecycleMetadata(x.MetadataJson);
                return new TempleNameChangeHandledPair(
                    PickLifecycleValue(metadata, "PreviousUsername") ?? "",
                    PickLifecycleValue(metadata, "NewUsername") ?? "",
                    PickLifecycleValue(metadata, "Action") ?? "",
                    x.CreatedAt);
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.PreviousUsername) && !string.IsNullOrWhiteSpace(x.NewUsername))
            .ToList();

        return new TempleNameChangeDetectionInput(
            now,
            TempleNameChangeDetectionWindow,
            oldCandidates,
            womOnlyCandidates,
            openMerges,
            handledPairs);
    }

    private async Task<LifecycleEvent?> UpsertTempleNameChangeRequiredEventAsync(
        TrackerDbContext db,
        TempleNameChangeDetection detection,
        CancellationToken ct)
    {
        var openRequirements = await db.LifecycleEvents
            .Where(x => x.EventType == TempleNameChangeReviewEventTypes.Required && x.Status == "OPEN")
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);
        var matching = openRequirements.FirstOrDefault(x =>
        {
            var metadata = ReadLifecycleMetadata(x.MetadataJson);
            return string.Equals(NormalizeUsername(PickLifecycleValue(metadata, "PreviousUsername") ?? ""), NormalizeUsername(detection.PreviousUsername), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizeUsername(PickLifecycleValue(metadata, "NewUsername") ?? ""), NormalizeUsername(detection.NewUsername), StringComparison.OrdinalIgnoreCase);
        });

        var metadataJson = JsonUtil.Serialize(new
        {
            detection.PreviousUsername,
            detection.NewUsername,
            detection.PreviousPlayerId,
            detection.Rank,
            detection.WomRole,
            detection.WomMissingEventId,
            detection.TempleMissingEventId,
            WomOnlyEventId = detection.WomOnlyEventId,
            RelatedRequirementEventIds = new[] { detection.WomMissingEventId, detection.TempleMissingEventId, detection.WomOnlyEventId }
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .Distinct()
                .OrderBy(x => x)
                .ToArray(),
            Source = "discord-worker-conservative-detection",
            DetectedAt = DateTimeOffset.UtcNow
        });

        if (matching is not null)
        {
            var existingMetadata = ReadLifecycleMetadata(matching.MetadataJson);
            if (!IsTempleNameChangeConfirmed(existingMetadata))
            {
                matching.MetadataJson = metadataJson;
            }
            return matching;
        }

        var required = new LifecycleEvent
        {
            PlayerId = detection.PreviousPlayerId,
            EventType = TempleNameChangeReviewEventTypes.Required,
            MetadataJson = metadataJson,
            Status = "OPEN",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.LifecycleEvents.Add(required);
        return required;
    }

    private async Task SuppressTempleNameChangeNoisyCardsAsync(TrackerDbContext db, LifecycleEvent requiredEvent, CancellationToken ct)
    {
        var metadata = ReadLifecycleMetadata(requiredEvent.MetadataJson);
        var previousUsername = NormalizeUsername(PickLifecycleValue(metadata, "PreviousUsername") ?? "");
        var newUsername = NormalizeUsername(PickLifecycleValue(metadata, "NewUsername") ?? "");
        var previousPlayerId = ExtractInt(requiredEvent.MetadataJson, "PreviousPlayerId") ?? requiredEvent.PlayerId;
        var womOnlyEventId = ExtractInt(requiredEvent.MetadataJson, "WomOnlyEventId");
        if (string.IsNullOrWhiteSpace(previousUsername) || string.IsNullOrWhiteSpace(newUsername)) return;

        var postedEvents = await db.LifecycleEvents
            .Where(x =>
                (x.PlayerId == previousPlayerId &&
                    (x.EventType == "WOM_MISSING_DISCORD_POSTED" ||
                     x.EventType == "TEMPLE_MISSING_DISCORD_POSTED")) ||
                x.EventType == "WOM_ONLY_DISCORD_POSTED")
            .OrderByDescending(x => x.CreatedAt)
            .Take(100)
            .ToListAsync(ct);

        foreach (var posted in postedEvents)
        {
            if (posted.EventType == "WOM_ONLY_DISCORD_POSTED")
            {
                var postedMetadata = ReadLifecycleMetadata(posted.MetadataJson);
                var postedUsername = NormalizeUsername(PickLifecycleValue(postedMetadata, "Username", "Player") ?? "");
                var postedRequiredId = ExtractInt(posted.MetadataJson, "RequiredEventId");
                if (!string.Equals(postedUsername, newUsername, StringComparison.OrdinalIgnoreCase) &&
                    (!womOnlyEventId.HasValue || postedRequiredId != womOnlyEventId.Value))
                {
                    continue;
                }
            }

            await SuppressTempleNameChangeNoisyCardAsync(db, requiredEvent, posted, previousUsername, newUsername, ct);
        }
    }

    private async Task SuppressTempleNameChangeNoisyCardAsync(
        TrackerDbContext db,
        LifecycleEvent requiredEvent,
        LifecycleEvent postedEvent,
        string previousUsername,
        string newUsername,
        CancellationToken ct,
        string? suppressionDescription = null)
    {
        var alreadySuppressed = await db.LifecycleEvents
            .Where(x => x.EventType == TempleNameChangeReviewEventTypes.SuppressedCard)
            .ToListAsync(ct);
        if (alreadySuppressed.Any(x => ExtractInt(x.MetadataJson, "PostedEventId") == postedEvent.Id)) return;

        var channelId = ExtractUlong(postedEvent.MetadataJson, "ChannelId");
        var messageId = ExtractUlong(postedEvent.MetadataJson, "DiscordMessageId");
        var lookupKey = $"temple-name-change-suppress:{postedEvent.Id}";
        var (lookupState, liveDiscordMessage, resolvedChannelId, resolvedMessageId) = await TryGetPostedUserMessageAsync(postedEvent, lookupKey);
        if (lookupState == PostedMessageLookupState.Unknown)
        {
            SetLookupBackoff(lookupKey);
        }
        else if (lookupState == PostedMessageLookupState.Malformed)
        {
            postedEvent.Status = "DONE";
            return;
        }

        if (lookupState == PostedMessageLookupState.Found && liveDiscordMessage is not null)
        {
            await liveDiscordMessage.ModifyAsync(props =>
            {
                props.Components = new ComponentBuilder().Build();
                props.Embed = BuildHandledEmbed(
                    liveDiscordMessage.Embeds.FirstOrDefault(),
                    suppressionDescription ?? $"Suppressed by Temple name change review: {previousUsername} -> {newUsername}",
                    "rename");
            });
            resolvedChannelId = liveDiscordMessage.Channel.Id;
            resolvedMessageId = liveDiscordMessage.Id;
        }

        var finalChannelId = resolvedChannelId ?? channelId;
        var finalMessageId = resolvedMessageId ?? messageId;
        if (finalChannelId.HasValue && finalMessageId.HasValue)
        {
            var now = DateTimeOffset.UtcNow;
            ScheduleChannelMessageDelete(
                db,
                requiredEvent.PlayerId,
                finalChannelId.Value,
                finalMessageId.Value,
                "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED",
                new
                {
                    Reason = "temple-name-change-suppressed-card",
                    PostedEventId = postedEvent.Id,
                    postedEvent.EventType,
                    PreviousUsername = previousUsername,
                    NewUsername = newUsername
                },
                now.AddSeconds(10),
                now.AddMinutes(1),
                dedupeCompletedSchedules: false);
        }

        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = requiredEvent.PlayerId,
            EventType = TempleNameChangeReviewEventTypes.SuppressedCard,
            MetadataJson = JsonUtil.Serialize(new
            {
                RequiredEventId = requiredEvent.Id,
                PostedEventId = postedEvent.Id,
                postedEvent.EventType,
                PreviousUsername = previousUsername,
                NewUsername = newUsername,
                ChannelId = finalChannelId,
                DiscordMessageId = finalMessageId
            }),
            Status = "DONE",
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private async Task PostMergeActionMessages(CancellationToken ct)
    {
        if (_client is null) return;
        var channel = _client.GetChannel(_options.ChannelId) as IMessageChannel;
        if (channel is null) return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var requiredEvents = await db.LifecycleEvents
            .Where(x => x.EventType == "MERGE_ACTION_REQUIRED" && x.Status == "OPEN")
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);

        foreach (var ev in requiredEvents)
        {
            var mergePostedEvents = await db.LifecycleEvents
                .Where(x => x.EventType == "MERGE_DISCORD_POSTED")
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync(ct);
            var posted = mergePostedEvents.FirstOrDefault(x => MetadataIntEquals(x.MetadataJson, "RequiredEventId", ev.Id));
            if (posted is not null) continue;

            var metadata = ReadLifecycleMetadata(ev.MetadataJson);
            var newPlayer = PickLifecycleValue(metadata, "NewPlayer") ?? $"Player #{ev.PlayerId}";
            var suggested = PickLifecycleValue(metadata, "SuggestedPrevious") ?? "Unknown";
            if (!string.IsNullOrWhiteSpace(suggested))
            {
                await CloseOpenTempleNameChangeForMergeAsync(db, suggested, newPlayer, ev.PlayerId, ct);
                await SuppressTempleMissingForMergeAsync(db, suggested, ev.PlayerId, ct);
            }
            var newPlayerDiscordGuess = await GuessDiscordMemberForNamesAsync([newPlayer], ct);
            var previousPlayerDiscordGuess = await GuessDiscordMemberForNamesAsync([suggested], ct);
            var embedBuilder = new EmbedBuilder()
                .WithTitle("Possible Rename Review")
                .WithColor(new Color(234, 179, 8))
                .WithDescription($"New: `{newPlayer}`\nSuggested previous: `{suggested}`\nChoose how to resolve this rename review.\nIf confirmed/reassigned and the old name has unknown WOM role, old-name WOM cleanup is attempted automatically.");
            AddDiscordGuessField(embedBuilder, "New Player", newPlayerDiscordGuess);
            AddDiscordGuessField(embedBuilder, "Suggested Previous", previousPlayerDiscordGuess);
            var embed = embedBuilder.Build();
            var components = new ComponentBuilder()
                .WithButton("Confirm rename", $"merge:confirm:{ev.PlayerId}", ButtonStyle.Success)
                .WithButton("Choose other candidate", $"merge:choose:{ev.PlayerId}", ButtonStyle.Primary)
                .WithButton("Manual previous name", $"merge:manual:{ev.PlayerId}", ButtonStyle.Secondary)
                .WithButton("Abort rename", $"merge:abort:{ev.PlayerId}", ButtonStyle.Danger)
                .Build();
            var msg = await channel.SendMessageAsync(embed: embed, components: components);
            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = ev.PlayerId,
                EventType = "MERGE_DISCORD_POSTED",
                MetadataJson = JsonUtil.Serialize(new
                {
                    RequiredEventId = ev.Id,
                    NewPlayer = newPlayer,
                    SuggestedPrevious = suggested,
                    NewPlayerDiscordGuess = FormatDiscordGuessForFingerprint(newPlayerDiscordGuess),
                    SuggestedPreviousDiscordGuess = FormatDiscordGuessForFingerprint(previousPlayerDiscordGuess),
                    ChannelId = _options.ChannelId,
                    DiscordMessageId = msg.Id
                }),
                Status = "DONE",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<bool> HasOpenMergePendingForPreviousUsernameAsync(TrackerDbContext db, string username, CancellationToken ct)
    {
        var normalizedUsername = NormalizeUsername(username);
        if (string.IsNullOrWhiteSpace(normalizedUsername)) return false;

        var openMergeEvents = await db.LifecycleEvents
            .Where(x => x.EventType == "MERGE_ACTION_REQUIRED" && x.Status == "OPEN")
            .ToListAsync(ct);

        foreach (var mergeEvent in openMergeEvents)
        {
            var metadata = ReadLifecycleMetadata(mergeEvent.MetadataJson);
            var suggested = NormalizeUsername(PickLifecycleValue(metadata, "SuggestedPrevious") ?? "");
            if (string.IsNullOrWhiteSpace(suggested))
            {
                var fallbackPosted = await db.LifecycleEvents
                    .Where(x => x.PlayerId == mergeEvent.PlayerId && x.EventType == "MERGE_DISCORD_POSTED")
                    .OrderByDescending(x => x.CreatedAt)
                    .FirstOrDefaultAsync(ct);
                if (fallbackPosted is not null)
                {
                    var fallbackMetadata = ReadLifecycleMetadata(fallbackPosted.MetadataJson);
                    suggested = NormalizeUsername(PickLifecycleValue(fallbackMetadata, "SuggestedPrevious") ?? "");
                }
            }
            if (string.Equals(suggested, normalizedUsername, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<LifecycleEvent?> FindOpenMergeForPromotionAsync(
        TrackerDbContext db,
        int playerId,
        string username,
        CancellationToken ct)
    {
        var openMergeEvents = await db.LifecycleEvents
            .Where(x => x.EventType == "MERGE_ACTION_REQUIRED" && x.Status == "OPEN")
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);

        return openMergeEvents.FirstOrDefault(x => IsMergeEventRelatedToPlayer(x, playerId, username));
    }

    private async Task CloseOpenTempleNameChangeForMergeAsync(
        TrackerDbContext db,
        string previousUsername,
        string newUsername,
        int mergePlayerId,
        CancellationToken ct)
    {
        var normalizedPrevious = NormalizeUsername(previousUsername);
        var normalizedNew = NormalizeUsername(newUsername);
        var openRequirements = await db.LifecycleEvents
            .Where(x => x.EventType == TempleNameChangeReviewEventTypes.Required && x.Status == "OPEN")
            .ToListAsync(ct);
        var postedTempleNameChangeEvents = await db.LifecycleEvents
            .Where(x => x.EventType == TempleNameChangeReviewEventTypes.DiscordPosted)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
        foreach (var requirement in openRequirements)
        {
            var metadata = ReadLifecycleMetadata(requirement.MetadataJson);
            var previous = NormalizeUsername(PickLifecycleValue(metadata, "PreviousUsername") ?? "");
            var current = NormalizeUsername(PickLifecycleValue(metadata, "NewUsername") ?? "");
            if (!string.Equals(previous, normalizedPrevious, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(current, normalizedNew, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var postedEvent in postedTempleNameChangeEvents.Where(x =>
                         MetadataIntEquals(x.MetadataJson, "RequiredEventId", requirement.Id)))
            {
                await SuppressTempleNameChangeNoisyCardAsync(
                    db,
                    requirement,
                    postedEvent,
                    previousUsername,
                    newUsername,
                    ct,
                    $"Superseded by rename merge review: {previousUsername} -> {newUsername}");
            }

            requirement.Status = "DONE";
            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = mergePlayerId,
                EventType = "TEMPLE_NAME_CHANGE_RESOLVED_BY_MERGE",
                MetadataJson = JsonUtil.Serialize(new
                {
                    PreviousUsername = previousUsername,
                    NewUsername = newUsername,
                    RequiredEventId = requirement.Id,
                    MergePlayerId = mergePlayerId,
                    Source = "merge-review-created"
                }),
                Status = "DONE",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private async Task SuppressTempleMissingForMergeAsync(TrackerDbContext db, string previousUsername, int mergePlayerId, CancellationToken ct)
    {
        var normalizedPrevious = NormalizeUsername(previousUsername);
        if (string.IsNullOrWhiteSpace(normalizedPrevious)) return;

        var oldPlayer = await db.Players.FirstOrDefaultAsync(x => x.Username.ToLower() == normalizedPrevious.ToLower(), ct);
        if (oldPlayer is null) return;
        var newUsername = await db.Players
            .Where(x => x.Id == mergePlayerId)
            .Select(x => x.Username)
            .FirstOrDefaultAsync(ct) ?? "";

        await CloseOpenLifecycleEventsAsync(db, oldPlayer.Id,
            "MISSING_IN_ROSTER",
            "TEMPLE_MISSING_ACTION_REQUIRED",
            "WOM_MISSING_ACTION_REQUIRED");

        var recentSupersedeRows = await db.LifecycleEvents
            .Where(x =>
                x.PlayerId == mergePlayerId &&
                x.EventType == "MERGE_SUPERSEDED_TEMPLE_MISSING" &&
                x.CreatedAt >= DateTimeOffset.UtcNow.AddMinutes(-10))
            .ToListAsync(ct);
        var hasRecentSupersede = recentSupersedeRows.Any(x =>
            MetadataStringEquals(x.MetadataJson, "PreviousPlayer", oldPlayer.Username));
        if (!hasRecentSupersede)
        {
            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = mergePlayerId,
                EventType = "MERGE_SUPERSEDED_TEMPLE_MISSING",
                MetadataJson = JsonUtil.Serialize(new
                {
                    PreviousPlayer = oldPlayer.Username,
                    Source = "merge-review-pending"
                }),
                Status = "DONE",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        var oldPosted = await db.LifecycleEvents
            .Where(x => x.PlayerId == oldPlayer.Id && x.EventType == "TEMPLE_MISSING_DISCORD_POSTED")
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (oldPosted is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(oldPosted.MetadataJson);
                if (TryReadUlong(doc.RootElement, "ChannelId", out var channelId) &&
                    TryReadUlong(doc.RootElement, "DiscordMessageId", out var messageId))
                {
                    await MarkSupersededMissingCardHandledAsync(
                        db,
                        oldPosted,
                        previousUsername,
                        newUsername,
                        ct);
                    ScheduleChannelMessageDelete(
                        db,
                        mergePlayerId,
                        channelId,
                        messageId,
                        "TEMPLE_MISSING_DISCORD_DELETE_SCHEDULED",
                        new { Reason = "temple-missing-superseded-by-merge", PreviousPlayer = oldPlayer.Username });
                }
            }
            catch
            {
                // best effort
            }
        }

        var oldWomPosted = await db.LifecycleEvents
            .Where(x => x.PlayerId == oldPlayer.Id && x.EventType == "WOM_MISSING_DISCORD_POSTED")
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (oldWomPosted is null) return;

        try
        {
            using var doc = JsonDocument.Parse(oldWomPosted.MetadataJson);
            if (TryReadUlong(doc.RootElement, "ChannelId", out var channelId) &&
                TryReadUlong(doc.RootElement, "DiscordMessageId", out var messageId))
            {
                await MarkSupersededMissingCardHandledAsync(
                    db,
                    oldWomPosted,
                    previousUsername,
                    newUsername,
                    ct);
                ScheduleChannelMessageDelete(
                    db,
                    mergePlayerId,
                    channelId,
                    messageId,
                    "WOM_MISSING_DISCORD_DELETE_SCHEDULED",
                    new { Reason = "wom-missing-superseded-by-merge", PreviousPlayer = oldPlayer.Username });
            }
        }
        catch
        {
            // best effort
        }
    }

    private async Task MarkSupersededMissingCardHandledAsync(
        TrackerDbContext db,
        LifecycleEvent postedEvent,
        string previousUsername,
        string newUsername,
        CancellationToken ct)
    {
        var handledRows = await db.LifecycleEvents
            .Where(x =>
                x.EventType == DiscordMergeCardRules.SupersededCardHandledEventType &&
                x.Status == "DONE")
            .ToListAsync(ct);
        if (handledRows.Any(x => MetadataIntEquals(x.MetadataJson, "PostedEventId", postedEvent.Id)) ||
            db.ChangeTracker.Entries<LifecycleEvent>().Any(x =>
                x.State != EntityState.Deleted &&
                x.Entity.EventType == DiscordMergeCardRules.SupersededCardHandledEventType &&
                MetadataIntEquals(x.Entity.MetadataJson, "PostedEventId", postedEvent.Id)))
        {
            return;
        }

        var lookupKey = $"merge-superseded-card:{postedEvent.Id}";
        var (lookupState, liveDiscordMessage, resolvedChannelId, resolvedMessageId) =
            await TryGetPostedUserMessageAsync(postedEvent, lookupKey);
        if (lookupState == PostedMessageLookupState.Unknown)
        {
            SetLookupBackoff(lookupKey);
            return;
        }

        var canRecordHandled = lookupState == PostedMessageLookupState.Missing;
        if (lookupState == PostedMessageLookupState.Found && liveDiscordMessage is not null)
        {
            if (liveDiscordMessage.Components is null || liveDiscordMessage.Components.Count == 0)
            {
                canRecordHandled = true;
            }
            else
            {
                try
                {
                    await liveDiscordMessage.ModifyAsync(props =>
                    {
                        props.Components = new ComponentBuilder().Build();
                        props.Embed = BuildHandledEmbed(
                            liveDiscordMessage.Embeds.FirstOrDefault(),
                            $"Superseded by rename review: {previousUsername} -> {newUsername}",
                            "rename");
                    });
                    canRecordHandled = true;
                }
                catch (Discord.Net.HttpException ex) when (IsDiscordMessageMissingOrInaccessible(ex))
                {
                    canRecordHandled = true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to mark superseded Discord card {MessageId} as handled; it will be retried.",
                        liveDiscordMessage.Id);
                }
            }
        }

        if (!canRecordHandled) return;

        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = postedEvent.PlayerId,
            EventType = DiscordMergeCardRules.SupersededCardHandledEventType,
            MetadataJson = JsonUtil.Serialize(new
            {
                PostedEventId = postedEvent.Id,
                PostedEventType = postedEvent.EventType,
                ChannelId = resolvedChannelId ?? ExtractUlong(postedEvent.MetadataJson, "ChannelId"),
                DiscordMessageId = resolvedMessageId ?? ExtractUlong(postedEvent.MetadataJson, "DiscordMessageId"),
                PreviousUsername = previousUsername,
                NewUsername = newUsername,
                Source = "merge-review"
            }),
            Status = "DONE",
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private async Task ReconcileSupersededMergeCardsAsync(TrackerDbContext db, CancellationToken ct)
    {
        var openDeleteSchedules = await db.LifecycleEvents
            .Where(x =>
                x.Status == "OPEN" &&
                (x.EventType == "TEMPLE_MISSING_DISCORD_DELETE_SCHEDULED" ||
                 x.EventType == "WOM_MISSING_DISCORD_DELETE_SCHEDULED"))
            .ToListAsync(ct);
        var openDeleteMessageIds = openDeleteSchedules
            .Select(x => ExtractUlong(x.MetadataJson, "DiscordMessageId"))
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToHashSet();

        if (openDeleteMessageIds.Count == 0) return;

        var completedMerges = await db.LifecycleEvents
            .Where(x => x.EventType == "MERGE_ACTION_APPLIED" && x.Status == "DONE")
            .OrderByDescending(x => x.CreatedAt)
            .Take(100)
            .ToListAsync(ct);

        foreach (var mergeEvent in completedMerges)
        {
            var metadata = ReadLifecycleMetadata(mergeEvent.MetadataJson);
            var action = PickLifecycleValue(metadata, "Action");
            if (!string.Equals(action, "confirm", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(action, "reassign", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var previousUsername = PickLifecycleValue(metadata, "PreviousPlayer");
            var newUsername = PickLifecycleValue(metadata, "CanonicalPlayer", "NewPlayer");
            if (string.IsNullOrWhiteSpace(previousUsername) || string.IsNullOrWhiteSpace(newUsername)) continue;

            var postedEvents = await db.LifecycleEvents
                .Where(x =>
                    x.PlayerId == mergeEvent.PlayerId &&
                    (x.EventType == "TEMPLE_MISSING_DISCORD_POSTED" ||
                     x.EventType == "WOM_MISSING_DISCORD_POSTED"))
                .OrderByDescending(x => x.CreatedAt)
                .Take(100)
                .ToListAsync(ct);

            foreach (var postedEvent in postedEvents)
            {
                var postedMessageId = ExtractUlong(postedEvent.MetadataJson, "DiscordMessageId");
                if (!postedMessageId.HasValue || !openDeleteMessageIds.Contains(postedMessageId.Value)) continue;

                var postedMetadata = ReadLifecycleMetadata(postedEvent.MetadataJson);
                var postedUsername = PickLifecycleValue(postedMetadata, "Player", "Username");
                if (!string.Equals(
                        NormalizeUsername(postedUsername ?? ""),
                        NormalizeUsername(previousUsername),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await MarkSupersededMissingCardHandledAsync(
                    db,
                    postedEvent,
                    previousUsername,
                    newUsername,
                    ct);
            }
        }
    }

    private async Task SupersedePromotionCardForMergeAsync(
        TrackerDbContext db,
        LifecycleEvent postedEvent,
        int candidateId,
        int ownerPlayerId,
        string source,
        int? mergeActionEventId,
        CancellationToken ct)
    {
        var metadata = ReadLifecycleMetadata(postedEvent.MetadataJson);
        var postedCandidateId = ExtractInt(postedEvent.MetadataJson, "CandidateId");
        if (postedCandidateId.HasValue) candidateId = postedCandidateId.Value;

        var channelId = ExtractUlong(postedEvent.MetadataJson, "ChannelId");
        var messageId = ExtractUlong(postedEvent.MetadataJson, "DiscordMessageId");
        var alreadySuperseded = string.Equals(
            PickLifecycleValue(metadata, "SupersededByMerge"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        var lookupKey = $"promotion-supersede:{postedEvent.Id}";
        var (lookupState, liveDiscordMessage, resolvedChannelId, resolvedMessageId) =
            await TryGetPostedUserMessageAsync(postedEvent, lookupKey);
        if (lookupState == PostedMessageLookupState.Unknown)
        {
            SetLookupBackoff(lookupKey);
            return;
        }
        if (lookupState == PostedMessageLookupState.Malformed)
        {
            postedEvent.Status = "DONE";
            return;
        }

        if (lookupState == PostedMessageLookupState.Missing)
        {
            await RecordMissingTrackedMessageEventAsync(
                db,
                ownerPlayerId,
                "promotion",
                postedEvent.Id,
                null,
                resolvedChannelId ?? channelId,
                resolvedMessageId ?? messageId,
                "promotion-merge-superseded",
                ct);
        }
        else if (lookupState == PostedMessageLookupState.Found && liveDiscordMessage is not null)
        {
            await liveDiscordMessage.ModifyAsync(props =>
            {
                props.Components = new ComponentBuilder().Build();
                props.Embed = BuildHandledEmbed(
                    liveDiscordMessage.Embeds.FirstOrDefault(),
                    "Superseded by rename review; promotion will be re-evaluated.",
                    "rename");
            });
            resolvedChannelId = liveDiscordMessage.Channel.Id;
            resolvedMessageId = liveDiscordMessage.Id;
        }

        var finalChannelId = resolvedChannelId ?? channelId;
        var finalMessageId = resolvedMessageId ?? messageId;
        if (finalChannelId.HasValue && finalMessageId.HasValue)
        {
            ScheduleChannelMessageDelete(
                db,
                ownerPlayerId,
                finalChannelId.Value,
                finalMessageId.Value,
                "PROMOTION_DISCORD_DELETE_SCHEDULED",
                new
                {
                    CandidateId = candidateId,
                    Reason = "promotion-superseded-by-merge",
                    Source = source,
                    MergeActionEventId = mergeActionEventId
                });
        }

        postedEvent.MetadataJson = JsonUtil.Serialize(new
        {
            CandidateId = candidateId,
            DiscordMessageId = finalMessageId,
            ChannelId = finalChannelId,
            SupersededByMerge = true,
            SupersededAt = DateTimeOffset.UtcNow,
            SupersededByMergeActionEventId = mergeActionEventId,
            SupersedeReason = source
        });

        if (!alreadySuperseded)
        {
            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = ownerPlayerId,
                EventType = "PROMOTION_SUPERSEDED_BY_MERGE",
                MetadataJson = JsonUtil.Serialize(new
                {
                    CandidateId = candidateId,
                    Source = source,
                    MergeActionEventId = mergeActionEventId,
                    PromotionPostedEventId = postedEvent.Id,
                    ChannelId = finalChannelId,
                    DiscordMessageId = finalMessageId
                }),
                Status = "DONE",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private async Task RestorePromotionCardsAfterMergeAbortAsync(
        TrackerDbContext db,
        LifecycleEvent mergeActionEvent,
        CancellationToken ct)
    {
        var metadata = ReadLifecycleMetadata(mergeActionEvent.MetadataJson);
        var previousUsername = PickLifecycleValue(metadata, "SuggestedPrevious", "PreviousPlayer");
        var relatedPlayerIds = new HashSet<int> { mergeActionEvent.PlayerId };
        if (!string.IsNullOrWhiteSpace(previousUsername))
        {
            var previousPlayerId = await db.Players
                .Where(x => x.Username.ToLower() == previousUsername.ToLower())
                .Select(x => (int?)x.Id)
                .FirstOrDefaultAsync(ct);
            if (previousPlayerId.HasValue) relatedPlayerIds.Add(previousPlayerId.Value);
        }

        var pendingCandidates = await db.PromotionCandidates
            .Where(x => x.Status == PromotionStatus.PENDING && relatedPlayerIds.Contains(x.PlayerId))
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);
        if (pendingCandidates.Count == 0) return;

        var candidateIds = pendingCandidates.Select(x => x.Id).ToArray();
        var postedEvents = await db.LifecycleEvents
            .Where(x => x.EventType == "PROMOTION_DISCORD_POSTED")
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
        var scheduledDeletes = await db.LifecycleEvents
            .Where(x => x.EventType == "PROMOTION_DISCORD_DELETE_SCHEDULED" && x.Status == "OPEN")
            .ToListAsync(ct);

        foreach (var postedEvent in postedEvents.Where(x =>
                     ExtractInt(x.MetadataJson, "CandidateId") is int candidateId &&
                     candidateIds.Contains(candidateId) &&
                     IsPromotionPostedSupersededByMerge(x)))
        {
            var channelId = ExtractUlong(postedEvent.MetadataJson, "ChannelId");
            var messageId = ExtractUlong(postedEvent.MetadataJson, "DiscordMessageId");
            foreach (var scheduledDelete in scheduledDeletes.Where(x =>
                         messageId.HasValue && MetadataUlongEquals(x.MetadataJson, "DiscordMessageId", messageId.Value)))
            {
                scheduledDelete.Status = "DONE";
            }

            postedEvent.MetadataJson = JsonUtil.Serialize(new
            {
                CandidateId = ExtractInt(postedEvent.MetadataJson, "CandidateId"),
                DiscordMessageId = messageId,
                ChannelId = channelId,
                RenderFingerprint = (string?)null,
                SupersededByMerge = false,
                ReenableAfterMergeAbort = true,
                ReenabledAt = DateTimeOffset.UtcNow,
                MergeActionEventId = mergeActionEvent.Id
            });
        }
    }

    private async Task ProcessMergeActionUpdates(CancellationToken ct)
    {
        if (_client is null) return;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var updates = await db.LifecycleEvents
            .Where(x => x.EventType == "MERGE_ACTION_APPLIED" && x.Status == "OPEN")
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);
        if (updates.Count == 0) return;

        var shouldRefreshPromotions = false;
        foreach (var update in updates)
        {
            var updateMetadata = ReadLifecycleMetadata(update.MetadataJson);
            var mergeAction = (PickLifecycleValue(updateMetadata, "Action") ?? "").Trim().ToLowerInvariant();
            if (mergeAction is "confirm" or "reassign")
            {
                var transferredPendingCandidateIds = ExtractIntArray(update.MetadataJson, "TransferredPendingCandidateIds");
                if (transferredPendingCandidateIds.Count > 0)
                {
                    foreach (var candidateId in transferredPendingCandidateIds)
                    {
                        var postedPromotionEvents = await db.LifecycleEvents
                            .Where(x => x.EventType == "PROMOTION_DISCORD_POSTED")
                            .OrderByDescending(x => x.CreatedAt)
                            .ToListAsync(ct);
                        postedPromotionEvents = postedPromotionEvents
                            .Where(x => MetadataIntEquals(x.MetadataJson, "CandidateId", candidateId))
                            .ToList();
                        foreach (var postedPromotionEvent in postedPromotionEvents)
                        {
                            await SupersedePromotionCardForMergeAsync(
                                db,
                                postedPromotionEvent,
                                candidateId,
                                update.PlayerId,
                                "merge-action-applied",
                                update.Id,
                                ct);
                        }
                    }
                }

                shouldRefreshPromotions = true;
            }
            else if (mergeAction == "abort")
            {
                await RestorePromotionCardsAfterMergeAbortAsync(db, update, ct);
                shouldRefreshPromotions = true;
            }

            var posted = await db.LifecycleEvents
                .Where(x => x.PlayerId == update.PlayerId && x.EventType == "MERGE_DISCORD_POSTED")
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (posted is not null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(posted.MetadataJson);
                    if (TryReadUlong(doc.RootElement, "ChannelId", out var channelId) && TryReadUlong(doc.RootElement, "DiscordMessageId", out var messageId))
                    {
                        var channel = await ResolveMessageChannelAsync(channelId);
                        var msg = channel is null ? null : await channel.GetMessageAsync(messageId);
                        if (msg is IUserMessage userMessage)
                        {
                            await userMessage.ModifyAsync(props =>
                            {
                                props.Components = new ComponentBuilder().Build();
                                props.Embed = BuildHandledEmbed(userMessage.Embeds.FirstOrDefault(), "Handled via web/discord merge action", "approve");
                            });
                            ScheduleChannelMessageDelete(db, update.PlayerId, channelId, messageId, "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED", new { Reason = "merge-action-handled" });
                        }
                    }
                }
                catch { }
            }
            update.Status = "DONE";
        }
        await db.SaveChangesAsync(ct);

        if (shouldRefreshPromotions)
        {
            await PostPendingPromotionCandidates(ct);
        }
    }

    private async Task<List<string>> GetMergeCandidateOptionsAsync(int playerId)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var mergeEvent = await db.LifecycleEvents
            .Where(x => x.PlayerId == playerId && x.EventType == "MERGE_SUGGESTED")
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync();
        if (mergeEvent is null) return [];
        var metadata = ReadLifecycleMetadata(mergeEvent.MetadataJson);
        var candidates = new List<string>();
        var suggested = PickLifecycleValue(metadata, "SuggestedPrevious");
        if (!string.IsNullOrWhiteSpace(suggested)) candidates.Add(suggested);
        var allMissing = await db.Players
            .Where(x => x.Status == PlayerStatus.MISSING_PENDING_REVIEW)
            .OrderBy(x => x.Username)
            .Select(x => x.Username)
            .Take(10)
            .ToListAsync();
        foreach (var candidate in allMissing)
        {
            if (!candidates.Any(x => string.Equals(x, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(candidate);
            }
        }
        return candidates;
    }

    private async Task<IMessageChannel?> ResolveMessageChannelAsync(ulong channelId)
    {
        if (_client is null) return null;
        var channel = _client.GetChannel(channelId) as IMessageChannel;
        if (channel is not null) return channel;
        try
        {
            var restChannel = await _client.Rest.GetChannelAsync(channelId);
            return restChannel as IMessageChannel;
        }
        catch
        {
            return null;
        }
    }

    private async Task HandleSlashCommandAsync(SocketSlashCommand command)
    {
        var sw = Stopwatch.StartNew();
        logger.LogInformation("Discord slash start: {Command} by {User}", command.Data.Name, command.User.Username);
        try
        {
            var adminLocked = IsAdminLockedSlashCommand(command.Data.Name);
            var allowed = !adminLocked || HasDiscordAdminRole(command.User);
            await LogSlashCommandAsync(command, adminLocked, allowed);

            if (!allowed)
            {
                await DenySlashCommandAsync(command);
                return;
            }

            var deferEphemeral = IsEphemeralSlashCommand(command.Data.Name);
            if (!command.HasResponded)
            {
                await command.DeferAsync(ephemeral: deferEphemeral);
                logger.LogInformation("Discord slash deferred: {Command} ephemeral={Ephemeral}", command.Data.Name, deferEphemeral);
            }

            if (string.Equals(command.Data.Name, "update", StringComparison.OrdinalIgnoreCase))
            {
                var usernameRaw = command.Data.Options.FirstOrDefault(x => x.Name == "player")?.Value?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(usernameRaw))
                {
                    await RespondAndAutoDeleteAsync(command, "Please provide a player username.");
                    return;
                }

                await using var updateScope = scopeFactory.CreateAsyncScope();
                var updateDb = updateScope.ServiceProvider.GetRequiredService<TrackerDbContext>();
                var foundPlayer = await updateDb.Players.FirstOrDefaultAsync(x => x.Username.ToLower() == usernameRaw.ToLower());
                if (foundPlayer is null)
                {
                    await RespondAndAutoDeleteAsync(command, $"No player found for `{usernameRaw}`.");
                    return;
                }

                var wasActive = foundPlayer.Status == PlayerStatus.ACTIVE;
                var templeGroupId = configuration.GetValue<int?>("TempleOsrs:GroupId") ?? 449;
                var inTemple = await IsPlayerInTempleGroupAsync(foundPlayer.Username, templeGroupId);
                var suppressedByPendingMerge = await HasOpenMergePendingForPreviousUsernameAsync(updateDb, foundPlayer.Username, CancellationToken.None);
                if (!inTemple && foundPlayer.Status != PlayerStatus.REMOVED_CONFIRMED)
                {
                    if (suppressedByPendingMerge)
                    {
                        await CloseOpenLifecycleEventsAsync(updateDb, foundPlayer.Id, "MISSING_IN_ROSTER", "TEMPLE_MISSING_ACTION_REQUIRED");
                    }
                    else
                    {
                        foundPlayer.Status = PlayerStatus.MISSING_PENDING_REVIEW;
                        var hasPendingAction = await updateDb.LifecycleEvents.AnyAsync(x =>
                            x.PlayerId == foundPlayer.Id &&
                            x.EventType == "TEMPLE_MISSING_ACTION_REQUIRED" &&
                            x.Status == "OPEN");
                        if (!hasPendingAction)
                        {
                            updateDb.LifecycleEvents.Add(new LifecycleEvent
                            {
                                PlayerId = foundPlayer.Id,
                                EventType = "TEMPLE_MISSING_ACTION_REQUIRED",
                                MetadataJson = JsonUtil.Serialize(new
                                {
                                    foundPlayer.Username,
                                    MissingAt = DateTimeOffset.UtcNow,
                                    Source = "discord-slash-update"
                                }),
                                Status = "OPEN",
                                CreatedAt = DateTimeOffset.UtcNow
                            });
                        }
                        updateDb.LifecycleEvents.Add(new LifecycleEvent
                        {
                            PlayerId = foundPlayer.Id,
                            EventType = "MISSING_IN_ROSTER",
                            MetadataJson = JsonUtil.Serialize(new
                            {
                                foundPlayer.Username,
                                MissingAt = DateTimeOffset.UtcNow,
                                Source = "discord-slash-update"
                            }),
                            Status = "OPEN",
                            CreatedAt = DateTimeOffset.UtcNow
                        });
                    }
                }

                if (wasActive && inTemple)
                {
                    var hasOpenRenameRecheck = await updateDb.LifecycleEvents.AnyAsync(x =>
                        x.PlayerId == foundPlayer.Id &&
                        x.EventType == RenameReviewEventTypes.RecheckRequested &&
                        x.Status == "OPEN");
                    if (!hasOpenRenameRecheck)
                    {
                        updateDb.LifecycleEvents.Add(new LifecycleEvent
                        {
                            PlayerId = foundPlayer.Id,
                            EventType = RenameReviewEventTypes.RecheckRequested,
                            MetadataJson = JsonUtil.Serialize(new
                            {
                                Player = foundPlayer.Username,
                                RequestedBy = command.User.Username,
                                Source = "discord-slash-update"
                            }),
                            Status = "OPEN",
                            CreatedAt = DateTimeOffset.UtcNow
                        });
                    }
                }

                updateDb.LifecycleEvents.Add(new LifecycleEvent
                {
                    PlayerId = foundPlayer.Id,
                    EventType = "PRIORITY_UPDATE_REQUEST",
                    MetadataJson = JsonUtil.Serialize(new
                    {
                        Player = foundPlayer.Username,
                        RequestedBy = command.User.Username,
                        Source = "discord-slash-update"
                    }),
                    Status = "OPEN",
                    CreatedAt = DateTimeOffset.UtcNow
                });
                await updateDb.SaveChangesAsync();
                if (foundPlayer.Status is PlayerStatus.MISSING_PENDING_REVIEW or PlayerStatus.NEW_PENDING_REVIEW) queue.EnqueueMissingPriority(foundPlayer.Id);
                else queue.EnqueueFront(foundPlayer.Id);

                await RespondAndAutoDeleteAsync(command, foundPlayer.Status is PlayerStatus.MISSING_PENDING_REVIEW or PlayerStatus.NEW_PENDING_REVIEW
                    ? $"`{foundPlayer.Username}` was queued as high-priority review."
                    : $"Queued `{foundPlayer.Username}` for priority update.");
                return;
            }

            if (string.Equals(command.Data.Name, "set-pets", StringComparison.OrdinalIgnoreCase))
            {
                var petsUsername = command.Data.Options.FirstOrDefault(x => x.Name == "player")?.Value?.ToString()?.Trim();
                var countRaw = command.Data.Options.FirstOrDefault(x => x.Name == "count")?.Value;
                if (string.IsNullOrWhiteSpace(petsUsername) || countRaw is null || !int.TryParse(countRaw.ToString(), out var petCount) || petCount < 0)
                {
                    await RespondAndAutoDeleteAsync(command, "Please provide a valid player and pet count (0 or higher).", ephemeral: false);
                    return;
                }

                await using var petScope = scopeFactory.CreateAsyncScope();
                var petDb = petScope.ServiceProvider.GetRequiredService<TrackerDbContext>();
                var petWom = petScope.ServiceProvider.GetRequiredService<IWiseOldManClient>();
                var petsPlayer = await petDb.Players
                    .Include(x => x.Snapshots)
                    .FirstOrDefaultAsync(x => x.Username.ToLower() == petsUsername.ToLower());
                if (petsPlayer is null)
                {
                    await RespondAndAutoDeleteAsync(command, $"No player found for `{petsUsername}`.", ephemeral: false);
                    return;
                }

                petsPlayer.ManualPetOverride = petCount;
                var isRankUpgradeExcludedRole = await ReevaluatePlayerForManualPetsAsync(petDb, petWom, petsPlayer);
                await petDb.SaveChangesAsync();

                await RespondAndAutoDeleteAsync(command, isRankUpgradeExcludedRole
                    ? $"Manual pets set: `{petsPlayer.Username}` -> `{petCount}`. Player has a non-clan WiseOldMan role and is excluded from rank upgrades."
                    : $"Manual pets set: `{petsPlayer.Username}` -> `{petCount}`. Rank eligibility has been re-evaluated.", ephemeral: false);
                return;
            }

            if (string.Equals(command.Data.Name, "temple-add", StringComparison.OrdinalIgnoreCase))
            {
                var players = ParsePlayers(command);
                if (players.Count == 0)
                {
                    await RespondAndAutoDeleteAsync(command, "No valid player names found.", ephemeral: false);
                    return;
                }
                var renameDetection = await TryPrepareRenameReviewForCommandAsync(players, CancellationToken.None);
                if (renameDetection is not null)
                {
                    await RespondAndAutoDeleteAsync(command, BuildRenameGuardMessage(renameDetection), ephemeral: false);
                    return;
                }
                await RespondAndAutoDeleteAsync(command, await ExecuteTempleAddAsync(players), ephemeral: false);
                return;
            }

            if (string.Equals(command.Data.Name, "add", StringComparison.OrdinalIgnoreCase))
            {
                var players = ParsePlayers(command);
                if (players.Count == 0)
                {
                    await RespondAndAutoDeleteAsync(command, "Please provide one or more valid player names.", ephemeral: false);
                    return;
                }

                var renameDetection = await TryPrepareRenameReviewForCommandAsync(players, CancellationToken.None);
                if (renameDetection is not null)
                {
                    await RespondAndAutoDeleteAsync(command, BuildRenameGuardMessage(renameDetection), ephemeral: false);
                    return;
                }

                var templeEmbed = await ExecuteTempleAddAsync(players);
                await RespondAndAutoDeleteAsync(command, templeEmbed, ephemeral: false);

                var womEmbed = await ExecuteWomAddAsync(players);
                await RespondAndAutoDeleteAsync(command, womEmbed, ephemeral: false);
                return;
            }

            if (string.Equals(command.Data.Name, "remove", StringComparison.OrdinalIgnoreCase))
            {
                var players = ParsePlayers(command);
                if (players.Count == 0)
                {
                    await RespondAndAutoDeleteAsync(command, "Please provide one or more valid player names.", ephemeral: false);
                    return;
                }

                var renameDetection = await TryPrepareRenameReviewForCommandAsync(players, CancellationToken.None);
                if (renameDetection is not null)
                {
                    await RespondAndAutoDeleteAsync(command, BuildRenameGuardMessage(renameDetection), ephemeral: false);
                    return;
                }

                var templeEmbed = await ExecuteTempleRemoveAsync(players);
                await RespondAndAutoDeleteAsync(command, templeEmbed, ephemeral: false);

                var womEmbed = await ExecuteWomRemoveAsync(players);
                await RespondAndAutoDeleteAsync(command, womEmbed, ephemeral: false);
                return;
            }

            if (string.Equals(command.Data.Name, "temple-remove", StringComparison.OrdinalIgnoreCase))
            {
                var players = ParsePlayers(command);
                if (players.Count == 0)
                {
                    await RespondAndAutoDeleteAsync(command, "No valid player names found.", ephemeral: false);
                    return;
                }
                var renameDetection = await TryPrepareRenameReviewForCommandAsync(players, CancellationToken.None);
                if (renameDetection is not null)
                {
                    await RespondAndAutoDeleteAsync(command, BuildRenameGuardMessage(renameDetection), ephemeral: false);
                    return;
                }
                await RespondAndAutoDeleteAsync(command, await ExecuteTempleRemoveAsync(players), ephemeral: false);
                return;
            }

            if (string.Equals(command.Data.Name, "wom-add", StringComparison.OrdinalIgnoreCase))
            {
                var players = ParsePlayers(command);
                if (players.Count == 0)
                {
                    await RespondAndAutoDeleteAsync(command, BuildWomResultEmbed(
                        title: "WiseOldMan Add Failed",
                        success: false,
                        groupId: configuration.GetValue<int?>("WiseOldMan:GroupId") ?? 0,
                        players: [],
                        details: "Please provide one or more valid player names."), ephemeral: false);
                    return;
                }

                var renameDetection = await TryPrepareRenameReviewForCommandAsync(players, CancellationToken.None);
                if (renameDetection is not null)
                {
                    await RespondAndAutoDeleteAsync(command, BuildRenameGuardMessage(renameDetection), ephemeral: false);
                    return;
                }

                await RespondAndAutoDeleteAsync(command, await ExecuteWomAddAsync(players), ephemeral: false);
                return;
            }

            if (string.Equals(command.Data.Name, "wom-remove", StringComparison.OrdinalIgnoreCase))
            {
                var players = ParsePlayers(command);
                if (players.Count == 0)
                {
                    await RespondAndAutoDeleteAsync(command, BuildWomResultEmbed(
                        title: "WiseOldMan Remove Failed",
                        success: false,
                        groupId: configuration.GetValue<int?>("WiseOldMan:GroupId") ?? 0,
                        players: [],
                        details: "Please provide one or more valid player names."), ephemeral: false);
                    return;
                }

                var renameDetection = await TryPrepareRenameReviewForCommandAsync(players, CancellationToken.None);
                if (renameDetection is not null)
                {
                    await RespondAndAutoDeleteAsync(command, BuildRenameGuardMessage(renameDetection), ephemeral: false);
                    return;
                }

                await RespondAndAutoDeleteAsync(command, await ExecuteWomRemoveAsync(players), ephemeral: false);
                return;
            }

            if (string.Equals(command.Data.Name, "wom-role-update", StringComparison.OrdinalIgnoreCase))
            {
                var playerName = command.Data.Options.FirstOrDefault(x => x.Name == "player")?.Value?.ToString()?.Trim();
                var role = command.Data.Options.FirstOrDefault(x => x.Name == "rank")?.Value?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(role) || !IsAllowedWomRole(role))
                {
                    await RespondAndAutoDeleteAsync(command, BuildWomRoleUpdateEmbed(
                        title: "WiseOldMan Role Update Failed",
                        success: false,
                        groupId: configuration.GetValue<int?>("WiseOldMan:GroupId") ?? 0,
                        playerName: playerName ?? "N/A",
                        requestedRole: role ?? "N/A",
                        updatedRole: null,
                        womPlayerId: null,
                        displayName: null,
                        details: "Please provide a valid player and selectable rank."), ephemeral: false);
                    return;
                }

                await RespondAndAutoDeleteAsync(command, await ExecuteWomRoleUpdateAsync(command, playerName, role), ephemeral: false);
                return;
            }

            if (string.Equals(command.Data.Name, "wom-cache-refresh", StringComparison.OrdinalIgnoreCase))
            {
                await HandleWomCacheRefreshSlashCommandAsync(command);
                return;
            }

            if (string.Equals(command.Data.Name, "discord-guess", StringComparison.OrdinalIgnoreCase))
            {
                await HandleDiscordGuessSlashCommandAsync(command);
                return;
            }

            if (string.Equals(command.Data.Name, "discord-rank-check", StringComparison.OrdinalIgnoreCase))
            {
                await HandleDiscordRankCheckSlashCommandAsync(command);
                return;
            }

            if (string.Equals(command.Data.Name, "discord-rank-audit", StringComparison.OrdinalIgnoreCase))
            {
                await HandleDiscordRankAuditSlashCommandAsync(command);
                return;
            }

            if (string.Equals(command.Data.Name, "ungnomify", StringComparison.OrdinalIgnoreCase))
            {
                await HandleWomSpecialRankAuditSlashCommandAsync(command);
                return;
            }

            if (string.Equals(command.Data.Name, "discord-rank-set", StringComparison.OrdinalIgnoreCase))
            {
                await HandleDiscordRankSetSlashCommandAsync(command);
                return;
            }

            if (string.Equals(command.Data.Name, "discord-rank-review-post", StringComparison.OrdinalIgnoreCase))
            {
                await HandleDiscordRankReviewPostSlashCommandAsync(command);
                return;
            }

            if (string.Equals(command.Data.Name, "history", StringComparison.OrdinalIgnoreCase))
            {
                await HandleHistorySlashCommandAsync(command);
                return;
            }

            if (string.Equals(command.Data.Name, "clear", StringComparison.OrdinalIgnoreCase))
            {
                await HandleClearSlashCommandAsync(command);
                return;
            }

            if (string.Equals(command.Data.Name, "help", StringComparison.OrdinalIgnoreCase))
            {
                await HandleHelpSlashCommandAsync(command);
                return;
            }

            if (string.Equals(command.Data.Name, "rank-conditions", StringComparison.OrdinalIgnoreCase))
            {
                await HandleRankConditionsSlashCommandAsync(command);
                return;
            }

            if (string.Equals(command.Data.Name, "unignore", StringComparison.OrdinalIgnoreCase))
            {
                await HandleWomUnignoreSlashCommandAsync(command);
                return;
            }

            if (string.Equals(command.Data.Name, "show-ignored", StringComparison.OrdinalIgnoreCase))
            {
                await HandleShowIgnoredSlashCommandAsync(command);
                return;
            }

            if (string.Equals(command.Data.Name, "requeue-review-card", StringComparison.OrdinalIgnoreCase))
            {
                await HandleRequeueReviewCardSlashCommandAsync(command);
                return;
            }

            if (!string.Equals(command.Data.Name, "lookup", StringComparison.OrdinalIgnoreCase))
                return;

            var username = command.Data.Options.FirstOrDefault(x => x.Name == "player")?.Value?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(username))
            {
                await RespondAndAutoDeleteAsync(command, "Please provide a player username.");
                return;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();

            var player = await db.Players
                .Where(x => x.Username.ToLower() == username.ToLower())
                .Select(x => new
                {
                    x.Username,
                    x.CurrentRank,
                    x.EligibleRank,
                    x.Status,
                    Pets = x.ManualPetOverride ?? x.StoredPetCount,
                    x.LastSynced,
                    Latest = x.Snapshots
                        .OrderByDescending(s => s.Timestamp)
                        .Select(s => new
                        {
                            s.TotalLevel,
                            s.Ehb,
                            s.Ehp,
                            s.Collections
                        })
                        .FirstOrDefault()
                })
                .FirstOrDefaultAsync();

            if (player is null)
            {
                await RespondAndAutoDeleteAsync(command, $"No player found for `{username}`.");
                return;
            }

            var lookupTempleGroupId = configuration.GetValue<int?>("TempleOsrs:GroupId") ?? 449;
            var lookupWomGroupId = configuration.GetValue<int?>("WiseOldMan:GroupId") ?? 0;
            var templeAdded = await IsPlayerInTempleGroupAsync(player.Username, lookupTempleGroupId);
            var womAdded = lookupWomGroupId > 0 && await IsPlayerInWiseOldManGroupAsync(player.Username, lookupWomGroupId);

            var embed = new EmbedBuilder()
                .WithTitle($"Lookup: {player.Username}")
                .WithColor(new Color(59, 130, 246))
                .AddField("Current Rank", player.CurrentRank, true)
                .AddField("Status", player.Status.ToString(), true)
                .AddField("Total Level", player.Latest?.TotalLevel.ToString() ?? "N/A", true)
                .AddField("EHB", player.Latest is null ? "N/A" : player.Latest.Ehb.ToString("0.0", CultureInfo.InvariantCulture), true)
                .AddField("EHP", player.Latest is null ? "N/A" : player.Latest.Ehp.ToString("0.0", CultureInfo.InvariantCulture), true)
                .AddField("Collections", player.Latest?.Collections.ToString() ?? "N/A", true)
                .AddField("Pets", player.Pets > 0 ? player.Pets.ToString() : "N/A", true)
                .AddField("Temple", templeAdded ? "Added" : "Missing", true)
                .AddField("WiseOldMan", womAdded ? "Added" : "Missing", true)
                .AddField("Last Synced (Swedish Time)", FormatSwedishTime(player.LastSynced), false)
                .WithTimestamp(DateTimeOffset.Now)
                .Build();

            await RespondAndAutoDeleteAsync(command, embed);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling /lookup.");
            await RespondAndAutoDeleteAsync(command, "Command failed.", ephemeral: true);
        }
        finally
        {
            logger.LogInformation("Discord slash end: {Command} in {ElapsedMs}ms", command.Data.Name, sw.ElapsedMilliseconds);
        }
    }

    private async Task HandleClearSlashCommandAsync(SocketSlashCommand command)
    {
        var configuredChannelId = _options.ChannelId;
        if (!DiscordClearRules.CanRunInConfiguredChannel(command.ChannelId, configuredChannelId))
        {
            await RespondAndAutoDeleteAsync(
                command,
                "For safety, /clear must be run in the configured #rank-up channel. No messages were touched.");
            return;
        }

        if (!await _discordClearCommandLock.WaitAsync(0))
        {
            await RespondAndAutoDeleteAsync(
                command,
                "A /clear operation is already running. No messages were touched.");
            return;
        }

        try
        {
            var resolvedChannel = await ResolveMessageChannelAsync(configuredChannelId);
            if (resolvedChannel is null || resolvedChannel.Id != configuredChannelId)
            {
                logger.LogError(
                    "Refusing /clear because the configured Discord channel {ConfiguredChannelId} could not be resolved exactly.",
                    configuredChannelId);
                await RespondAndAutoDeleteAsync(
                    command,
                    "The configured #rank-up channel could not be resolved exactly. No messages were touched.");
                return;
            }

            if (resolvedChannel is not ITextChannel rankUpChannel)
            {
                logger.LogError(
                    "Refusing /clear because configured Discord channel {ConfiguredChannelId} is not a text channel.",
                    configuredChannelId);
                await RespondAndAutoDeleteAsync(
                    command,
                    "The configured #rank-up channel is not a text channel. No messages were touched.");
                return;
            }

            var messages = await GetAllDiscordMessagesForClearAsync(rankUpChannel, configuredChannelId);
            var clearCandidates = messages
                .Select(message => new DiscordClearMessage(
                    message.Id,
                    message.Timestamp,
                    message.Source == MessageSource.User && !message.Author.IsBot))
                .ToArray();
            var messagesToDelete = DiscordClearRules
                .SelectUserMessagesToDelete(clearCandidates)
                .Select(candidate => candidate.Id)
                .ToHashSet();

            if (messagesToDelete.Count == 0)
            {
                await RespondAndAutoDeleteAsync(
                    command,
                    "No user messages were found to clear. The quickguide and all bot messages were left untouched.");
                return;
            }

            var deletedCount = await DeleteDiscordMessagesForClearAsync(
                rankUpChannel,
                configuredChannelId,
                messages.Where(message => messagesToDelete.Contains(message.Id)).ToArray());

            await RespondAndAutoDeleteAsync(
                command,
                $"Removed {deletedCount} user message(s) from the configured #rank-up channel. " +
                "The oldest user message (the quickguide) and all bot messages were left untouched.");
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed handling /clear for the configured Discord channel {ConfiguredChannelId}.",
                configuredChannelId);
            await RespondAndAutoDeleteAsync(
                command,
                "Clear failed. Only the configured #rank-up channel was targeted; some eligible messages may have been removed before the failure.");
        }
        finally
        {
            _discordClearCommandLock.Release();
        }
    }

    private static async Task<IReadOnlyList<IMessage>> GetAllDiscordMessagesForClearAsync(
        IMessageChannel rankUpChannel,
        ulong expectedChannelId)
    {
        EnsureClearChannel(rankUpChannel, expectedChannelId);

        var messagesById = new Dictionary<ulong, IMessage>();
        ulong? beforeMessageId = null;

        while (true)
        {
            IEnumerable<IMessage> page = beforeMessageId is null
                ? await rankUpChannel
                    .GetMessagesAsync(DiscordClearPageSize, CacheMode.AllowDownload)
                    .FlattenAsync()
                : await rankUpChannel
                    .GetMessagesAsync(beforeMessageId.Value, Direction.Before, DiscordClearPageSize, CacheMode.AllowDownload)
                    .FlattenAsync();

            var pageMessages = page.ToArray();
            if (pageMessages.Length == 0)
            {
                break;
            }

            foreach (var message in pageMessages)
            {
                messagesById[message.Id] = message;
            }

            var oldestPageMessageId = pageMessages.Min(message => message.Id);
            if (pageMessages.Length < DiscordClearPageSize || beforeMessageId == oldestPageMessageId)
            {
                break;
            }

            beforeMessageId = oldestPageMessageId;
        }

        return messagesById.Values
            .OrderBy(message => message.Timestamp)
            .ThenBy(message => message.Id)
            .ToArray();
    }

    private static async Task<int> DeleteDiscordMessagesForClearAsync(
        ITextChannel rankUpChannel,
        ulong expectedChannelId,
        IReadOnlyCollection<IMessage> messages)
    {
        EnsureClearChannel(rankUpChannel, expectedChannelId);

        var bulkDeleteCutoff = DateTimeOffset.UtcNow - DiscordBulkDeleteMaxAge;
        var recentMessages = messages
            .Where(message => message.Timestamp > bulkDeleteCutoff)
            .OrderByDescending(message => message.Id)
            .ToArray();
        var oldMessages = messages
            .Where(message => message.Timestamp <= bulkDeleteCutoff)
            .OrderByDescending(message => message.Id)
            .ToArray();
        var deletedCount = 0;

        foreach (var batch in recentMessages.Chunk(DiscordClearPageSize))
        {
            if (batch.Length == 1)
            {
                await rankUpChannel.DeleteMessageAsync(batch[0].Id);
            }
            else
            {
                await rankUpChannel.DeleteMessagesAsync(batch.Select(message => message.Id));
            }

            deletedCount += batch.Length;
        }

        foreach (var message in oldMessages)
        {
            await rankUpChannel.DeleteMessageAsync(message.Id);
            deletedCount++;
        }

        return deletedCount;
    }

    private static void EnsureClearChannel(IMessageChannel channel, ulong expectedChannelId)
    {
        if (expectedChannelId == 0 || channel.Id != expectedChannelId)
        {
            throw new InvalidOperationException(
                $"Refusing Discord clear operation for channel {channel.Id}; expected configured channel {expectedChannelId}.");
        }
    }

    private async Task HandleDiscordGuessSlashCommandAsync(SocketSlashCommand command)
    {
        var playerRaw = command.Data.Options.FirstOrDefault(x => x.Name == "player")?.Value?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(playerRaw))
        {
            await RespondAndAutoDeleteAsync(command, "Please provide a player username.", ephemeral: false);
            return;
        }

        using var timeout = new CancellationTokenSource(DiscordGuessCommandTimeout);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
            var player = await db.Players
                .Where(x => x.Username.ToLower() == playerRaw.ToLower())
                .Select(x => new { x.Id, x.Username })
                .FirstOrDefaultAsync(timeout.Token);

            if (player is null)
            {
                await RespondAndAutoDeleteAsync(command, $"No player found for `{EscapeInlineCode(playerRaw)}`.", ephemeral: false);
                return;
            }

            var aliases = await GetDiscordGuessPlayerNamesAsync(db, player.Id, player.Username, timeout.Token);
            var guess = await GuessDiscordMemberForNamesAsync(aliases, timeout.Token);
            var embed = BuildDiscordGuessEmbed(player.Username, aliases, guess);
            await RespondAndAutoDeleteAsync(command, embed, ephemeral: false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            logger.LogWarning("Discord guess slash command timed out for {Player}.", playerRaw);
            await RespondAndAutoDeleteAsync(command, $"Discord guess timed out for `{EscapeInlineCode(playerRaw)}`. Try again in a moment.", ephemeral: false);
        }
    }

    private static Embed BuildDiscordGuessEmbed(
        string playerName,
        IReadOnlyList<string> aliases,
        DiscordMemberGuessResult guess)
    {
        var builder = new EmbedBuilder()
            .WithTitle($"Discord Guess: {playerName}")
            .WithColor(new Color(99, 102, 241))
            .WithTimestamp(DateTimeOffset.Now);

        var aliasText = aliases.Count == 0
            ? $"`{EscapeInlineCode(playerName)}`"
            : string.Join(", ", aliases.Take(8).Select(x => $"`{EscapeInlineCode(x)}`"));
        if (aliases.Count > 8)
        {
            aliasText += $" +{aliases.Count - 8} more";
        }

        builder.AddField("Names Used", aliasText, false);

        if (guess.Matches.Count == 0)
        {
            builder
                .WithDescription("No plausible Discord member match found.")
                .AddField("Result", "No mention will be shown on cards for this player unless another alias or linked account is added later.", false);
            return builder.Build();
        }

        var best = guess.Best!;
        var second = guess.Matches.Skip(1).FirstOrDefault();
        var showSingle =
            best.Strength == DiscordMemberMatchStrength.Exact ||
            (best.Strength == DiscordMemberMatchStrength.Strong && (second is null || second.Score < 85));

        if (showSingle)
        {
            var label = best.Strength == DiscordMemberMatchStrength.Exact ? "Exact match" : "Strong best guess";
            builder
                .WithDescription($"{label}: {best.Mention}")
                .AddField("Matched On", $"{best.Score}% via {best.MatchedField} `{EscapeInlineCode(best.MatchedValue)}`", true)
                .AddField("Player Alias", $"`{EscapeInlineCode(best.PlayerAlias)}`", true)
                .AddField("Source", best.FromDiscordSearch ? "Discord search + member cache" : "Member cache", true);
            return builder.Build();
        }

        builder.WithDescription("Possible Discord member matches:");
        var lines = guess.Matches.Select((match, index) =>
            $"{index + 1}. {match.Mention} - {match.Score}% via {match.MatchedField} `{EscapeInlineCode(match.MatchedValue)}` from `{EscapeInlineCode(match.PlayerAlias)}`");
        builder.AddField("Top Matches", string.Join("\n", lines), false);
        return builder.Build();
    }

    private async Task HandleDiscordRankCheckSlashCommandAsync(SocketSlashCommand command)
    {
        var playerRaw = command.Data.Options.FirstOrDefault(x => x.Name == "player")?.Value?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(playerRaw))
        {
            await RespondAndAutoDeleteAsync(command, "Please provide a player username.", ephemeral: false);
            return;
        }

        using var timeout = new CancellationTokenSource(DiscordGuessCommandTimeout);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
            var player = await db.Players
                .Where(x => x.Username.ToLower() == playerRaw.ToLower())
                .Select(x => new DiscordRankAuditPlayer(x.Id, x.Username, x.CurrentRank, x.Status))
                .FirstOrDefaultAsync(timeout.Token);

            if (player is null)
            {
                await RespondAndAutoDeleteAsync(command, $"No player found for `{EscapeInlineCode(playerRaw)}`.", ephemeral: false);
                return;
            }

            var hasWomRankMismatchIgnore = await HasOpenWomRankMismatchIgnoreAsync(db, player.Id, timeout.Token);
            player = player with { WomRankMismatchIgnored = hasWomRankMismatchIgnore };
            var result = await CheckDiscordRankForPlayerAsync(db, player, cachedMembers: null, timeout.Token);
            await RespondAndAutoDeleteAsync(command, BuildDiscordRankCheckEmbed(result), ephemeral: false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            logger.LogWarning("Discord rank check slash command timed out for {Player}.", playerRaw);
            await RespondAndAutoDeleteAsync(command, $"Discord rank check timed out for `{EscapeInlineCode(playerRaw)}`. Try again in a moment.", ephemeral: false);
        }
    }

    private async Task HandleDiscordRankAuditSlashCommandAsync(SocketSlashCommand command)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
            var results = await BuildDiscordRankAuditResultsAsync(db, timeout.Token);
            var embed = BuildDiscordRankAuditSummaryEmbed(results);
            var csv = BuildDiscordRankAuditCsv(results);
            await RespondWithCsvAsync(command, embed, csv, $"discord-rank-audit-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            logger.LogWarning("Discord rank audit slash command timed out.");
            await RespondAndAutoDeleteAsync(command, "Discord rank audit timed out. Try again after the member cache has warmed up.", ephemeral: false);
        }
    }

    private async Task HandleWomSpecialRankAuditSlashCommandAsync(SocketSlashCommand command)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
            var wiseOldManClient = scope.ServiceProvider.GetRequiredService<IWiseOldManClient>();
            var rows = await BuildWomSpecialRankAuditRowsAsync(db, wiseOldManClient, timeout.Token);
            await RespondWithUngnomifyEmbedsAsync(command, rows);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            logger.LogWarning("WOM special rank audit slash command timed out.");
            await RespondAndAutoDeleteAsync(command, "WOM special rank audit timed out. Try again after the Wise Old Man cache has warmed up.", ephemeral: false);
        }
    }

    private static async Task<IReadOnlyList<UngnomifyRow>> BuildWomSpecialRankAuditRowsAsync(
        TrackerDbContext db,
        IWiseOldManClient wiseOldManClient,
        CancellationToken ct)
    {
        var trackedPlayers = await db.Players
            .Where(x => x.Status != PlayerStatus.REMOVED_CONFIRMED)
            .Select(x => new
            {
                x.Username,
                x.CurrentRank
            })
            .ToListAsync(ct);

        var trackedByUsername = trackedPlayers
            .GroupBy(x => UsernameRules.NormalizeUsername(x.Username), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var womRoles = await wiseOldManClient.GetMemberRolesAsync(ct);
        var rows = new List<UngnomifyRow>();

        foreach (var (usernameRaw, roleRaw) in womRoles)
        {
            var role = FormatWomSpecialRankAuditRole(roleRaw);
            if (role is null) continue;

            trackedByUsername.TryGetValue(UsernameRules.NormalizeUsername(usernameRaw), out var player);

            rows.Add(new UngnomifyRow(
                UsernameRules.NormalizeUsername(usernameRaw),
                player is null ? "Untracked" : FormatAuditRank(player.CurrentRank),
                role));
        }

        return rows
            .OrderBy(x => WomSpecialRankAuditRoleSortOrder(x.SpecialRank))
            .ThenBy(x => x.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task RespondWithUngnomifyEmbedsAsync(SocketSlashCommand command, IReadOnlyList<UngnomifyRow> rows)
    {
        if (rows.Count == 0)
        {
            await RespondAndAutoDeleteAsync(command, "No current Goblin or Short green guy roles were found.", ephemeral: false);
            return;
        }

        foreach (var embed in BuildUngnomifyEmbeds(rows))
        {
            await RespondAndAutoDeleteAsync(command, embed, ephemeral: false);
        }
    }

    private static IReadOnlyList<Embed> BuildUngnomifyEmbeds(IReadOnlyList<UngnomifyRow> rows)
    {
        const int rowsPerEmbed = 40;
        var chunks = rows.Chunk(rowsPerEmbed).ToList();
        return chunks.Select((chunk, index) =>
        {
            var builder = new EmbedBuilder()
                .WithTitle(index == 0 ? "Ungnomify" : $"Ungnomify ({index + 1}/{chunks.Count})")
                .WithDescription("Current Goblin and Short green guy roles.")
                .WithColor(new Color(199, 163, 95))
                .AddField("Name", string.Join("\n", chunk.Select(x => x.Username)), true)
                .AddField("Current rank", string.Join("\n", chunk.Select(x => x.CurrentRank)), true)
                .AddField("Special rank", string.Join("\n", chunk.Select(x => x.SpecialRank)), true)
                .WithFooter($"Showing {chunk.Length} of {rows.Count} player(s).")
                .WithTimestamp(DateTimeOffset.Now);
            return builder.Build();
        }).ToList();
    }

    private static string? FormatWomSpecialRankAuditRole(string role)
    {
        var normalized = RankRules.NormalizeRankName(role);
        if (string.Equals(normalized, "goblin", StringComparison.OrdinalIgnoreCase)) return "Goblin";
        if (string.Equals(normalized, "short green guy", StringComparison.OrdinalIgnoreCase)) return "Short green guy";
        return null;
    }

    private static string FormatAuditRank(string rank) =>
        string.IsNullOrWhiteSpace(rank) ? "Unavailable" : RankRules.NormalizeRankName(rank);

    private static int WomSpecialRankAuditRoleSortOrder(string role) =>
        string.Equals(role, "Goblin", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

    private async Task HandleDiscordRankReviewPostSlashCommandAsync(SocketSlashCommand command)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            if (!command.ChannelId.HasValue)
            {
                await RespondAndAutoDeleteAsync(command, "This command must be run in a Discord channel.", ephemeral: false);
                return;
            }

            var channel = await ResolveMessageChannelAsync(command.ChannelId.Value);
            if (channel is null)
            {
                await RespondAndAutoDeleteAsync(command, "Could not resolve the Discord channel for review cards.", ephemeral: false);
                return;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
            var results = await BuildDiscordRankAuditResultsAsync(db, timeout.Token);
            var actionable = results
                .Where(IsActionableDiscordRankReviewResult)
                .OrderBy(x => DiscordRankAuditCsvSortOrder(x.Verdict))
                .ThenBy(x => RankRules.RankOrder(x.DatabaseRank))
                .ThenBy(x => x.Username, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var result in actionable)
            {
                timeout.Token.ThrowIfCancellationRequested();
                await channel.SendMessageAsync(
                    embed: BuildDiscordRankReviewEmbed(result),
                    components: BuildDiscordRankReviewComponents(result));
            }

            await RespondAndAutoDeleteAsync(
                command,
                $"Posted {actionable.Count} Discord rank review card(s). Wrong: {actionable.Count(x => x.Verdict == "WrongRankRole")}, multiple: {actionable.Count(x => x.Verdict == "MultipleRankRoles")}, missing: {actionable.Count(x => x.Verdict == "MissingExpectedRankRole")}.",
                ephemeral: false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            logger.LogWarning("Discord rank review post slash command timed out.");
            await RespondAndAutoDeleteAsync(command, "Discord rank review posting timed out. Try again in a moment.", ephemeral: false);
        }
    }

    private async Task HandleDiscordRankSetSlashCommandAsync(SocketSlashCommand command)
    {
        using var timeout = new CancellationTokenSource(DiscordGuessCommandTimeout);
        try
        {
            var guild = _client?.GetGuild(_options.GuildId);
            if (guild is null)
            {
                await RespondAndAutoDeleteAsync(command, "Discord guild is not available in the bot cache.", ephemeral: false);
                return;
            }

            var rank = command.Data.Options.FirstOrDefault(x => x.Name == "rank")?.Value?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(rank) || !DiscordRankNames.Contains(rank, StringComparer.OrdinalIgnoreCase))
            {
                await RespondAndAutoDeleteAsync(command, "Please select a valid Discord rank role.", ephemeral: false);
                return;
            }

            var rankRoles = BuildDiscordRankRoleConfiguration(guild);
            if (rankRoles.MissingRanks.Contains(rank, StringComparer.OrdinalIgnoreCase))
            {
                await RespondAndAutoDeleteAsync(command, $"No configured Discord role ID for `{EscapeInlineCode(rank)}`.", ephemeral: false);
                return;
            }

            var selectedRole = rankRoles.RolesByRank[rank];
            var guildRole = guild.GetRole(selectedRole.RoleId);
            if (guildRole is null)
            {
                await RespondAndAutoDeleteAsync(command, $"Configured Discord role for `{EscapeInlineCode(rank)}` was not found in this guild.", ephemeral: false);
                return;
            }

            var guildUser = await ResolveSelectedGuildUserAsync(command, guild, timeout.Token);
            if (guildUser is null)
            {
                await RespondAndAutoDeleteAsync(command, "Please select a valid Discord guild member.", ephemeral: false);
                return;
            }

            var update = await ApplyDiscordRankRoleAsync(guildUser, selectedRole, rankRoles);
            var embed = BuildDiscordRankSetEmbed(guildUser, selectedRole, update.RemovedRoles, update.AddedRole, command.User.Username);
            await RespondAndAutoDeleteAsync(command, embed, ephemeral: false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            logger.LogWarning("Discord rank set slash command timed out.");
            await RespondAndAutoDeleteAsync(command, "Discord rank update timed out. Try again in a moment.", ephemeral: false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Discord rank set slash command failed.");
            await RespondAndAutoDeleteAsync(command, $"Discord rank update failed: {EscapeInlineCode(ex.Message)}", ephemeral: false);
        }
    }

    private async Task<IReadOnlyList<DiscordRankAuditResult>> BuildDiscordRankAuditResultsAsync(TrackerDbContext db, CancellationToken ct)
    {
        var players = await db.Players
            .Where(x => x.Status != PlayerStatus.REMOVED_CONFIRMED)
            .OrderBy(x => x.Username)
            .Select(x => new DiscordRankAuditPlayer(x.Id, x.Username, x.CurrentRank, x.Status))
            .ToListAsync(ct);
        var ignoredMismatchPlayerIds = await db.LifecycleEvents
            .Where(x => x.EventType == "WOM_RANK_MISMATCH_IGNORED" && x.Status == "OPEN")
            .Select(x => x.PlayerId)
            .Distinct()
            .ToHashSetAsync(ct);
        players = players
            .Select(x => x with { WomRankMismatchIgnored = ignoredMismatchPlayerIds.Contains(x.Id) })
            .ToList();

        var cachedMembers = await GetCachedDiscordMembersAsync(ct);
        var results = new List<DiscordRankAuditResult>(players.Count);
        foreach (var player in players)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await CheckDiscordRankForPlayerAsync(db, player, cachedMembers, ct));
        }

        return results;
    }

    private async Task<DiscordRankAuditResult> CheckDiscordRankForPlayerAsync(
        TrackerDbContext db,
        DiscordRankAuditPlayer player,
        IReadOnlyList<DiscordMemberLookupCandidate>? cachedMembers,
        CancellationToken ct)
    {
        var normalizedRank = RankRules.NormalizeRankName(player.CurrentRank);
        if (string.Equals(normalizedRank, "Recruit", StringComparison.OrdinalIgnoreCase))
        {
            return DiscordRankAuditResult.Skipped(player, "SkippedRecruit", "Recruit is treated as a transition rank and is not mapped to a Discord role.");
        }

        if (!RankRules.IsKnownClanRank(normalizedRank))
        {
            return DiscordRankAuditResult.Skipped(player, "UnknownDatabaseRank", $"Database rank `{normalizedRank}` is not a known clan rank.");
        }

        var guild = _client?.GetGuild(_options.GuildId);
        if (guild is null)
        {
            return DiscordRankAuditResult.Skipped(player, "DiscordGuildUnavailable", "Discord guild is not available in the bot cache.");
        }

        var rankRoles = BuildDiscordRankRoleConfiguration(guild);
        if (rankRoles.MissingRanks.Contains(normalizedRank, StringComparer.OrdinalIgnoreCase))
        {
            return DiscordRankAuditResult.Skipped(player, "RankRoleMappingMissing", $"No configured Discord role ID for `{normalizedRank}`.");
        }

        var expectedRole = rankRoles.RolesByRank[normalizedRank];
        var aliases = await GetDiscordGuessPlayerNamesAsync(db, player.Id, player.Username, ct);
        var guess = cachedMembers is null
            ? await GuessDiscordMemberForNamesAsync(aliases, ct)
            : DiscordMemberGuessing.Guess(aliases, cachedMembers);
        var exactMatches = guess.Matches
            .Where(x => x.Strength == DiscordMemberMatchStrength.Exact)
            .GroupBy(x => x.UserId)
            .Select(x => x.OrderByDescending(m => m.Score).First())
            .ToList();

        if (exactMatches.Count == 0)
        {
            return DiscordRankAuditResult.Skipped(player, "NoExactDiscordMatch", "No exact Discord username/display/nickname match was found.");
        }

        if (exactMatches.Count > 1)
        {
            return DiscordRankAuditResult.Skipped(player, "AmbiguousExactDiscordMatch", $"{exactMatches.Count} exact Discord matches were found.");
        }

        var match = exactMatches[0];
        var guildUser = await ResolveGuildUserAsync(guild, match.UserId, ct);
        if (guildUser is null)
        {
            return DiscordRankAuditResult.WithMatch(
                player,
                "DiscordUserNotInGuild",
                "Exact match could not be resolved as a current guild member.",
                expectedRole,
                [],
                match);
        }

        var foundRankRoles = GetDiscordRankRolesForUser(guildUser, rankRoles);
        var verdict = foundRankRoles.Count switch
        {
            0 => "MissingExpectedRankRole",
            1 when foundRankRoles[0].RoleId == expectedRole.RoleId => "OK",
            1 when player.WomRankMismatchIgnored && RankRules.RankOrder(foundRankRoles[0].Rank) > RankRules.RankOrder(normalizedRank) => "AllowedHigherRankIgnored",
            1 => "WrongRankRole",
            _ => "MultipleRankRoles"
        };

        var details = verdict switch
        {
            "OK" => "Discord rank role matches the database rank.",
            "AllowedHigherRankIgnored" => "Discord rank role is higher than the database rank, but an open WOM rank mismatch ignore allows this exception.",
            "MissingExpectedRankRole" => "No configured rank role was found on the Discord member.",
            "WrongRankRole" => "A configured rank role was found, but it does not match the database rank.",
            _ => "Multiple configured rank roles were found on the Discord member."
        };

        return DiscordRankAuditResult.WithMatch(player, verdict, details, expectedRole, foundRankRoles, match);
    }

    private static Task<bool> HasOpenWomRankMismatchIgnoreAsync(TrackerDbContext db, int playerId, CancellationToken ct) =>
        db.LifecycleEvents.AnyAsync(x =>
            x.PlayerId == playerId &&
            x.EventType == "WOM_RANK_MISMATCH_IGNORED" &&
            x.Status == "OPEN",
            ct);

    private DiscordRankRoleConfiguration BuildDiscordRankRoleConfiguration(SocketGuild guild)
    {
        var rolesByRank = new Dictionary<string, DiscordRankRole>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var rank in DiscordRankNames)
        {
            if (!TryGetConfiguredRankRoleId(rank, out var roleId))
            {
                missing.Add(rank);
                continue;
            }

            var role = guild.GetRole(roleId);
            rolesByRank[rank] = new DiscordRankRole(rank, roleId, role?.Name ?? $"Unknown role {roleId}");
        }

        var rankByRoleId = rolesByRank.Values.ToDictionary(x => x.RoleId, x => x.Rank);
        return new DiscordRankRoleConfiguration(rolesByRank, rankByRoleId, missing);
    }

    private bool TryGetConfiguredRankRoleId(string rank, out ulong roleId)
    {
        roleId = 0;
        var raw = _options.RankRoleIds
            .FirstOrDefault(x => string.Equals(RankRules.NormalizeRankName(x.Key), rank, StringComparison.OrdinalIgnoreCase))
            .Value;
        return !string.IsNullOrWhiteSpace(raw) && ulong.TryParse(raw, out roleId) && roleId > 0;
    }

    private static IReadOnlyList<DiscordRankRole> GetDiscordRankRolesForUser(
        IGuildUser guildUser,
        DiscordRankRoleConfiguration rankRoles)
    {
        var rankRoleIds = rankRoles.RankByRoleId.Keys.ToHashSet();
        var roleIds = guildUser is SocketGuildUser socketUser
            ? socketUser.Roles.Select(x => x.Id)
            : guildUser.RoleIds;

        return roleIds
            .Where(rankRoleIds.Contains)
            .Distinct()
            .Select(roleId =>
            {
                var rank = rankRoles.RankByRoleId[roleId];
                return rankRoles.RolesByRank[rank];
            })
            .OrderBy(x => RankRules.RankOrder(x.Rank))
            .ToList();
    }

    private static async Task<DiscordRankRoleUpdateResult> ApplyDiscordRankRoleAsync(
        IGuildUser guildUser,
        DiscordRankRole selectedRole,
        DiscordRankRoleConfiguration rankRoles)
    {
        var existingRankRoles = GetDiscordRankRolesForUser(guildUser, rankRoles);
        var removeRoleIds = existingRankRoles
            .Where(x => x.RoleId != selectedRole.RoleId)
            .Select(x => x.RoleId)
            .Distinct()
            .ToList();
        var userRoleIds = guildUser is SocketGuildUser socketUser
            ? socketUser.Roles.Select(x => x.Id).ToHashSet()
            : guildUser.RoleIds.ToHashSet();
        var addRole = !userRoleIds.Contains(selectedRole.RoleId);

        foreach (var roleId in removeRoleIds)
        {
            await guildUser.RemoveRoleAsync(roleId);
        }

        if (addRole)
        {
            await guildUser.AddRoleAsync(selectedRole.RoleId);
        }

        var removedRoles = existingRankRoles
            .Where(x => removeRoleIds.Contains(x.RoleId))
            .ToList();
        return new DiscordRankRoleUpdateResult(removedRoles, addRole);
    }

    private static async Task<IGuildUser?> ResolveGuildUserAsync(SocketGuild guild, ulong userId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var socketUser = guild.GetUser(userId);
        if (socketUser is not null) return socketUser;
        return await ((IGuild)guild).GetUserAsync(userId, CacheMode.AllowDownload);
    }

    private static async Task<IGuildUser?> ResolveSelectedGuildUserAsync(SocketSlashCommand command, SocketGuild guild, CancellationToken ct)
    {
        var userValue = command.Data.Options.FirstOrDefault(x => x.Name == "user")?.Value;
        return userValue switch
        {
            IGuildUser guildUser => guildUser,
            IUser user => await ResolveGuildUserAsync(guild, user.Id, ct),
            ulong userId => await ResolveGuildUserAsync(guild, userId, ct),
            _ when ulong.TryParse(userValue?.ToString(), out var parsedUserId) => await ResolveGuildUserAsync(guild, parsedUserId, ct),
            _ => null
        };
    }

    private async Task HandleDiscordRankReviewButtonAsync(SocketMessageComponent component, string[] parts)
    {
        var action = parts[1];
        if (string.Equals(action, "dismiss", StringComparison.OrdinalIgnoreCase))
        {
            await RespondToComponentAsync(component, "Dismissed Discord rank review card.", ephemeral: true);
            await DeleteDiscordRankReviewCardAsync(component);
            return;
        }

        if (!string.Equals(action, "accept", StringComparison.OrdinalIgnoreCase) ||
            parts.Length != 4 ||
            !ulong.TryParse(parts[2], out var userId))
        {
            await RespondToComponentAsync(component, "Unknown action.", ephemeral: true);
            return;
        }

        var rank = parts[3];
        await ApplyDiscordRankReviewRoleAsync(component, userId, rank, "Accepted");
    }

    private async Task HandleDiscordRankReviewManualSelectAsync(SocketMessageComponent component, string customId)
    {
        var parts = customId.Split(':');
        if (parts.Length != 3 ||
            !string.Equals(parts[0], "drankreview", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(parts[1], "manual", StringComparison.OrdinalIgnoreCase) ||
            !ulong.TryParse(parts[2], out var userId))
        {
            await RespondToComponentAsync(component, "Malformed rank selection.", ephemeral: true);
            return;
        }

        var rank = component.Data.Values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(rank))
        {
            await RespondToComponentAsync(component, "No rank selected.", ephemeral: true);
            return;
        }

        await ApplyDiscordRankReviewRoleAsync(component, userId, rank, "Manual rank");
    }

    private async Task ApplyDiscordRankReviewRoleAsync(SocketMessageComponent component, ulong userId, string rank, string actionLabel)
    {
        try
        {
            var guild = _client?.GetGuild(_options.GuildId);
            if (guild is null)
            {
                await RespondToComponentAsync(component, "Discord guild is not available in the bot cache.", ephemeral: true);
                return;
            }

            if (!DiscordRankNames.Contains(rank, StringComparer.OrdinalIgnoreCase))
            {
                await RespondToComponentAsync(component, "Selected rank is not a configured clan rank.", ephemeral: true);
                return;
            }

            var rankRoles = BuildDiscordRankRoleConfiguration(guild);
            if (rankRoles.MissingRanks.Contains(rank, StringComparer.OrdinalIgnoreCase))
            {
                await RespondToComponentAsync(component, $"No configured Discord role ID for `{EscapeInlineCode(rank)}`.", ephemeral: true);
                return;
            }

            var selectedRole = rankRoles.RolesByRank[rank];
            if (guild.GetRole(selectedRole.RoleId) is null)
            {
                await RespondToComponentAsync(component, $"Configured Discord role for `{EscapeInlineCode(rank)}` was not found in this guild.", ephemeral: true);
                return;
            }

            var guildUser = await ResolveGuildUserAsync(guild, userId, CancellationToken.None);
            if (guildUser is null)
            {
                await RespondToComponentAsync(component, "Discord member could not be resolved.", ephemeral: true);
                return;
            }

            var update = await ApplyDiscordRankRoleAsync(guildUser, selectedRole, rankRoles);
            var removedText = update.RemovedRoles.Count == 0
                ? "none"
                : string.Join(", ", update.RemovedRoles.Select(x => x.RoleName));
            await RespondToComponentAsync(
                component,
                $"{actionLabel}: set {guildUser.Mention} to {FormatDiscordRoleMention(selectedRole.RoleId, selectedRole.RoleName)}. Removed: {removedText}.",
                ephemeral: true);
            await DeleteDiscordRankReviewCardAsync(component);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed applying Discord rank review action.");
            await RespondToComponentAsync(component, $"Discord rank update failed: {EscapeInlineCode(ex.Message)}", ephemeral: true);
        }
    }

    private static async Task DeleteDiscordRankReviewCardAsync(SocketMessageComponent component)
    {
        try
        {
            await component.Message.DeleteAsync();
        }
        catch
        {
            // best-effort cleanup; the ephemeral acknowledgement above still tells the officer what happened.
        }
    }

    private static bool IsActionableDiscordRankReviewResult(DiscordRankAuditResult result) =>
        result.DiscordUserId.HasValue &&
        result.Verdict is "WrongRankRole" or "MultipleRankRoles" or "MissingExpectedRankRole";

    private static Embed BuildDiscordRankReviewEmbed(DiscordRankAuditResult result)
    {
        var currentRoles = FormatDiscordRoleMentions(result.FoundRoleIds, result.FoundRoleNames);
        return new EmbedBuilder()
            .WithTitle("Discord Rank Review")
            .WithColor(new Color(245, 158, 11))
            .WithDescription("Review the exact Discord match before applying a role change.")
            .AddField("Player", result.Username, true)
            .AddField("Discord Member", result.DiscordMention ?? "N/A", true)
            .AddField("Verdict", result.Verdict, true)
            .AddField("Should Have", FormatDiscordRoleMention(result.ExpectedRoleId, result.ExpectedRoleName), true)
            .AddField("Currently Has", currentRoles, true)
            .AddField("Matched On", string.IsNullOrWhiteSpace(result.MatchedField) ? "N/A" : $"{result.MatchedField}: `{EscapeInlineCode(result.MatchedValue ?? "")}`", false)
            .WithTimestamp(DateTimeOffset.Now)
            .Build();
    }

    private static MessageComponent BuildDiscordRankReviewComponents(DiscordRankAuditResult result)
    {
        var userId = result.DiscordUserId!.Value;
        var rank = RankRules.NormalizeRankName(result.DatabaseRank);
        var menu = new SelectMenuBuilder()
            .WithCustomId($"drankreview:manual:{userId}")
            .WithPlaceholder("Manual rank")
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var rankName in DiscordRankNames)
        {
            menu.AddOption(rankName, rankName);
        }

        return new ComponentBuilder()
            .WithButton("Accept", $"drankreview:accept:{userId}:{rank}", ButtonStyle.Success)
            .WithButton("Dismiss", $"drankreview:dismiss:{userId}", ButtonStyle.Secondary)
            .WithSelectMenu(menu)
            .Build();
    }

    private static Embed BuildDiscordRankSetEmbed(
        IGuildUser guildUser,
        DiscordRankRole assignedRole,
        IReadOnlyList<DiscordRankRole> removedRoles,
        bool addedRole,
        string handledBy)
    {
        var removedText = removedRoles.Count == 0
            ? "None"
            : FormatDiscordRoleMentions(removedRoles.Select(x => x.RoleId).ToList(), removedRoles.Select(x => x.RoleName).ToList());
        var actionText = addedRole
            ? $"Added {FormatDiscordRoleMention(assignedRole.RoleId, assignedRole.RoleName)}"
            : $"{FormatDiscordRoleMention(assignedRole.RoleId, assignedRole.RoleName)} was already present";

        return new EmbedBuilder()
            .WithTitle("Discord Rank Role Updated")
            .WithColor(new Color(34, 197, 94))
            .AddField("Member", guildUser.Mention, true)
            .AddField("Assigned Rank Role", actionText, true)
            .AddField("Removed Rank Roles", removedText, false)
            .WithFooter($"Handled by {handledBy}")
            .WithTimestamp(DateTimeOffset.Now)
            .Build();
    }

    private static Embed BuildDiscordRankCheckEmbed(DiscordRankAuditResult result)
    {
        var color = result.Verdict == "OK"
            ? new Color(34, 197, 94)
            : result.Verdict.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase) || result.Verdict.Contains("Match", StringComparison.OrdinalIgnoreCase)
                ? new Color(245, 158, 11)
                : new Color(239, 68, 68);

        var builder = new EmbedBuilder()
            .WithTitle($"Discord Rank Check: {result.Username}")
            .WithColor(color)
            .AddField("Verdict", result.Verdict, true)
            .AddField("Database Rank", result.DatabaseRank, true)
            .AddField("Player Status", result.PlayerStatus.ToString(), true)
            .AddField("Discord Match", string.IsNullOrWhiteSpace(result.DiscordMention) ? "N/A" : result.DiscordMention, true)
            .AddField("Matched On", string.IsNullOrWhiteSpace(result.MatchSummary) ? "N/A" : result.MatchSummary, true)
            .AddField("Expected Role", FormatDiscordRoleMention(result.ExpectedRoleId, result.ExpectedRoleName), true)
            .AddField("WOM Mismatch Ignored", result.WomRankMismatchIgnored ? "Yes" : "No", true)
            .AddField("Found Rank Roles", FormatDiscordRoleMentions(result.FoundRoleIds, result.FoundRoleNames), false)
            .AddField("Details", result.Details, false)
            .WithTimestamp(DateTimeOffset.Now);

        return builder.Build();
    }

    private static string FormatDiscordRoleMention(ulong? roleId, string? fallbackName)
    {
        if (roleId.HasValue && roleId.Value > 0)
        {
            return $"<@&{roleId.Value}>";
        }

        return string.IsNullOrWhiteSpace(fallbackName) ? "N/A" : fallbackName;
    }

    private static string FormatDiscordRoleMentions(IReadOnlyList<ulong> roleIds, IReadOnlyList<string> fallbackNames)
    {
        if (roleIds.Count > 0)
        {
            return string.Join(", ", roleIds.Select(roleId => $"<@&{roleId}>"));
        }

        return fallbackNames.Count == 0 ? "None" : string.Join(", ", fallbackNames);
    }

    private static Embed BuildDiscordRankAuditSummaryEmbed(IReadOnlyList<DiscordRankAuditResult> results)
    {
        var grouped = results
            .GroupBy(x => x.Verdict)
            .OrderByDescending(x => x.Key == "OK")
            .ThenBy(x => x.Key)
            .ToList();
        var mismatchCount = results.Count(x => x.Verdict is "MissingExpectedRankRole" or "WrongRankRole" or "MultipleRankRoles");
        var allowedIgnoredCount = results.Count(x => x.Verdict == "AllowedHigherRankIgnored");
        var skippedCount = results.Count(x => x.Verdict.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase) ||
                                             x.Verdict is "NoExactDiscordMatch" or "AmbiguousExactDiscordMatch" or "RankRoleMappingMissing" or "UnknownDatabaseRank");

        var builder = new EmbedBuilder()
            .WithTitle("Discord Rank Audit")
            .WithColor(mismatchCount == 0 ? new Color(34, 197, 94) : new Color(245, 158, 11))
            .WithDescription("Exact Discord matches only. Full details are attached as CSV.")
            .AddField("Players Scanned", results.Count.ToString(CultureInfo.InvariantCulture), true)
            .AddField("OK", results.Count(x => x.Verdict == "OK").ToString(CultureInfo.InvariantCulture), true)
            .AddField("Needs Review", mismatchCount.ToString(CultureInfo.InvariantCulture), true)
            .AddField("Allowed Higher", allowedIgnoredCount.ToString(CultureInfo.InvariantCulture), true)
            .AddField("Skipped", skippedCount.ToString(CultureInfo.InvariantCulture), true)
            .WithTimestamp(DateTimeOffset.Now);

        var lines = grouped.Select(x => $"{x.Key}: {x.Count()}");
        builder.AddField("Verdicts", string.Join("\n", lines), false);
        return builder.Build();
    }

    private static string BuildDiscordRankAuditCsv(IReadOnlyList<DiscordRankAuditResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Verdict,Username,Database Rank,Expected Role,Found Rank Roles,WOM Mismatch Ignored,Discord Display Name,Matched On,Matched Value,Player Alias,Player Status,Details");
        foreach (var result in results
            .OrderBy(x => DiscordRankAuditCsvSortOrder(x.Verdict))
            .ThenBy(x => RankRules.RankOrder(x.DatabaseRank))
            .ThenBy(x => x.Username, StringComparer.OrdinalIgnoreCase))
        {
            var fields = new[]
            {
                result.Verdict,
                result.Username,
                result.DatabaseRank,
                result.ExpectedRoleName ?? "",
                string.Join(";", result.FoundRoleNames),
                result.WomRankMismatchIgnored ? "Yes" : "No",
                result.DiscordDisplayName ?? "",
                result.MatchedField ?? "",
                result.MatchedValue ?? "",
                result.PlayerAlias ?? "",
                result.PlayerStatus.ToString(),
                result.Details
            };
            sb.AppendLine(string.Join(",", fields.Select(EscapeCsv)));
        }

        return sb.ToString();
    }

    private static int DiscordRankAuditCsvSortOrder(string verdict) =>
        verdict switch
        {
            "WrongRankRole" => 0,
            "MultipleRankRoles" => 1,
            "MissingExpectedRankRole" => 2,
            "DiscordUserNotInGuild" => 3,
            "RankRoleMappingMissing" => 4,
            "UnknownDatabaseRank" => 5,
            "AmbiguousExactDiscordMatch" => 6,
            "NoExactDiscordMatch" => 7,
            "OK" => 8,
            "AllowedHigherRankIgnored" => 9,
            "SkippedRecruit" => 10,
            _ => 11
        };

    private async Task RespondWithCsvAsync(SocketSlashCommand command, Embed embed, string csv, string fileName, bool ephemeral = false)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var response = await command.FollowupWithFileAsync(stream, fileName, embed: embed, ephemeral: ephemeral);
        var messageDescription = BuildSlashEmbedCleanupDescription(command, embed);
        if (ephemeral)
        {
            await ScheduleInteractionResponseDeleteAsync(command, messageDescription);
        }
        else if (command.ChannelId.HasValue)
        {
            await ScheduleChannelResponseDeleteAsync(command.ChannelId.Value, response.Id, $"slash-{command.CommandName}-followup", messageDescription);
        }
        else
        {
            await ScheduleInteractionResponseDeleteAsync(command, messageDescription);
        }
    }

    private static string EscapeCsv(string value)
    {
        var normalized = value ?? "";
        return "\"" + normalized.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private async Task HandleWomUnignoreSlashCommandAsync(SocketSlashCommand command)
    {
        var playerRaw = command.Data.Options.FirstOrDefault(x => x.Name == "player")?.Value?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(playerRaw))
        {
            await RespondAndAutoDeleteAsync(command, "Please provide a player username.", ephemeral: false);
            return;
        }

        var normalizedUsername = NormalizeUsername(playerRaw);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();

        var openWomOnlyIgnores = await db.LifecycleEvents
            .Where(x => x.EventType == "WOM_ONLY_IGNORED" && x.Status == "OPEN")
            .OrderBy(x => x.CreatedAt)
            .ToListAsync();
        var clearedWomOnlyIgnores = 0;
        foreach (var ignoreEvent in openWomOnlyIgnores)
        {
            var metadata = ReadLifecycleMetadata(ignoreEvent.MetadataJson);
            var ignoredUsername = PickLifecycleValue(metadata, "Username", "Player");
            if (!string.Equals(NormalizeUsername(ignoredUsername ?? ""), normalizedUsername, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ignoreEvent.Status = "DONE";
            clearedWomOnlyIgnores++;
        }

        var matchingPlayer = await db.Players
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(x => x.Username.ToLower() == normalizedUsername.ToLower());

        var clearedWomRankMismatchIgnores = 0;
        if (matchingPlayer is not null)
        {
            var openMismatchIgnores = await db.LifecycleEvents
                .Where(x =>
                    x.PlayerId == matchingPlayer.Id &&
                    x.EventType == "WOM_RANK_MISMATCH_IGNORED" &&
                    x.Status == "OPEN")
                .ToListAsync();
            foreach (var mismatchIgnore in openMismatchIgnores)
            {
                mismatchIgnore.Status = "DONE";
                clearedWomRankMismatchIgnores++;
            }
        }

        if (clearedWomOnlyIgnores > 0 || clearedWomRankMismatchIgnores > 0)
        {
            var ownerId = matchingPlayer?.Id ?? await ResolveLifecycleOwnerPlayerIdAsync(db, 0, CancellationToken.None);
            if (ownerId.HasValue)
            {
                db.LifecycleEvents.Add(new LifecycleEvent
                {
                    PlayerId = ownerId.Value,
                    EventType = "WOM_ONLY_ACTION_APPLIED",
                    MetadataJson = JsonUtil.Serialize(new
                    {
                        Username = normalizedUsername,
                        Action = "unignore",
                        ClearedWomOnlyIgnores = clearedWomOnlyIgnores,
                        ClearedWomRankMismatchIgnores = clearedWomRankMismatchIgnores,
                        HandledBy = command.User.Username,
                        HandledByDiscordUserId = command.User.Id,
                        Source = "discord-slash"
                    }),
                    Status = "DONE",
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
        }

        await db.SaveChangesAsync();

        await RespondAndAutoDeleteAsync(
            command,
            $"Cleared ignore flags for `{normalizedUsername}`. WOM-only ignores: {clearedWomOnlyIgnores}. WOM rank mismatch ignores: {clearedWomRankMismatchIgnores}. Changes take effect on the next sync cycle.",
            ephemeral: false);
    }

    private async Task HandleRequeueReviewCardSlashCommandAsync(SocketSlashCommand command)
    {
        var playerRaw = command.Data.Options.FirstOrDefault(x => x.Name == "player")?.Value?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(playerRaw))
        {
            await RespondAndAutoDeleteAsync(command, "Please provide a player username.", ephemeral: false);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var player = await db.Players.FirstOrDefaultAsync(x => x.Username.ToLower() == playerRaw.ToLower());
        if (player is null)
        {
            await RespondAndAutoDeleteAsync(command, $"No player found for `{playerRaw}`.", ephemeral: false);
            return;
        }

        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = player.Id,
            EventType = "DISCORD_REVIEW_REQUEUE_REQUESTED",
            MetadataJson = JsonUtil.Serialize(new
            {
                Player = player.Username,
                HandledBy = command.User.Username,
                HandledByDiscordUserId = command.User.Id,
                Source = "discord-slash"
            }),
            Status = "OPEN",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        await RespondAndAutoDeleteAsync(command, $"Queued review-card requeue for `{player.Username}`.", ephemeral: false);
    }

    private async Task HandleHistorySlashCommandAsync(SocketSlashCommand command)
    {
        var limit = ParseHistoryRowLimit(command);
        var everything = ParseHistoryEverything(command);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();

        var query = db.LifecycleEvents
            .Where(x => x.EventType != AppStatusConstants.EventType);
        if (!everything)
        {
            query = query.Where(x => AdminHistoryEventTypes.Contains(x.EventType));
        }

        var rawRows = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(everything ? Math.Max(limit * 5, 100) : limit)
            .Select(x => new
            {
                x.Id,
                x.PlayerId,
                x.EventType,
                x.MetadataJson,
                x.Status,
                x.CreatedAt,
                Player = db.Players
                    .Where(p => p.Id == x.PlayerId)
                    .Select(p => p.Username)
                    .FirstOrDefault()
            })
            .ToListAsync();

        var rows = rawRows
            .Select(x => new HistoryEventRow(
                x.Id,
                x.PlayerId,
                x.Player,
                x.EventType,
                ReadLifecycleMetadata(x.MetadataJson),
                x.Status,
                x.CreatedAt))
            .Where(x => !IsHistoryCommandLog(x))
            .Take(limit)
            .ToList();

        await RespondPrivatelyAsync(command, BuildHistoryEmbed(rows, limit, everything));
    }

    private Embed BuildHistoryEmbed(IReadOnlyList<HistoryEventRow> rows, int requestedLimit, bool everything)
    {
        var builder = new EmbedBuilder()
            .WithTitle(everything ? "Recent Diagnostic History" : "Recent Admin History")
            .WithColor(everything ? new Color(96, 165, 250) : new Color(180, 137, 64))
            .WithDescription(rows.Count == 0
                ? "No recent history rows found for this view."
                : everything
                    ? $"Showing {rows.Count} recent tracker events, including system noise."
                    : $"Showing {rows.Count} recent admin-relevant events.")
            .WithFooter($"Rows: {requestedLimit} | Everything: {(everything ? "yes" : "no")}")
            .WithTimestamp(DateTimeOffset.Now);

        foreach (var row in rows)
        {
            builder.AddField(
                ShortenHistoryText($"{FormatDiscordRelativeTime(row.CreatedAt)} | {FormatHistoryEventLabel(row.EventType)}", 256),
                BuildHistoryFieldValue(row),
                false);
        }

        return builder.Build();
    }

    private string BuildHistoryFieldValue(HistoryEventRow row)
    {
        var summary = BuildHistorySummary(row);
        var actor = PickLifecycleValue(row.Metadata, "HandledBy", "RequestedBy", "User", "IgnoredBy");
        var details = new List<string>();

        if (!string.IsNullOrWhiteSpace(actor)) details.Add($"By {EscapeHistoryText(actor)}");

        return details.Count == 0
            ? ShortenHistoryText(summary, 1024)
            : ShortenHistoryText($"{summary}\n{string.Join(" | ", details)}", 1024);
    }

    private static string BuildHistorySummary(HistoryEventRow row)
    {
        var player = GetHistoryPlayerName(row);
        var playerText = string.IsNullOrWhiteSpace(player) ? "Player" : $"`{EscapeInlineCode(player)}`";
        var action = PickLifecycleValue(row.Metadata, "Action");
        var commandName = PickLifecycleValue(row.Metadata, "Command", "CommandName");
        var commandTarget = GetHistoryCommandTarget(row);
        var actor = PickLifecycleValue(row.Metadata, "HandledBy", "RequestedBy", "User", "IgnoredBy");
        var newPlayer = PickLifecycleValue(row.Metadata, "NewPlayer", "Username", "Player");
        var previousPlayer = PickLifecycleValue(row.Metadata, "SuggestedPrevious", "PreviousPlayer", "PreviousUsername");
        var newRank = PickLifecycleValue(row.Metadata, "NewRank");
        var oldRank = PickLifecycleValue(row.Metadata, "OldRank");
        var expectedRank = PickLifecycleValue(row.Metadata, "ExpectedRank");
        var actualWomRole = PickLifecycleValue(row.Metadata, "ActualWomRole");
        var requestedRole = PickLifecycleValue(row.Metadata, "RequestedRole");
        var updatedRole = PickLifecycleValue(row.Metadata, "UpdatedRole");

        return row.EventType switch
        {
            "NEW_PLAYER" => $"{playerText} joined the tracker and needs review.",
            "MISSING_IN_ROSTER" => $"{playerText} disappeared from the roster.",
            "STATUS_UPDATED" => $"{playerText} moved to `{EscapeInlineCode(PickLifecycleValue(row.Metadata, "Status") ?? row.Status)}`.",
            "PRIORITY_UPDATE_REQUEST" => $"{playerText} was queued for immediate sync.",
            "MERGE_SUGGESTED" => $"Possible rename: `{EscapeInlineCode(newPlayer ?? "new player")}` may be `{EscapeInlineCode(previousPlayer ?? "previous player")}`.",
            "MERGE_ACTION_REQUIRED" => $"Rename review opened for `{EscapeInlineCode(newPlayer ?? player ?? "player")}`.",
            "MERGE_ACTION_APPLIED" => $"Rename review resolved: `{EscapeInlineCode(action ?? "unknown")}`.",
            "TEMPLE_MISSING_ACTION_REQUIRED" => $"{playerText} needs a Temple decision.",
            "TEMPLE_MISSING_ACTION_APPLIED" => $"{playerText} Temple review resolved: `{EscapeInlineCode(action ?? "unknown")}`.",
            "WOM_MISSING_ACTION_REQUIRED" => $"{playerText} needs a Wise Old Man decision.",
            "WOM_MISSING_ACTION_APPLIED" => $"{playerText} Wise Old Man review resolved: `{EscapeInlineCode(action ?? "unknown")}`.",
            "WOM_ONLY_ACTION_REQUIRED" => $"{playerText} is in Wise Old Man only.",
            "WOM_ONLY_ACTION_APPLIED" => $"{playerText} Wise Old Man only review resolved: `{EscapeInlineCode(action ?? "unknown")}`.",
            "WOM_RANK_MISMATCH_REQUIRED" => $"{playerText} rank mismatch: tracker `{EscapeInlineCode(expectedRank ?? "unknown")}`, WOM `{EscapeInlineCode(actualWomRole ?? "unknown")}`.",
            "WOM_RANK_MISMATCH_ACTION_APPLIED" => $"{playerText} rank mismatch resolved: `{EscapeInlineCode(action ?? "unknown")}`.",
            "WOM_ROLE_UPDATE_APPLIED" => $"{playerText} WOM role update: `{EscapeInlineCode(requestedRole ?? "unknown")}` -> `{EscapeInlineCode(updatedRole ?? "unknown")}`.",
            "PROMOTION_CANDIDATE_CREATED" => oldRank is null || newRank is null
                ? $"{playerText} is ready for promotion."
                : $"{playerText} is ready for promotion: `{EscapeInlineCode(oldRank)}` -> `{EscapeInlineCode(newRank)}`.",
            "PROMOTION_DISCORD_POSTED" => $"{playerText} promotion card was posted.",
            "PROMOTION_DISCORD_ACTION_APPLIED" => $"{playerText} promotion resolved: `{EscapeInlineCode(action ?? "unknown")}`.",
            "DISCORD_MARK_RENAME_SUSPECT" => $"{playerText} promotion flagged as possible rename.",
            "DISCORD_REVIEW_REQUEUE_REQUESTED" => $"{playerText} review card was requeued.",
            "DISCORD_POSTED_MESSAGE_MISSING" => $"{playerText} Discord review card is missing.",
            "DISCORD_SLASH_COMMAND_USED" => string.IsNullOrWhiteSpace(commandName)
                ? $"Slash command used{(string.IsNullOrWhiteSpace(actor) ? "" : $" by {EscapeHistoryText(actor)}")}."
                : $"Slash command `/{EscapeInlineCode(commandName)}` used{FormatHistoryTargetClause(commandTarget)}{(string.IsNullOrWhiteSpace(actor) ? "" : $" by {EscapeHistoryText(actor)}")}.",
            "PLAYER_SNAPSHOT_CONTAMINATION_REQUIRED" => $"{playerText} has suspicious stat data and needs review.",
            "PET_HISCORES_DISCORD_POSTED" => $"Pet hiscores page `{EscapeInlineCode(PickLifecycleValue(row.Metadata, "Page") ?? "unknown")}` was posted.",
            "PET_HISCORES_BANNER_POSTED" => "Pet hiscores banner was posted.",
            _ => $"{FormatHistoryEventName(row.EventType)} was recorded."
        };
    }

    private static int ParseHistoryRowLimit(SocketSlashCommand command)
    {
        var raw = command.Data.Options.FirstOrDefault(x => x.Name == "rows")?.Value;
        if (raw is null) return 5;

        try
        {
            return Math.Clamp(Convert.ToInt32(raw, CultureInfo.InvariantCulture), 1, 25);
        }
        catch
        {
            return 5;
        }
    }

    private static bool ParseHistoryEverything(SocketSlashCommand command)
    {
        var raw = command.Data.Options.FirstOrDefault(x => x.Name == "everything")?.Value;
        return raw is bool value && value;
    }

    private static bool IsHistoryCommandLog(HistoryEventRow row)
    {
        return string.Equals(row.EventType, "DISCORD_SLASH_COMMAND_USED", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(PickLifecycleValue(row.Metadata, "Command", "CommandName"), "history", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetHistoryPlayerName(HistoryEventRow row)
    {
        if (row.EventType.EndsWith("_ACTION_APPLIED", StringComparison.OrdinalIgnoreCase) ||
            row.EventType.EndsWith("_ACTION_REQUIRED", StringComparison.OrdinalIgnoreCase) ||
            row.EventType.EndsWith("_DISCORD_POSTED", StringComparison.OrdinalIgnoreCase) ||
            row.EventType.StartsWith("WOM_ONLY_", StringComparison.OrdinalIgnoreCase))
        {
            return PickLifecycleValue(row.Metadata, "Username", "Player", "NewPlayer") ?? row.Player;
        }

        return row.Player ?? PickLifecycleValue(row.Metadata, "Username", "Player", "NewPlayer");
    }

    private static string? GetHistoryCommandTarget(HistoryEventRow row)
    {
        return PickLifecycleValue(
            row.Metadata,
            "Option.players",
            "Option.player",
            "Option.username",
            "Player",
            "Username",
            "NewPlayer");
    }

    private static string FormatHistoryTargetClause(string? target)
    {
        return string.IsNullOrWhiteSpace(target)
            ? ""
            : $" for `{EscapeInlineCode(target)}`";
    }

    private static string FormatHistoryEventName(string eventType)
    {
        var text = eventType.Replace("_", " ");
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
    }

    private static string FormatHistoryEventLabel(string eventType)
    {
        return eventType switch
        {
            "PROMOTION_CANDIDATE_CREATED" => "Promotion ready",
            "PROMOTION_DISCORD_ACTION_APPLIED" => "Promotion resolved",
            "NEW_PLAYER" => "New member",
            "MISSING_IN_ROSTER" => "Missing member",
            "STATUS_UPDATED" => "Status changed",
            "MERGE_SUGGESTED" => "Possible rename",
            "MERGE_ACTION_APPLIED" => "Rename resolved",
            "TEMPLE_MISSING_ACTION_APPLIED" => "Temple resolved",
            "WOM_MISSING_ACTION_APPLIED" => "WOM resolved",
            "WOM_ONLY_ACTION_APPLIED" => "WOM-only resolved",
            "WOM_RANK_MISMATCH_REQUIRED" => "Rank mismatch",
            "WOM_RANK_MISMATCH_ACTION_APPLIED" => "Rank fixed",
            "WOM_ROLE_UPDATE_APPLIED" => "WOM role update",
            "PLAYER_SNAPSHOT_CONTAMINATION_REQUIRED" => "Snapshot warning",
            _ => FormatHistoryEventName(eventType)
        };
    }

    private static string FormatDiscordRelativeTime(DateTimeOffset value)
    {
        return $"<t:{value.ToUnixTimeSeconds()}:R>";
    }

    private static string EscapeHistoryText(string value)
    {
        return value.Replace("`", "'", StringComparison.Ordinal);
    }

    private static string ShortenHistoryText(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        return value[..Math.Max(0, maxLength - 3)] + "...";
    }

    private static async Task RespondPrivatelyAsync(SocketSlashCommand command, Embed embed)
    {
        await command.FollowupAsync(embed: embed, ephemeral: true);
    }

    private sealed record HistoryEventRow(
        int Id,
        int PlayerId,
        string? Player,
        string EventType,
        Dictionary<string, string> Metadata,
        string Status,
        DateTimeOffset CreatedAt);

    private async Task HandleShowIgnoredSlashCommandAsync(SocketSlashCommand command)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();

        var womOnlyIgnoredEvents = await db.LifecycleEvents
            .Where(x => x.EventType == "WOM_ONLY_IGNORED" && x.Status == "OPEN")
            .OrderBy(x => x.CreatedAt)
            .ToListAsync();
        var womOnlyIgnored = womOnlyIgnoredEvents
            .Select(x =>
            {
                var metadata = ReadLifecycleMetadata(x.MetadataJson);
                return NormalizeUsername(PickLifecycleValue(metadata, "Username", "Player") ?? "");
            })
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var womRankMismatchIgnoredPlayerIds = await db.LifecycleEvents
            .Where(x => x.EventType == "WOM_RANK_MISMATCH_IGNORED" && x.Status == "OPEN")
            .Select(x => x.PlayerId)
            .Distinct()
            .ToListAsync();
        var womRankMismatchIgnored = womRankMismatchIgnoredPlayerIds.Count == 0
            ? new List<string>()
            : await db.Players
                .Where(x => womRankMismatchIgnoredPlayerIds.Contains(x.Id))
                .OrderBy(x => x.Username)
                .Select(x => x.Username)
                .ToListAsync();

        List<string> ToDisplayChunks(IReadOnlyList<string> names)
        {
            if (names.Count == 0) return ["None"];

            const int maxFieldValueLength = 1024;
            var chunks = new List<string>();
            var current = new StringBuilder();
            foreach (var name in names)
            {
                var line = $"• {name}";
                if (current.Length > 0 && current.Length + 1 + line.Length > maxFieldValueLength)
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.AppendLine();
                }
                current.Append(line);
            }
            if (current.Length > 0)
            {
                chunks.Add(current.ToString());
            }

            return chunks;
        }

        static void AddChunkedFields(EmbedBuilder builder, string title, int count, IReadOnlyList<string> chunks)
        {
            for (var i = 0; i < chunks.Count; i++)
            {
                var label = chunks.Count == 1 ? $"{title} ({count})" : $"{title} ({count}) {i + 1}/{chunks.Count}";
                builder.AddField(label, chunks[i], false);
            }
        }

        static string BuildAttachedList(IReadOnlyList<string> womOnly, IReadOnlyList<string> womRankMismatch)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Ignored Players");
            sb.AppendLine();
            sb.AppendLine($"WOM-only ignored ({womOnly.Count})");
            foreach (var name in womOnly)
            {
                sb.AppendLine($"- {name}");
            }
            if (womOnly.Count == 0)
            {
                sb.AppendLine("- None");
            }
            sb.AppendLine();
            sb.AppendLine($"WOM rank mismatch ignored ({womRankMismatch.Count})");
            foreach (var name in womRankMismatch)
            {
                sb.AppendLine($"- {name}");
            }
            if (womRankMismatch.Count == 0)
            {
                sb.AppendLine("- None");
            }
            return sb.ToString();
        }

        var womOnlyChunks = ToDisplayChunks(womOnlyIgnored);
        var womRankMismatchChunks = ToDisplayChunks(womRankMismatchIgnored);
        var totalFieldCount = womOnlyChunks.Count + womRankMismatchChunks.Count;
        var totalFieldValueLength = womOnlyChunks.Sum(x => x.Length) + womRankMismatchChunks.Sum(x => x.Length);

        var embedBuilder = new EmbedBuilder()
            .WithTitle("Ignored Players")
            .WithColor(new Color(59, 130, 246))
            .WithDescription("Currently open ignore flags by category.")
            .WithTimestamp(DateTimeOffset.Now);

        if (totalFieldCount <= 25 && totalFieldValueLength <= 5500)
        {
            AddChunkedFields(embedBuilder, "WOM-only ignored", womOnlyIgnored.Count, womOnlyChunks);
            AddChunkedFields(embedBuilder, "WOM rank mismatch ignored", womRankMismatchIgnored.Count, womRankMismatchChunks);
            await RespondAndAutoDeleteAsync(command, embedBuilder.Build(), ephemeral: true);
            return;
        }

        embedBuilder
            .WithDescription("Currently open ignore flags by category. Full list attached.")
            .AddField("WOM-only ignored", womOnlyIgnored.Count.ToString(CultureInfo.InvariantCulture), true)
            .AddField("WOM rank mismatch ignored", womRankMismatchIgnored.Count.ToString(CultureInfo.InvariantCulture), true);

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(BuildAttachedList(womOnlyIgnored, womRankMismatchIgnored)));
        await command.FollowupWithFileAsync(stream, "ignored-players.txt", embed: embedBuilder.Build(), ephemeral: true);
        await ScheduleInteractionResponseDeleteAsync(command, BuildSlashEmbedCleanupDescription(command, embedBuilder.Build()));
    }

    private async Task HandleHelpSlashCommandAsync(SocketSlashCommand command)
    {
        var helpText = """
### Info / Sync
**/history [rows]**  
Shows recent admin history privately. Rows can be 1-25. Set `everything` to include bot/system events.

**/clear**
Clears user-posted messages from the configured #rank-up channel while keeping the oldest user message (the quickguide) and all bot messages. It must be run in that configured channel.

**/discord-guess <player>**  
Shows the bot's best Discord member match for a tracked player, including a clickable mention.

**/discord-rank-check <player>**  
Checks an exact Discord match's rank role against the database.

**/discord-rank-audit**  
Runs a read-only exact-match rank role audit and attaches a CSV.

**/ungnomify**
Lists current Goblin and Short green guy roles with the player's current tracker rank.

**/discord-rank-set <user> <rank>**  
Replaces a Discord member's configured rank roles with the selected rank role.

**/discord-rank-review-post**  
Posts review cards for actionable Discord rank role mismatches.

**/lookup <player>**  
Shows a player summary: rank, stats, pets, Temple/WOM status, and latest sync.

**/rank-conditions**  
Shows the current tracker rank requirements.

**/update <player>**  
Prioritizes a player in the update queue for a faster sync.

**/set-pets <player> <count>**  
Sets a manual pet count override for a player.

### Temple / WOM Membership
**/temple-add <players>**  
Adds one or more players to TempleOSRS.  
Format: comma-separated, for example A, B, C.

**/temple-remove <players>**  
Removes one or more players from TempleOSRS.

**/wom-add <players>**  
Adds one or more players to Wise Old Man.

**/wom-remove <players>**  
Removes one or more players from Wise Old Man.

**/add <players>**  
Combo command: adds players to both TempleOSRS and Wise Old Man.

**/remove <players>**  
Combo command: removes players from both TempleOSRS and Wise Old Man.

**/wom-role-update <player> <rank>**  
Updates a player's Wise Old Man group role.

**/wom-cache-refresh**  
Refreshes the local Wise Old Man roster cache immediately.

### Review / Ignore Handling
**/requeue-review-card <player>**  
Forces review card reposting/recreation for a player when the review is still active.

**/unignore <player>**  
Removes ignore flags for a player in:
- WOM-only ignore
- WOM rank mismatch ignore

**/show-ignored**  
Shows all currently ignored players in:
- WOM-only
- WOM rank mismatch
""";

        await RespondAndAutoDeleteAsync(command, helpText, ephemeral: true);
    }

    private async Task HandleRankConditionsSlashCommandAsync(SocketSlashCommand command)
    {
        var sb = new StringBuilder();
        sb.AppendLine("**Recruit**: Default when no listed requirement is met.");
        foreach (var requirement in RankEvaluator.Requirements)
        {
            sb.AppendLine($"**{requirement.Rank}**: {FormatRankRequirement(requirement.Description)}");
        }

        var embed = new EmbedBuilder()
            .WithTitle("Current Rank Conditions")
            .WithDescription(sb.ToString())
            .WithColor(new Color(199, 163, 95))
            .WithFooter("Higher matching rank wins.")
            .WithTimestamp(DateTimeOffset.Now)
            .Build();

        await RespondAndAutoDeleteAsync(command, embed, ephemeral: true);
    }

    private static string FormatRankRequirement(string requirement) =>
        requirement.Replace(" OR ", " / ", StringComparison.Ordinal);

    private async Task LogSlashCommandAsync(SocketSlashCommand command, bool adminLocked, bool allowed)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
            var ownerId = await ResolveSlashCommandOwnerPlayerIdAsync(db, command, CancellationToken.None);
            if (!ownerId.HasValue)
            {
                logger.LogWarning("Unable to record slash command /{Command}; no valid player row exists for lifecycle ownership.", command.Data.Name);
                return;
            }

            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = ownerId.Value,
                EventType = "DISCORD_SLASH_COMMAND_USED",
                MetadataJson = JsonUtil.Serialize(new
                {
                    Command = command.Data.Name,
                    RequestedBy = command.User.Username,
                    RequestedByDiscordUserId = command.User.Id,
                    ChannelId = command.ChannelId,
                    GuildId = (command.User as SocketGuildUser)?.Guild.Id,
                    AdminLocked = adminLocked,
                    Allowed = allowed,
                    Options = DescribeSlashCommandOptions(command.Data.Options),
                    Source = "discord-slash"
                }),
                Status = "DONE",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record slash command /{Command}.", command.Data.Name);
        }
    }

    private async Task RespondAndAutoDeleteAsync(SocketSlashCommand command, string text, bool ephemeral = true)
    {
        var effectiveEphemeral = ephemeral;
        var response = await command.FollowupAsync(text: text, ephemeral: effectiveEphemeral);
        var messageDescription = BuildSlashTextCleanupDescription(command, text);
        if (effectiveEphemeral)
        {
            await ScheduleInteractionResponseDeleteAsync(command, messageDescription);
        }
        else
        {
            if (command.ChannelId.HasValue)
            {
                await ScheduleChannelResponseDeleteAsync(command.ChannelId.Value, response.Id, $"slash-{command.CommandName}-followup", messageDescription);
            }
            else
            {
                await ScheduleInteractionResponseDeleteAsync(command, messageDescription);
            }
        }
    }

    private async Task RespondAndAutoDeleteAsync(SocketSlashCommand command, Embed embed, bool ephemeral = true)
    {
        var effectiveEphemeral = ephemeral;
        var response = await command.FollowupAsync(embed: embed, ephemeral: effectiveEphemeral);
        var messageDescription = BuildSlashEmbedCleanupDescription(command, embed);
        if (effectiveEphemeral)
        {
            await ScheduleInteractionResponseDeleteAsync(command, messageDescription);
        }
        else
        {
            if (command.ChannelId.HasValue)
            {
                await ScheduleChannelResponseDeleteAsync(command.ChannelId.Value, response.Id, $"slash-{command.CommandName}-followup", messageDescription);
            }
            else
            {
                await ScheduleInteractionResponseDeleteAsync(command, messageDescription);
            }
        }
    }

    private static string BuildSlashTextCleanupDescription(SocketSlashCommand command, string text)
    {
        return BuildSlashCleanupDescription(command, text, $"slash-{command.CommandName}-followup");
    }

    private static string BuildSlashEmbedCleanupDescription(SocketSlashCommand command, IEmbed embed)
    {
        return BuildSlashCleanupDescription(command, BuildEmbedCleanupSummary(embed), $"slash-{command.CommandName}-followup");
    }

    private static string BuildInteractionCleanupDescription(SocketSlashCommand command, string? messageDescription = null)
    {
        return string.IsNullOrWhiteSpace(messageDescription)
            ? BuildSlashCleanupDescription(command, "interaction response cleanup", $"slash-{command.CommandName}-interaction-response")
            : NormalizeCleanupDescription(messageDescription, $"slash-{command.CommandName}-interaction-response");
    }

    private static string BuildSlashCleanupDescription(SocketSlashCommand command, string? summary, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(summary)
            ? fallback
            : $"/{command.CommandName}: {summary}";
        return NormalizeCleanupDescription(text, fallback);
    }

    private static IReadOnlyList<object> DescribeSlashCommandOptions(IReadOnlyCollection<SocketSlashCommandDataOption> options)
    {
        return options.Select(DescribeSlashCommandOption).ToArray();
    }

    private static object DescribeSlashCommandOption(SocketSlashCommandDataOption option)
    {
        object[] nested = option.Options is { Count: > 0 }
            ? option.Options.Select(DescribeSlashCommandOption).ToArray()
            : Array.Empty<object>();

        return new
        {
            option.Name,
            Value = TruncateOptionValue(option.Value?.ToString()),
            Options = nested
        };
    }

    private static string? TruncateOptionValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        return value.Length <= 160 ? value : value[..157] + "...";
    }

    private static string? BuildEmbedCleanupSummary(IEmbed? embed)
    {
        if (embed is null) return null;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(embed.Title)) parts.Add(embed.Title);
        if (!string.IsNullOrWhiteSpace(embed.Description)) parts.Add(embed.Description);
        return parts.Count == 0 ? null : string.Join(": ", parts);
    }

    private static string NormalizeCleanupDescription(string? value, string fallback)
    {
        var source = string.IsNullOrWhiteSpace(value) ? fallback : value;
        var normalized = string.Join(" ", source.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 240 ? normalized : normalized[..237] + "...";
    }

    private static string TruncateDetails(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "No response body.";
        var normalized = string.Join(" ", value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 480 ? normalized : normalized[..477] + "...";
    }

    private static string ComputeRenderFingerprint(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes);
    }

    private bool ShouldSkipMessagePatch(ulong messageId, string fingerprint, bool bypassCooldown = false)
    {
        if (!_messagePatchStateByMessageId.TryGetValue(messageId, out var current))
        {
            return false;
        }

        if (string.Equals(current.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return true;
        }

        if (!bypassCooldown && (DateTimeOffset.UtcNow - current.LastPatchedAtUtc) < MessagePatchMinInterval)
        {
            return true;
        }

        return false;
    }

    private void RecordMessagePatched(ulong messageId, string fingerprint)
    {
        _messagePatchStateByMessageId[messageId] = new MessagePatchState(fingerprint, DateTimeOffset.UtcNow);
    }

    private async Task<Discord.Rest.RestFollowupMessage?> RespondToComponentAsync(SocketMessageComponent component, string text, bool ephemeral = true)
    {
        try
        {
            var response = await component.FollowupAsync(text: text, ephemeral: ephemeral);
            if (ephemeral && response is not null)
            {
                await ScheduleInteractionFollowupDeleteAsync(component, response.Id, text);
            }
            return response;
        }
        catch
        {
            // no-op best effort
            return null;
        }
    }

    private sealed record MessagePatchState(string Fingerprint, DateTimeOffset LastPatchedAtUtc);

    private bool IsEphemeralSlashCommand(string commandName)
    {
        return string.Equals(commandName, "lookup", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(commandName, "update", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(commandName, "history", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(commandName, "clear", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(commandName, "help", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(commandName, "rank-conditions", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(commandName, "wom-cache-refresh", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(commandName, "show-ignored", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAdminLockedButton(string? prefix)
    {
        return prefix is not null &&
            (string.Equals(prefix, "promo", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(prefix, "missing", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(prefix, "wommissing", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(prefix, "womonly", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(prefix, "womrank", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(prefix, "templename", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(prefix, "merge", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(prefix, "drankreview", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAdminLockedSlashCommand(string commandName)
    {
        return string.Equals(commandName, "update", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "discord-guess", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "discord-rank-check", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "discord-rank-audit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "ungnomify", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "discord-rank-set", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "discord-rank-review-post", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "history", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "clear", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "set-pets", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "add", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "remove", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "temple-add", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "temple-remove", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "wom-add", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "wom-remove", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "wom-role-update", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "wom-cache-refresh", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "unignore", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "show-ignored", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "requeue-review-card", StringComparison.OrdinalIgnoreCase);
    }

    private bool HasDiscordAdminRole(SocketUser user)
    {
        if (_options.AdminRoleId <= 0)
        {
            logger.LogWarning("Discord admin role id is not configured; denying admin-locked interaction for {User}.", user.Username);
            return false;
        }

        return user is SocketGuildUser guildUser &&
            guildUser.Roles.Any(x => x.Id == _options.AdminRoleId);
    }

    private static async Task DenyComponentAsync(SocketMessageComponent component)
    {
        const string message = "You need the Swedes admin Discord role to use this action.";
        if (component.HasResponded)
        {
            await component.FollowupAsync(text: message, ephemeral: true);
            return;
        }

        await component.RespondAsync(text: message, ephemeral: true);
    }

    private static async Task DenySlashCommandAsync(SocketSlashCommand command)
    {
        const string message = "You need the Swedes admin Discord role to use this command.";
        if (command.HasResponded)
        {
            await command.FollowupAsync(text: message, ephemeral: true);
            return;
        }

        await command.RespondAsync(text: message, ephemeral: true);
    }

    private async Task SchedulePublicOriginalResponseDeleteAsync(SocketSlashCommand command, string reason)
    {
        try
        {
            if (!command.ChannelId.HasValue)
            {
                await ScheduleInteractionResponseDeleteAsync(command);
                return;
            }

            var original = await command.GetOriginalResponseAsync();
            var messageDescription = BuildSlashCleanupDescription(
                command,
                string.IsNullOrWhiteSpace(original.Content) ? BuildEmbedCleanupSummary(original.Embeds.FirstOrDefault()) : original.Content,
                reason);
            await ScheduleChannelResponseDeleteAsync(command.ChannelId.Value, original.Id, reason, messageDescription);
        }
        catch
        {
            await ScheduleInteractionResponseDeleteAsync(command);
        }
    }

    private string FormatSwedishTime(DateTimeOffset? value)
    {
        if (value is null) return "N/A";
        var local = TimeZoneInfo.ConvertTime(value.Value, _swedishTimeZone);
        return $"{local:yyyy-MM-dd HH:mm}";
    }

    private async Task<DiscordMemberGuessResult> GuessDiscordMemberForPlayerAsync(
        TrackerDbContext db,
        int playerId,
        string playerName,
        CancellationToken ct)
    {
        if (_client is null || _options.GuildId == 0)
        {
            return new DiscordMemberGuessResult([]);
        }

        var playerNames = await GetDiscordGuessPlayerNamesAsync(db, playerId, playerName, ct);
        return await GuessDiscordMemberForNamesAsync(playerNames, ct);
    }

    private async Task<DiscordMemberGuessResult> GuessDiscordMemberForNamesAsync(
        IReadOnlyList<string> playerNames,
        CancellationToken ct)
    {
        if (_client is null || _options.GuildId == 0 || playerNames.Count == 0)
        {
            return new DiscordMemberGuessResult([]);
        }

        var members = new List<DiscordMemberLookupCandidate>();
        members.AddRange(await GetCachedDiscordMembersAsync(ct));
        members.AddRange(await SearchDiscordMembersAsync(playerNames, ct));
        return DiscordMemberGuessing.Guess(playerNames, members);
    }

    private async Task<List<string>> GetDiscordGuessPlayerNamesAsync(
        TrackerDbContext db,
        int playerId,
        string playerName,
        CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddDiscordGuessPlayerName(names, playerName);

        var metadataRows = await db.LifecycleEvents
            .Where(x => x.PlayerId == playerId &&
                (x.EventType.Contains("MERGE") || x.EventType.Contains("RENAME")))
            .OrderByDescending(x => x.CreatedAt)
            .Take(25)
            .Select(x => x.MetadataJson)
            .ToListAsync(ct);

        foreach (var metadata in metadataRows)
        {
            AddDiscordGuessPlayerNamesFromMetadata(names, metadata);
        }

        return names.ToList();
    }

    private Task<IReadOnlyList<DiscordMemberLookupCandidate>> GetCachedDiscordMembersAsync(CancellationToken ct)
    {
        return RefreshDiscordMemberCacheAsync(forceRefresh: false, DiscordMemberDownloadTimeout, ct);
    }

    private Task<IReadOnlyList<DiscordMemberLookupCandidate>> ForceRefreshDiscordMemberCacheAsync(CancellationToken ct)
    {
        return RefreshDiscordMemberCacheAsync(forceRefresh: true, DiscordMemberWarmupDownloadTimeout, ct);
    }

    private async Task<IReadOnlyList<DiscordMemberLookupCandidate>> RefreshDiscordMemberCacheAsync(
        bool forceRefresh,
        TimeSpan downloadTimeout,
        CancellationToken ct)
    {
        if (!forceRefresh && _discordMemberCache is not null && DateTimeOffset.UtcNow < _discordMemberCacheValidUntil)
        {
            return _discordMemberCache;
        }

        if (!await _discordMemberCacheLock.WaitAsync(DiscordMemberCacheLockTimeout, ct))
        {
            logger.LogWarning("Timed out waiting for Discord member cache lock; using existing cache if available.");
            return _discordMemberCache ?? [];
        }

        try
        {
            if (!forceRefresh && _discordMemberCache is not null && DateTimeOffset.UtcNow < _discordMemberCacheValidUntil)
            {
                return _discordMemberCache;
            }

            var guild = _client?.GetGuild(_options.GuildId);
            if (guild is null)
            {
                return [];
            }

            var downloadedMembers = true;
            try
            {
                downloadedMembers = await WaitForDiscordTaskAsync(guild.DownloadUsersAsync(), downloadTimeout, ct);
                if (!downloadedMembers)
                {
                    logger.LogWarning("Timed out downloading Discord guild members after {TimeoutSeconds}s; using currently cached guild users.", downloadTimeout.TotalSeconds);
                }
            }
            catch (Exception ex)
            {
                downloadedMembers = false;
                logger.LogWarning(ex, "Failed to download Discord guild members for guessing.");
            }

            _discordMemberCache = guild.Users
                .Where(x => !x.IsBot)
                .Select(x => ToLookupCandidate(x, fromDiscordSearch: false))
                .ToList();
            _discordMemberCacheValidUntil = DateTimeOffset.UtcNow.Add(downloadedMembers ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(1));
            if (_discordMemberCache.Count == 0)
            {
                logger.LogWarning(
                    "Discord member cache refresh produced no non-bot users. DownloadedMembers={DownloadedMembers}; valid for {ValidSeconds}s.",
                    downloadedMembers,
                    (_discordMemberCacheValidUntil - DateTimeOffset.UtcNow).TotalSeconds);
            }
            else
            {
                logger.LogInformation(
                    "Discord member cache refreshed with {MemberCount} non-bot users. DownloadedMembers={DownloadedMembers}; valid until {ValidUntil:u}.",
                    _discordMemberCache.Count,
                    downloadedMembers,
                    _discordMemberCacheValidUntil);
            }

            return _discordMemberCache;
        }
        finally
        {
            _discordMemberCacheLock.Release();
        }
    }

    private async Task WarmDiscordMemberCacheAsync(CancellationToken ct)
    {
        try
        {
            var members = await ForceRefreshDiscordMemberCacheAsync(ct);
            logger.LogInformation("Discord member cache warmup completed with {MemberCount} non-bot users.", members.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Discord member cache warmup failed.");
        }
    }

    private async Task<IReadOnlyList<DiscordMemberLookupCandidate>> SearchDiscordMembersAsync(
        IReadOnlyList<string> playerNames,
        CancellationToken ct)
    {
        var guild = _client?.GetGuild(_options.GuildId);
        if (guild is null) return [];

        var members = new List<DiscordMemberLookupCandidate>();
        var searchTimer = Stopwatch.StartNew();
        foreach (var query in BuildDiscordMemberSearchQueries(playerNames))
        {
            ct.ThrowIfCancellationRequested();
            var remaining = DiscordMemberSearchTotalTimeout - searchTimer.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                logger.LogWarning(
                    "Stopped Discord member search after {TimeoutSeconds}s total timeout; collected {MemberCount} search candidates.",
                    DiscordMemberSearchTotalTimeout.TotalSeconds,
                    members.Count);
                break;
            }

            var queryTimeout = remaining < DiscordMemberSearchTimeout ? remaining : DiscordMemberSearchTimeout;
            try
            {
                var results = await WaitForDiscordTaskAsync(guild.SearchUsersAsync(query, 10), queryTimeout, ct);
                if (results is null)
                {
                    logger.LogWarning("Timed out searching Discord members for query {Query} after {TimeoutSeconds}s.", query, queryTimeout.TotalSeconds);
                    continue;
                }

                members.AddRange(results.Where(x => !x.IsBot).Select(x => ToLookupCandidate(x, fromDiscordSearch: true)));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to search Discord members for query {Query}.", query);
            }
        }

        return members;
    }

    private static DiscordMemberLookupCandidate ToLookupCandidate(IGuildUser user, bool fromDiscordSearch)
    {
        return new DiscordMemberLookupCandidate(
            user.Id,
            user.Username,
            user.GlobalName,
            user.Nickname,
            user.DisplayName,
            fromDiscordSearch);
    }

    private static IReadOnlyList<string> BuildDiscordMemberSearchQueries(IEnumerable<string> playerNames)
    {
        var queries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var playerName in playerNames)
        {
            if (string.IsNullOrWhiteSpace(playerName)) continue;
            var trimmed = playerName.Trim();
            if (trimmed.Length >= 2) queries.Add(trimmed);

            var compact = trimmed
                .Replace("_", "", StringComparison.Ordinal)
                .Replace("-", "", StringComparison.Ordinal)
                .Replace(" ", "", StringComparison.Ordinal);
            if (compact.Length >= 3) queries.Add(compact);
        }

        return queries.Take(12).ToList();
    }

    private static async Task<bool> WaitForDiscordTaskAsync(Task task, TimeSpan timeout, CancellationToken ct)
    {
        var delay = Task.Delay(timeout, ct);
        var completed = await Task.WhenAny(task, delay);
        if (completed == delay)
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }

        await task;
        return true;
    }

    private static async Task<T?> WaitForDiscordTaskAsync<T>(Task<T> task, TimeSpan timeout, CancellationToken ct)
    {
        var delay = Task.Delay(timeout, ct);
        var completed = await Task.WhenAny(task, delay);
        if (completed == delay)
        {
            ct.ThrowIfCancellationRequested();
            return default;
        }

        return await task;
    }

    private static void AddDiscordGuessPlayerNamesFromMetadata(HashSet<string> names, string metadataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

            AddDiscordGuessPlayerName(names, ReadStringProperty(doc.RootElement, "Username"));
            AddDiscordGuessPlayerName(names, ReadStringProperty(doc.RootElement, "Player"));
            AddDiscordGuessPlayerName(names, ReadStringProperty(doc.RootElement, "NewPlayer"));
            AddDiscordGuessPlayerName(names, ReadStringProperty(doc.RootElement, "PreviousPlayer"));
            AddDiscordGuessPlayerName(names, ReadStringProperty(doc.RootElement, "SuggestedPrevious"));
            AddDiscordGuessPlayerName(names, ReadStringProperty(doc.RootElement, "CanonicalPlayer"));

            if (doc.RootElement.TryGetProperty("CandidatePreviousPlayers", out var candidates) &&
                candidates.ValueKind == JsonValueKind.Array)
            {
                foreach (var candidate in candidates.EnumerateArray())
                {
                    if (candidate.ValueKind != JsonValueKind.Object) continue;
                    AddDiscordGuessPlayerName(names, ReadStringProperty(candidate, "PreviousPlayer"));
                    AddDiscordGuessPlayerName(names, ReadStringProperty(candidate, "Username"));
                    AddDiscordGuessPlayerName(names, ReadStringProperty(candidate, "Player"));
                }
            }
        }
        catch
        {
            // best-effort aliases only
        }
    }

    private static string? ReadStringProperty(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static void AddDiscordGuessPlayerName(HashSet<string> names, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        names.Add(NormalizeUsername(value));
    }

    private static Embed BuildPromotionEmbed(
        string playerName,
        string oldRank,
        string newRank,
        string womRank,
        string updateTarget,
        DiscordMemberGuessResult discordGuess,
        string statsSummary,
        string reason,
        string lastSynced)
    {
        var b = new EmbedBuilder()
            .WithTitle("Rank Candidate Detected")
            .WithColor(new Color(59, 130, 246))
            .AddField("Player", playerName, true)
            .AddField("Old Rank", oldRank, true)
            .AddField("New Eligible Rank", newRank, true)
            .AddField("WOM Rank", womRank, true)
            .AddField("Update Target", updateTarget, true);

        var discordGuessValue = FormatDiscordGuessForEmbed(discordGuess, out var discordGuessFieldName);
        if (!string.IsNullOrWhiteSpace(discordGuessValue))
        {
            b.AddField(discordGuessFieldName, discordGuessValue, false);
        }

        b.AddField("Stats Summary", statsSummary, false)
            .AddField("Reason", reason, false)
            .AddField("Last Synced (Swedish Time)", lastSynced, false);
        return b.Build();
    }

    private static string FormatDiscordGuessForEmbed(DiscordMemberGuessResult guess, out string fieldName)
    {
        fieldName = "Discord Match";
        var best = guess.Best;
        if (best is null) return "";

        var second = guess.Matches.Skip(1).FirstOrDefault();
        var showSingle =
            best.Strength == DiscordMemberMatchStrength.Exact ||
            (best.Strength == DiscordMemberMatchStrength.Strong && (second is null || second.Score < 85));

        if (showSingle)
        {
            var label = best.Strength == DiscordMemberMatchStrength.Exact ? "exact" : "best guess";
            return $"{best.Mention} ({label}; matched {best.MatchedField} `{EscapeInlineCode(best.MatchedValue)}`)";
        }

        fieldName = "Possible Discord Matches";
        return string.Join("\n", guess.Matches.Select(match =>
            $"{match.Mention} - {match.Score}% via {match.MatchedField} `{EscapeInlineCode(match.MatchedValue)}`"));
    }

    private static void AddDiscordGuessField(EmbedBuilder builder, string subject, DiscordMemberGuessResult guess)
    {
        var value = FormatDiscordGuessForEmbed(guess, out var baseFieldName);
        if (string.IsNullOrWhiteSpace(value)) return;

        builder.AddField($"{baseFieldName}: {subject}", value, false);
    }

    private static string FormatDiscordGuessForFingerprint(DiscordMemberGuessResult guess)
    {
        if (guess.Matches.Count == 0) return "none";
        return string.Join("|", guess.Matches.Select(x =>
            $"{x.UserId}:{x.Score}:{x.Strength}:{x.MatchedField}:{x.MatchedValue}:{x.PlayerAlias}"));
    }

    private static string EscapeInlineCode(string value) => value.Replace("`", "'", StringComparison.Ordinal);

    private static string ToPromotionUpdateTargetLabel(PromotionCandidateType candidateType)
    {
        return candidateType switch
        {
            PromotionCandidateType.wom_already_at_new_rank => "Database only",
            PromotionCandidateType.needs_wom_rank_update => "Ingame + database",
            _ => "Review needed"
        };
    }

    private static string BuildStatsSummary(double? ehb, double? ehp, int? collections, int pets)
    {
        var parts = new List<string>
        {
            $"EHB: {(ehb.HasValue ? ehb.Value.ToString("0.0", CultureInfo.InvariantCulture) : "N/A")}",
            $"EHP: {(ehp.HasValue ? ehp.Value.ToString("0.0", CultureInfo.InvariantCulture) : "N/A")}"
        };
        if (collections.HasValue) parts.Add($"Collections: {collections.Value}");
        if (pets > 0) parts.Add($"Pets: {pets}");
        return string.Join(" | ", parts);
    }

    private static Embed BuildHandledEmbed(IEmbed? source, string handledText, string action)
    {
        var color = action switch
        {
            "approve" => new Color(34, 197, 94),
            "dismiss" => new Color(239, 68, 68),
            "rename" => new Color(245, 158, 11),
            _ => new Color(100, 116, 139)
        };

        var builder = source is not null ? source.ToEmbedBuilder() : new EmbedBuilder().WithTitle("Rank Candidate");
        builder.WithColor(color);
        builder.WithFooter(handledText);
        builder.WithTimestamp(DateTimeOffset.Now);
        return builder.Build();
    }

    private static Embed BuildWomOnlyRequiredEmbed(string username, string womRole)
    {
        var role = string.IsNullOrWhiteSpace(womRole) ? "Unknown" : womRole.Trim();
        var description =
            "This player exists in WiseOldMan but is missing from both Temple and the tracker database.\n\n" +
            "Use **Add player to Temple** to start normal tracking, or **Ignore tracking this player** to suppress future alerts until unignored.";

        return new EmbedBuilder()
            .WithTitle("WiseOldMan Only Player Detected")
            .WithColor(new Color(234, 88, 12))
            .WithDescription(description)
            .AddField("Player", username, true)
            .AddField("WiseOldMan Rank", role, true)
            .WithTimestamp(DateTimeOffset.Now)
            .Build();
    }

    private static Embed BuildTempleNameChangeNeededEmbed(
        string previousUsername,
        string newUsername,
        string rank,
        string womRole,
        DiscordMemberGuessResult previousDiscordGuess,
        DiscordMemberGuessResult newDiscordGuess)
    {
        var description =
            $"Previous name: `{previousUsername}`\n" +
            $"New name: `{newUsername}`\n\n" +
            "Next step: manually confirm/update the name change on TempleOSRS so the new profile receives the old Temple history.\n\n" +
            "Do not remove the player from DB or add/remove plain group membership as the primary fix.";

        var builder = new EmbedBuilder()
            .WithTitle("Temple Name Change Needed")
            .WithColor(new Color(234, 179, 8))
            .WithDescription(description)
            .AddField("Previous name", previousUsername, true)
            .AddField("New name", newUsername, true)
            .AddField("Rank signal", $"{rank} / WOM {womRole}", true);
        AddDiscordGuessField(builder, "Previous name", previousDiscordGuess);
        AddDiscordGuessField(builder, "New name", newDiscordGuess);
        return builder
            .WithTimestamp(DateTimeOffset.Now)
            .Build();
    }

    private static MessageComponent BuildTempleNameChangeNeededComponents(int requiredEventId)
    {
        return new ComponentBuilder()
            .WithButton("Confirm", $"templename:confirm:{requiredEventId}", ButtonStyle.Success)
            .WithButton("Decline", $"templename:decline:{requiredEventId}", ButtonStyle.Danger)
            .Build();
    }

    private static MessageComponent BuildWomOnlyRequiredComponents(int requiredEventId)
    {
        return new ComponentBuilder()
            .WithButton("Add player to Temple", $"womonly:add:{requiredEventId}", ButtonStyle.Success)
            .WithButton("Ignore tracking this player", $"womonly:ignore:{requiredEventId}", ButtonStyle.Danger)
            .Build();
    }

    private static Embed BuildWomRankMismatchEmbed(
        string playerName,
        string expectedRank,
        string actualWomRole,
        string direction,
        DiscordMemberGuessResult discordGuess)
    {
        var directionText = direction switch
        {
            "higher" => "WOM/in-game rank appears ahead of the database rank",
            "lower" => "WOM/in-game rank appears behind the database rank",
            _ => "WOM rank differs from the database rank"
        };
        var rankMismatchLabel = direction switch
        {
            "higher" => "higher",
            "lower" => "lower",
            _ => "different"
        };
        var description =
            $"**Ingame (WOM)** is {rankMismatchLabel} than **database**.\n\n" +
            $"Select \"Sync to WOM ({actualWomRole})\" to set both to {actualWomRole}, or \"Sync to database ({expectedRank})\" to set both to {expectedRank}.\n\n" +
            "Use **Dismiss** to review later, or **Ignore** to permanently allow this mismatch for this player.";

        var builder = new EmbedBuilder()
            .WithTitle("WiseOldMan Rank Mismatch")
            .WithColor(direction == "higher" ? new Color(239, 68, 68) : new Color(245, 158, 11))
            .WithDescription(description)
            .AddField("Player", playerName, true)
            .AddField("Database Rank", expectedRank, true)
            .AddField("WiseOldMan Rank", actualWomRole, true)
            .AddField("Mismatch", directionText, false);
        AddDiscordGuessField(builder, "Player", discordGuess);
        return builder
            .WithTimestamp(DateTimeOffset.Now)
            .Build();
    }

    private static MessageComponent BuildWomRankMismatchComponents(int playerId, int requiredEventId, string expectedRank, string actualWomRole)
    {
        var womRankLabel = string.IsNullOrWhiteSpace(actualWomRole) ? "Unknown" : actualWomRole.Trim();
        var dbRankLabel = string.IsNullOrWhiteSpace(expectedRank) ? "Unknown" : expectedRank.Trim();
        return new ComponentBuilder()
            .WithButton($"Sync to WOM ({womRankLabel})", $"womrank:sync_wom_to_db:{playerId}:{requiredEventId}", ButtonStyle.Primary)
            .WithButton($"Sync to database ({dbRankLabel})", $"womrank:sync_db_to_wom:{playerId}:{requiredEventId}", ButtonStyle.Primary)
            .WithButton("Dismiss", $"womrank:dismiss:{playerId}:{requiredEventId}", ButtonStyle.Secondary)
            .WithButton("Ignore tracking for this player", $"womrank:ignore:{playerId}:{requiredEventId}", ButtonStyle.Danger)
            .Build();
    }

    private sealed record WomRoleUpdateResult(
        bool Success,
        int HttpStatus,
        string Details,
        string? UpdatedRole,
        int? WomPlayerId,
        string? DisplayName);

    private async Task<WomRoleUpdateResult> ExecuteWomRoleUpdateForPlayerAsync(string playerName, string role)
    {
        var womGroupId = configuration.GetValue<int?>("WiseOldMan:GroupId") ?? 0;
        var womVerificationCode = configuration["WiseOldMan:VerificationCode"] ?? "";
        if (womGroupId <= 0 || string.IsNullOrWhiteSpace(womVerificationCode))
        {
            return new WomRoleUpdateResult(false, 0, "WiseOldMan settings are missing (`WiseOldMan:GroupId` or `WiseOldMan:VerificationCode`).", null, null, null);
        }

        var client = httpClientFactory.CreateClient();
        var updateBody = JsonSerializer.Serialize(new
        {
            verificationCode = womVerificationCode,
            username = playerName,
            role = role.ToLowerInvariant()
        });
        var updateReq = new HttpRequestMessage(HttpMethod.Put, $"https://api.wiseoldman.net/v2/groups/{womGroupId}/role")
        {
            Content = new StringContent(updateBody, Encoding.UTF8, "application/json")
        };
        var response = await client.SendAsync(updateReq);
        var responseText = await response.Content.ReadAsStringAsync();

        var details = response.IsSuccessStatusCode
            ? "Role update accepted by WiseOldMan."
            : $"HTTP {(int)response.StatusCode}: {TruncateDetails(responseText)}";
        var updatedRole = role;
        int? womPlayerId = null;
        string? displayName = null;
        if (response.IsSuccessStatusCode &&
            TryReadWomRoleUpdateResponse(responseText, out var parsedUpdatedRole, out womPlayerId, out displayName))
        {
            updatedRole = parsedUpdatedRole;
        }

        return new WomRoleUpdateResult(
            response.IsSuccessStatusCode,
            (int)response.StatusCode,
            response.IsSuccessStatusCode ? details : TruncateDetails(responseText),
            response.IsSuccessStatusCode ? updatedRole : null,
            womPlayerId,
            displayName);
    }

    private static string GetWomRankMismatchDirection(string expectedRank, string actualWomRole)
    {
        var expected = RankRules.RankOrder(expectedRank);
        var actual = RankRules.RankOrder(actualWomRole);
        if (actual > expected) return "higher";
        if (actual < expected) return "lower";
        return "different";
    }

    private static TimeZoneInfo ResolveSwedishTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"); } catch { }
        try { return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); } catch { }
        return TimeZoneInfo.Local;
    }

    private static bool TryParseTempleMembershipResponse(string json, out int processed, out int oldCount, out int newCount)
    {
        processed = 0;
        oldCount = 0;
        newCount = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return false;

            if (data.TryGetProperty("added_names", out var added))
            {
                processed = added.GetInt32();
            }
            else if (data.TryGetProperty("removed_names", out var removed))
            {
                processed = removed.GetInt32();
            }
            else
            {
                return false;
            }

            if (!data.TryGetProperty("old_member_count", out var oldProp)) return false;
            if (!data.TryGetProperty("new_member_count", out var newProp)) return false;
            oldCount = oldProp.GetInt32();
            newCount = newProp.GetInt32();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<Embed> ExecuteTempleAddAsync(List<string> players)
    {
        var templeGroupId = configuration.GetValue<int?>("TempleOsrs:GroupId") ?? 0;
        var templeApiKey = configuration["TempleOsrs:ApiKey"] ?? "";
        if (templeGroupId <= 0 || string.IsNullOrWhiteSpace(templeApiKey))
        {
            return BuildTempleResultEmbed(
                title: "TempleOSRS Add Failed",
                success: false,
                groupId: templeGroupId,
                players: players,
                details: "TempleOSRS settings are missing (`TempleOsrs:GroupId` or `TempleOsrs:ApiKey`).");
        }

        var client = httpClientFactory.CreateClient();
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = templeGroupId.ToString(CultureInfo.InvariantCulture),
            ["key"] = templeApiKey,
            ["players"] = string.Join(",", players)
        });
        var response = await client.PostAsync("https://templeosrs.com/api/add_group_member.php", body);
        var responseText = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            return BuildTempleResultEmbed(
                title: "TempleOSRS Add Failed",
                success: false,
                groupId: templeGroupId,
                players: players,
                details: $"HTTP {(int)response.StatusCode}: {responseText}");
        }

        var details = "Request accepted by TempleOSRS.";
        if (TryParseTempleMembershipResponse(responseText, out var processed, out var oldCount, out var newCount))
        {
            details = $"Processed by Temple: {processed}\nMembers: {oldCount} -> {newCount}";
        }

        return BuildTempleResultEmbed(
            title: "TempleOSRS Add Completed",
            success: true,
            groupId: templeGroupId,
            players: players,
            details: details);
    }

    private async Task<Embed> ExecuteWomAddAsync(List<string> players)
    {
        var womGroupId = configuration.GetValue<int?>("WiseOldMan:GroupId") ?? 0;
        var womVerificationCode = configuration["WiseOldMan:VerificationCode"] ?? "";
        if (womGroupId <= 0 || string.IsNullOrWhiteSpace(womVerificationCode))
        {
            return BuildWomResultEmbed(
                title: "WiseOldMan Add Failed",
                success: false,
                groupId: womGroupId,
                players: players,
                details: "WiseOldMan settings are missing (`WiseOldMan:GroupId` or `WiseOldMan:VerificationCode`).");
        }

        var client = httpClientFactory.CreateClient();
        var addBody = JsonSerializer.Serialize(new
        {
            verificationCode = womVerificationCode,
            members = players.Select(p => new { username = p, role = "member" }).ToArray()
        });
        var addReq = new HttpRequestMessage(HttpMethod.Post, $"https://api.wiseoldman.net/v2/groups/{womGroupId}/members")
        {
            Content = new StringContent(addBody, Encoding.UTF8, "application/json")
        };
        var response = await client.SendAsync(addReq);
        var responseText = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            return BuildWomResultEmbed(
                title: "WiseOldMan Add Failed",
                success: false,
                groupId: womGroupId,
                players: players,
                details: $"HTTP {(int)response.StatusCode}: {responseText}");
        }

        await InvalidateWiseOldManCacheAsync("wom-add-command");
        return BuildWomResultEmbed(
            title: "WiseOldMan Add Completed",
            success: true,
            groupId: womGroupId,
            players: players,
            details: "Request accepted by WiseOldMan.");
    }

    private async Task<Embed> ExecuteTempleRemoveAsync(List<string> players)
    {
        var templeGroupId = configuration.GetValue<int?>("TempleOsrs:GroupId") ?? 0;
        var templeApiKey = configuration["TempleOsrs:ApiKey"] ?? "";
        if (templeGroupId <= 0 || string.IsNullOrWhiteSpace(templeApiKey))
        {
            return BuildTempleResultEmbed(
                title: "TempleOSRS Remove Failed",
                success: false,
                groupId: templeGroupId,
                players: players,
                details: "TempleOSRS settings are missing (`TempleOsrs:GroupId` or `TempleOsrs:ApiKey`).");
        }

        var client = httpClientFactory.CreateClient();
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = templeGroupId.ToString(CultureInfo.InvariantCulture),
            ["key"] = templeApiKey,
            ["players"] = string.Join(",", players)
        });
        var response = await client.PostAsync("https://templeosrs.com/api/remove_group_member.php", body);
        var responseText = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            return BuildTempleResultEmbed(
                title: "TempleOSRS Remove Failed",
                success: false,
                groupId: templeGroupId,
                players: players,
                details: $"HTTP {(int)response.StatusCode}: {responseText}");
        }

        var details = "Request accepted by TempleOSRS.";
        if (TryParseTempleMembershipResponse(responseText, out var processed, out var oldCount, out var newCount))
        {
            details = $"Processed by Temple: {processed}\nMembers: {oldCount} -> {newCount}";
        }

        return BuildTempleResultEmbed(
            title: "TempleOSRS Remove Completed",
            success: true,
            groupId: templeGroupId,
            players: players,
            details: details);
    }

    private async Task<Embed> ExecuteWomRemoveAsync(List<string> players)
    {
        var womGroupId = configuration.GetValue<int?>("WiseOldMan:GroupId") ?? 0;
        var womVerificationCode = configuration["WiseOldMan:VerificationCode"] ?? "";
        if (womGroupId <= 0 || string.IsNullOrWhiteSpace(womVerificationCode))
        {
            return BuildWomResultEmbed(
                title: "WiseOldMan Remove Failed",
                success: false,
                groupId: womGroupId,
                players: players,
                details: "WiseOldMan settings are missing (`WiseOldMan:GroupId` or `WiseOldMan:VerificationCode`).");
        }

        var client = httpClientFactory.CreateClient();
        var removeBody = JsonSerializer.Serialize(new
        {
            verificationCode = womVerificationCode,
            members = players.ToArray()
        });
        var removeReq = new HttpRequestMessage(HttpMethod.Delete, $"https://api.wiseoldman.net/v2/groups/{womGroupId}/members")
        {
            Content = new StringContent(removeBody, Encoding.UTF8, "application/json")
        };
        var response = await client.SendAsync(removeReq);
        var responseText = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            return BuildWomResultEmbed(
                title: "WiseOldMan Remove Failed",
                success: false,
                groupId: womGroupId,
                players: players,
                details: $"HTTP {(int)response.StatusCode}: {responseText}");
        }

        await InvalidateWiseOldManCacheAsync("wom-remove-command");
        return BuildWomResultEmbed(
            title: "WiseOldMan Remove Completed",
            success: true,
            groupId: womGroupId,
            players: players,
            details: "Request accepted by WiseOldMan.");
    }

    private async Task<Embed> ExecuteWomRoleUpdateAsync(SocketSlashCommand command, string playerName, string role)
    {
        var womGroupId = configuration.GetValue<int?>("WiseOldMan:GroupId") ?? 0;
        var womVerificationCode = configuration["WiseOldMan:VerificationCode"] ?? "";
        if (womGroupId <= 0 || string.IsNullOrWhiteSpace(womVerificationCode))
        {
            await LogWomRoleUpdateAppliedAsync(command, playerName, role, false, null, null, null, null, "WiseOldMan settings are missing.");
            return BuildWomRoleUpdateEmbed(
                title: "WiseOldMan Role Update Failed",
                success: false,
                groupId: womGroupId,
                playerName: playerName,
                requestedRole: role,
                updatedRole: null,
                womPlayerId: null,
                displayName: null,
                details: "WiseOldMan settings are missing (`WiseOldMan:GroupId` or `WiseOldMan:VerificationCode`).");
        }

        var client = httpClientFactory.CreateClient();
        var updateBody = JsonSerializer.Serialize(new
        {
            verificationCode = womVerificationCode,
            username = playerName,
            role = role.ToLowerInvariant()
        });
        var updateReq = new HttpRequestMessage(HttpMethod.Put, $"https://api.wiseoldman.net/v2/groups/{womGroupId}/role")
        {
            Content = new StringContent(updateBody, Encoding.UTF8, "application/json")
        };
        var response = await client.SendAsync(updateReq);
        var responseText = await response.Content.ReadAsStringAsync();
        var responseDetails = response.IsSuccessStatusCode
            ? "Role update accepted by WiseOldMan."
            : $"HTTP {(int)response.StatusCode}: {TruncateDetails(responseText)}";

        var updatedRole = role;
        int? womPlayerId = null;
        string? displayName = null;
        if (response.IsSuccessStatusCode)
        {
            if (TryReadWomRoleUpdateResponse(responseText, out var parsedUpdatedRole, out womPlayerId, out displayName))
            {
                updatedRole = parsedUpdatedRole;
            }
            await InvalidateWiseOldManCacheAsync("wom-role-update-command");
        }

        await LogWomRoleUpdateAppliedAsync(
            command,
            playerName,
            role,
            response.IsSuccessStatusCode,
            (int)response.StatusCode,
            updatedRole,
            womPlayerId,
            displayName,
            response.IsSuccessStatusCode ? "Role update accepted by WiseOldMan." : TruncateDetails(responseText));

        return BuildWomRoleUpdateEmbed(
            title: response.IsSuccessStatusCode ? "WiseOldMan Role Update Completed" : "WiseOldMan Role Update Failed",
            success: response.IsSuccessStatusCode,
            groupId: womGroupId,
            playerName: playerName,
            requestedRole: role,
            updatedRole: response.IsSuccessStatusCode ? updatedRole : null,
            womPlayerId: womPlayerId,
            displayName: displayName,
            details: responseDetails);
    }

    private async Task LogWomRoleUpdateAppliedAsync(
        SocketSlashCommand command,
        string playerName,
        string requestedRole,
        bool success,
        int? httpStatus,
        string? updatedRole,
        int? womPlayerId,
        string? displayName,
        string? details)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
            var playerId = await db.Players
                .Where(x => x.Username.ToLower() == playerName.ToLower())
                .Select(x => (int?)x.Id)
                .FirstOrDefaultAsync();
            playerId ??= await ResolveLifecycleOwnerPlayerIdAsync(db, 0, CancellationToken.None);
            if (!playerId.HasValue) return;

            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = playerId.Value,
                EventType = "WOM_ROLE_UPDATE_APPLIED",
                MetadataJson = JsonUtil.Serialize(new
                {
                    Player = playerName,
                    RequestedRole = requestedRole,
                    UpdatedRole = updatedRole,
                    Success = success,
                    HttpStatus = httpStatus,
                    RequestedBy = command.User.Username,
                    RequestedByDiscordUserId = command.User.Id,
                    ChannelId = command.ChannelId,
                    GuildId = (command.User as SocketGuildUser)?.Guild.Id,
                    WiseOldManPlayerId = womPlayerId,
                    WiseOldManDisplayName = displayName,
                    Details = details,
                    Source = "discord-slash-wom-role-update"
                }),
                Status = "DONE",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record WiseOldMan role update for {Player}.", playerName);
        }
    }

    private async Task HandleWomCacheRefreshSlashCommandAsync(SocketSlashCommand command)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var wiseOldManClient = scope.ServiceProvider.GetRequiredService<IWiseOldManClient>();
            await wiseOldManClient.InvalidateCacheAsync(CancellationToken.None);
            var roles = await wiseOldManClient.GetMemberRolesAsync(CancellationToken.None);

            logger.LogInformation(
                "Wise Old Man roster cache refreshed by Discord slash command /{Command} from {User} ({UserId}); loaded {MemberCount} members.",
                command.Data.Name,
                command.User.Username,
                command.User.Id,
                roles.Count);

            await RespondAndAutoDeleteAsync(
                command,
                $"Wise Old Man roster cache refreshed. Loaded {roles.Count.ToString(CultureInfo.InvariantCulture)} member role(s).",
                ephemeral: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling /wom-cache-refresh.");
            await RespondAndAutoDeleteAsync(
                command,
                "Failed to refresh the Wise Old Man roster cache. Check worker logs for details.",
                ephemeral: true);
        }
    }

    private static bool TryReadWomRoleUpdateResponse(string json, out string updatedRole, out int? womPlayerId, out string? displayName)
    {
        updatedRole = "";
        womPlayerId = null;
        displayName = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("role", out var roleProp))
            {
                updatedRole = roleProp.GetString() ?? "";
            }
            if (doc.RootElement.TryGetProperty("playerId", out var playerIdProp) && playerIdProp.TryGetInt32(out var parsedPlayerId))
            {
                womPlayerId = parsedPlayerId;
            }
            if (doc.RootElement.TryGetProperty("player", out var playerProp) && playerProp.ValueKind == JsonValueKind.Object)
            {
                if (playerProp.TryGetProperty("displayName", out var displayNameProp))
                {
                    displayName = displayNameProp.GetString();
                }
                if (string.IsNullOrWhiteSpace(displayName) && playerProp.TryGetProperty("username", out var usernameProp))
                {
                    displayName = usernameProp.GetString();
                }
            }
            return !string.IsNullOrWhiteSpace(updatedRole);
        }
        catch
        {
            return false;
        }
    }

    private async Task<TempleRosterLookupResult> TryGetTempleGroupUsernamesAsync(int groupId, CancellationToken ct)
    {
        if (groupId <= 0)
        {
            return new TempleRosterLookupResult(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        try
        {
            var client = httpClientFactory.CreateClient();
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await client.GetAsync(
                $"https://templeosrs.com/api/groupmembers.php?id={groupId}",
                requestTimeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("Temple roster lookup returned HTTP {StatusCode} for group {GroupId}.", response.StatusCode, groupId);
                return new TempleRosterLookupResult(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }

            var json = await response.Content.ReadAsStringAsync(requestTimeout.Token);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                logger.LogDebug("Temple roster lookup returned a non-array payload for group {GroupId}.", groupId);
                return new TempleRosterLookupResult(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }

            var usernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String) continue;
                var username = NormalizeUsername(element.GetString() ?? "");
                if (!string.IsNullOrWhiteSpace(username)) usernames.Add(username);
            }

            return new TempleRosterLookupResult(true, usernames);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Temple roster lookup failed for group {GroupId}; rename reconciliation will remain pending.", groupId);
            return new TempleRosterLookupResult(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private async Task<bool> IsPlayerInTempleGroupAsync(string username, int groupId)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            var json = await client.GetStringAsync($"https://templeosrs.com/api/groupmembers.php?id={groupId}");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (string.Equals(el.GetString(), username, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }

    private async Task<bool> IsPlayerInWiseOldManGroupAsync(string username, int groupId)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            var csv = await client.GetStringAsync($"https://api.wiseoldman.net/v2/groups/{groupId}/csv");
            var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length <= 1) return false;
            var normalizedUsername = NormalizeUsername(username);
            for (var i = 1; i < lines.Length; i++)
            {
                var firstField = NormalizeUsername(lines[i].Split(',', 2)[0].Trim().Trim('"'));
                if (string.Equals(firstField, normalizedUsername, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }

    private static Embed BuildTempleResultEmbed(string title, bool success, int groupId, List<string> players, string details)
    {
        var groupName = groupId == 449 ? "Swedes" : $"Group {groupId}";
        var color = success ? new Color(34, 197, 94) : new Color(239, 68, 68);
        return new EmbedBuilder()
            .WithTitle(title)
            .WithColor(color)
            .AddField("Group", $"{groupName} ({groupId})", true)
            .AddField("Players", string.Join(", ", players.Select(p => $"`{p}`")), false)
            .AddField("Details", details, false)
            .WithTimestamp(DateTimeOffset.Now)
            .Build();
    }

    private static Embed BuildWomResultEmbed(string title, bool success, int groupId, List<string> players, string details)
    {
        var groupName = groupId == 7173 ? "Swedes" : $"Group {groupId}";
        var color = success ? new Color(34, 197, 94) : new Color(239, 68, 68);
        return new EmbedBuilder()
            .WithTitle(title)
            .WithColor(color)
            .AddField("Group", $"{groupName} ({groupId})", true)
            .AddField("Players", players.Count > 0 ? string.Join(", ", players.Select(p => $"`{p}`")) : "N/A", false)
            .AddField("Details", details, false)
            .WithTimestamp(DateTimeOffset.Now)
            .Build();
    }

    private static Embed BuildWomRoleUpdateEmbed(
        string title,
        bool success,
        int groupId,
        string playerName,
        string requestedRole,
        string? updatedRole,
        int? womPlayerId,
        string? displayName,
        string details)
    {
        var groupName = groupId == 7173 ? "Swedes" : $"Group {groupId}";
        var builder = new EmbedBuilder()
            .WithTitle(title)
            .WithColor(success ? new Color(34, 197, 94) : new Color(239, 68, 68))
            .AddField("Group", $"{groupName} ({groupId})", true)
            .AddField("Player", string.IsNullOrWhiteSpace(playerName) ? "N/A" : $"`{playerName}`", true)
            .AddField("Requested Role", FormatWomRoleLabel(requestedRole), true)
            .AddField("Updated Role", string.IsNullOrWhiteSpace(updatedRole) ? "N/A" : FormatWomRoleLabel(updatedRole), true)
            .AddField("WiseOldMan Player", BuildWomPlayerSummary(womPlayerId, displayName), true)
            .AddField("Details", details, false)
            .WithTimestamp(DateTimeOffset.Now);

        return builder.Build();
    }

    private static string BuildWomPlayerSummary(int? womPlayerId, string? displayName)
    {
        if (womPlayerId.HasValue && !string.IsNullOrWhiteSpace(displayName))
        {
            return $"{displayName} (#{womPlayerId.Value})";
        }
        if (womPlayerId.HasValue) return $"#{womPlayerId.Value}";
        return string.IsNullOrWhiteSpace(displayName) ? "N/A" : displayName;
    }

    private static bool IsAllowedWomRole(string role) =>
        WomRoleChoices.Any(x => string.Equals(x.Value, role, StringComparison.OrdinalIgnoreCase));

    private static string FormatWomRoleLabel(string role)
    {
        var choice = WomRoleChoices.FirstOrDefault(x => string.Equals(x.Value, role, StringComparison.OrdinalIgnoreCase));
        return choice?.Label ?? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(role.Replace('_', ' ').ToLowerInvariant());
    }

    private static List<string> ParsePlayers(SocketSlashCommand command)
    {
        var rawPlayers = command.Data.Options.FirstOrDefault(x => x.Name == "players")?.Value?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(rawPlayers)) return [];
        return rawPlayers.Split(',')
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeUsername(string input) =>
        UsernameRules.NormalizeUsername(input);

    private async Task InvalidateWiseOldManCacheAsync(string reason)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var wiseOldManClient = scope.ServiceProvider.GetRequiredService<IWiseOldManClient>();
            await wiseOldManClient.InvalidateCacheAsync(CancellationToken.None);
            logger.LogInformation("Invalidated Wise Old Man roster cache after {Reason}.", reason);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to invalidate Wise Old Man roster cache after {Reason}.", reason);
        }
    }

    private async Task<bool> AddPlayerToTempleAsync(string username)
    {
        try
        {
            var groupId = configuration.GetValue<int?>("TempleOsrs:GroupId") ?? 449;
            var apiKey = configuration["TempleOsrs:ApiKey"] ?? "";
            if (string.IsNullOrWhiteSpace(apiKey)) return false;
            var client = httpClientFactory.CreateClient();
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["id"] = groupId.ToString(CultureInfo.InvariantCulture),
                ["key"] = apiKey,
                ["players"] = username
            });
            var response = await client.PostAsync("https://templeosrs.com/api/add_group_member.php", body);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private async Task<bool> AddPlayerToWomAsync(string username)
    {
        try
        {
            var groupId = configuration.GetValue<int?>("WiseOldMan:GroupId") ?? 0;
            var verificationCode = configuration["WiseOldMan:VerificationCode"] ?? "";
            if (groupId <= 0 || string.IsNullOrWhiteSpace(verificationCode)) return false;
            var client = httpClientFactory.CreateClient();
            var addBody = JsonSerializer.Serialize(new
            {
                verificationCode,
                members = new[] { new { username, role = "member" } }
            });
            var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.wiseoldman.net/v2/groups/{groupId}/members")
            {
                Content = new StringContent(addBody, Encoding.UTF8, "application/json")
            };
            var response = await client.SendAsync(req);
            if (response.IsSuccessStatusCode)
            {
                await InvalidateWiseOldManCacheAsync("wom-add-helper");
                return true;
            }
            return false;
        }
        catch { return false; }
    }

    private async Task<bool> RemovePlayerFromWomAsync(string username)
    {
        try
        {
            var groupId = configuration.GetValue<int?>("WiseOldMan:GroupId") ?? 0;
            var verificationCode = configuration["WiseOldMan:VerificationCode"] ?? "";
            if (groupId <= 0 || string.IsNullOrWhiteSpace(verificationCode)) return true;
            var client = httpClientFactory.CreateClient();
            var removeBody = JsonSerializer.Serialize(new
            {
                verificationCode,
                members = new[] { username }
            });
            var req = new HttpRequestMessage(HttpMethod.Delete, $"https://api.wiseoldman.net/v2/groups/{groupId}/members")
            {
                Content = new StringContent(removeBody, Encoding.UTF8, "application/json")
            };
            var response = await client.SendAsync(req);
            if (response.IsSuccessStatusCode)
            {
                await InvalidateWiseOldManCacheAsync("wom-remove-helper");
                return true;
            }
            return false;
        }
        catch { return false; }
    }

    private async Task<bool> RemovePlayerFromTempleAsync(string username)
    {
        try
        {
            var groupId = configuration.GetValue<int?>("TempleOsrs:GroupId") ?? 449;
            var apiKey = configuration["TempleOsrs:ApiKey"] ?? "";
            if (string.IsNullOrWhiteSpace(apiKey)) return false;
            var client = httpClientFactory.CreateClient();
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["id"] = groupId.ToString(CultureInfo.InvariantCulture),
                ["key"] = apiKey,
                ["players"] = username
            });
            var response = await client.PostAsync("https://templeosrs.com/api/remove_group_member.php", body);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private async Task<bool> ReevaluatePlayerForManualPetsAsync(TrackerDbContext db, IWiseOldManClient wiseOldManClient, Player player)
    {
        var latest = player.Snapshots
            .OrderByDescending(x => x.Timestamp)
            .FirstOrDefault();

        if (latest is null)
        {
            return false;
        }

        var snapshot = new PlayerSnapshot
        {
            PlayerId = player.Id,
            Timestamp = DateTimeOffset.UtcNow,
            TotalLevel = latest.TotalLevel,
            Ehb = latest.Ehb,
            Ehp = latest.Ehp,
            Collections = latest.Collections,
            PetCount = player.ManualPetOverride ?? player.StoredPetCount
        };
        if (!snapshot.HasSameStats(latest))
        {
            db.PlayerSnapshots.Add(snapshot);
        }

        var rankResult = RankEvaluator.Evaluate(snapshot);
        player.EligibleRank = rankResult.Rank;

        var womRole = await wiseOldManClient.GetMemberRoleAsync(player.Username, CancellationToken.None);
        var isRankUpgradeExcludedRole = RankRules.IsSpecialWomRole(womRole ?? "");
        if (isRankUpgradeExcludedRole)
        {
            var pendingForExcludedRole = await db.PromotionCandidates
                .Where(x => x.PlayerId == player.Id && x.Status == PromotionStatus.PENDING)
                .ToListAsync();
            if (pendingForExcludedRole.Count > 0)
            {
                db.PromotionCandidates.RemoveRange(pendingForExcludedRole);
            }
            return true;
        }

        if (RankRules.RankOrder(player.EligibleRank) <= RankRules.RankOrder(player.CurrentRank))
        {
            return false;
        }

        var exists = await db.PromotionCandidates.AnyAsync(x =>
            x.PlayerId == player.Id &&
            x.Status == PromotionStatus.PENDING &&
            x.NewRank == player.EligibleRank);
        if (exists) return false;

        db.PromotionCandidates.Add(new PromotionCandidate
        {
            PlayerId = player.Id,
            OldRank = player.CurrentRank,
            NewRank = player.EligibleRank,
            Reason = rankResult.Explanation,
            Status = PromotionStatus.PENDING,
            CreatedAt = DateTimeOffset.UtcNow
        });
        return false;
    }

}

public class DiscordBotOptions
{
    public bool Enabled { get; set; }
    public string Token { get; set; } = "";
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong PetHiscoresChannelId { get; set; }
    public ulong AdminRoleId { get; set; }
    public Dictionary<string, string> RankRoleIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record DiscordRankAuditPlayer(
    int Id,
    string Username,
    string CurrentRank,
    PlayerStatus Status,
    bool WomRankMismatchIgnored = false);

public sealed record DiscordRankRole(string Rank, ulong RoleId, string RoleName);

public sealed record DiscordRankRoleUpdateResult(IReadOnlyList<DiscordRankRole> RemovedRoles, bool AddedRole);

public sealed record DiscordRankRoleConfiguration(
    IReadOnlyDictionary<string, DiscordRankRole> RolesByRank,
    IReadOnlyDictionary<ulong, string> RankByRoleId,
    IReadOnlyList<string> MissingRanks);

public sealed record DiscordRankAuditResult(
    int PlayerId,
    string Username,
    PlayerStatus PlayerStatus,
    bool WomRankMismatchIgnored,
    string DatabaseRank,
    string Verdict,
    string Details,
    ulong? DiscordUserId,
    string? DiscordDisplayName,
    string? DiscordMention,
    ulong? ExpectedRoleId,
    string? ExpectedRoleName,
    IReadOnlyList<ulong> FoundRoleIds,
    IReadOnlyList<string> FoundRoleNames,
    string? MatchStrength,
    string? MatchedField,
    string? MatchedValue,
    string? PlayerAlias)
{
    public string MatchSummary =>
        string.IsNullOrWhiteSpace(MatchedField) || string.IsNullOrWhiteSpace(MatchedValue)
            ? ""
            : $"{MatchStrength} via {MatchedField} `{MatchedValue}`";

    public static DiscordRankAuditResult Skipped(DiscordRankAuditPlayer player, string verdict, string details) =>
        new(
            player.Id,
            player.Username,
            player.Status,
            player.WomRankMismatchIgnored,
            player.CurrentRank,
            verdict,
            details,
            null,
            null,
            null,
            null,
            null,
            [],
            [],
            null,
            null,
            null,
            null);

    public static DiscordRankAuditResult WithMatch(
        DiscordRankAuditPlayer player,
        string verdict,
        string details,
        DiscordRankRole expectedRole,
        IReadOnlyList<DiscordRankRole> foundRoles,
        DiscordMemberGuessMatch match) =>
        new(
            player.Id,
            player.Username,
            player.Status,
            player.WomRankMismatchIgnored,
            player.CurrentRank,
            verdict,
            details,
            match.UserId,
            match.DisplayLabel,
            match.Mention,
            expectedRole.RoleId,
            expectedRole.RoleName,
            foundRoles.Select(x => x.RoleId).ToList(),
            foundRoles.Select(x => x.RoleName).ToList(),
            match.Strength.ToString(),
            match.MatchedField,
            match.MatchedValue,
            match.PlayerAlias);
}

public sealed record UngnomifyRow(
    string Username,
    string CurrentRank,
    string SpecialRank);

public record WomRoleChoice(string Label, string Value);
