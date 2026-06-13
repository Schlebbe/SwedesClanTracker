using System.Globalization;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
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
    private static readonly IReadOnlyList<WomRoleChoice> WomRoleChoices =
    [
        new("Officer", "officer"),
        new("Commander", "commander"),
        new("Lieutenant", "lieutenant"),
        new("Captain", "captain"),
        new("Astral", "astral"),
        new("General", "general"),
        new("Brigadier", "brigadier"),
        new("Admiral", "admiral"),
        new("Marshal", "marshal"),
        new("Beast", "beast"),
        new("Imp", "imp")
    ];
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
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lookupBackoffUntilByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ulong, MessagePatchState> _messagePatchStateByMessageId = new();
    private static readonly TimeSpan MessagePatchMinInterval = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _discordMemberCacheLock = new(1, 1);
    private IReadOnlyList<DiscordMemberLookupCandidate>? _discordMemberCache;
    private DateTimeOffset _discordMemberCacheValidUntil = DateTimeOffset.MinValue;
    private static readonly TimeSpan DiscordGuessCommandTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan DiscordMemberCacheLockTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DiscordMemberDownloadTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DiscordMemberWarmupDownloadTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DiscordMemberSearchTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DiscordMemberSearchTotalTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TempleNameChangeDetectionWindow = TimeSpan.FromHours(6);
    private static readonly TimeSpan TempleNameChangeReminderInterval = TimeSpan.FromHours(2);
    private static readonly TimeSpan WomOnlyPostGracePeriod = TimeSpan.FromMinutes(5);

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

    private static async Task ReassignOrRemoveStatusEventsAsync(TrackerDbContext db, int removedPlayerId)
    {
        var replacementPlayerId = await db.Players
            .Where(x => x.Id != removedPlayerId)
            .OrderBy(x => x.Id)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync();
        var statusRows = await db.LifecycleEvents
            .Where(x => x.PlayerId == removedPlayerId && x.EventType == AppStatusConstants.EventType)
            .ToListAsync();

        if (replacementPlayerId.HasValue)
        {
            foreach (var status in statusRows) status.PlayerId = replacementPlayerId.Value;
        }
        else
        {
            db.LifecycleEvents.RemoveRange(statusRows);
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

        var pendingPlayerIds = pending
            .Select(x => x.PlayerId)
            .Distinct()
            .ToList();
        var mergePendingPlayerIds = new HashSet<int>();
        if (pendingPlayerIds.Count > 0)
        {
            var openMergePlayerIds = await db.LifecycleEvents
                .Where(x =>
                    x.EventType == "MERGE_ACTION_REQUIRED" &&
                    x.Status == "OPEN" &&
                    pendingPlayerIds.Contains(x.PlayerId))
                .Select(x => x.PlayerId)
                .Distinct()
                .ToListAsync(ct);
            mergePendingPlayerIds = openMergePlayerIds.ToHashSet();
        }

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

            var isMergePending = c.PlayerStatus == PlayerStatus.MERGE_SUGGESTED || mergePendingPlayerIds.Contains(c.PlayerId);
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

                    if (patchDecision == PromotionCardPatchDecision.SkipMessageNotActionable)
                    {
                        logger.LogInformation(
                            "Skipping promotion card patch for candidate {CandidateId}; message {MessageId} has no actionable promotion buttons.",
                            c.Id,
                            liveDiscordMessage.Id);
                        continue;
                    }

                    if (ShouldSkipMessagePatch(liveDiscordMessage.Id, renderFingerprint))
                    {
                        continue;
                    }

                    await liveDiscordMessage.ModifyAsync(props =>
                    {
                        props.Embed = embed;
                        props.Components = builder.Build();
                    });
                    RecordMessagePatched(liveDiscordMessage.Id, renderFingerprint);
                    postedEvent.MetadataJson = JsonUtil.Serialize(new
                    {
                        CandidateId = c.Id,
                        DiscordMessageId = liveDiscordMessage.Id,
                        ChannelId = liveDiscordMessage.Channel.Id,
                        RenderFingerprint = renderFingerprint
                    });
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
                    MetadataJson = JsonUtil.Serialize(new { CandidateId = c.Id, DiscordMessageId = msg.Id, ChannelId = _options.ChannelId, RenderFingerprint = renderFingerprint }),
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
            var help = new SlashCommandBuilder()
                .WithName("help")
                .WithDescription("Visar alla tillgängliga kommandon och vad de gör.");
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
            await socketGuild.CreateApplicationCommandAsync(help.Build());
            await socketGuild.CreateApplicationCommandAsync(update.Build());
            await socketGuild.CreateApplicationCommandAsync(add.Build());
            await socketGuild.CreateApplicationCommandAsync(remove.Build());
            await socketGuild.CreateApplicationCommandAsync(templeAdd.Build());
            await socketGuild.CreateApplicationCommandAsync(templeRemove.Build());
            await socketGuild.CreateApplicationCommandAsync(womAdd.Build());
            await socketGuild.CreateApplicationCommandAsync(womRemove.Build());
            await socketGuild.CreateApplicationCommandAsync(womRoleUpdate.Build());
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
        if (parts[0] != "promo" || parts.Length != 3) return;
        var action = parts[1];
        if (!int.TryParse(parts[2], out var candidateId)) return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
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
                now.AddSeconds(10),
                now.AddMinutes(1),
                dedupeCompletedSchedules: false);
            await db.SaveChangesAsync();
            var alreadyHandledAction = candidate.Status == PromotionStatus.APPROVED ? "approve" : "dismiss";
            var alreadyHandledText = $"Already handled ({candidate.Status})";
            await TryUpdatePromotionCardHandledAsync(component, alreadyHandledText, alreadyHandledAction);
            await RespondToComponentAsync(component, $"This promotion was already handled ({candidate.Status}).", ephemeral: true);
            return;
        }

        if (action == "approve")
        {
            player.CurrentRank = candidate.NewRank;
            await CloseOpenLifecycleEventsAsync(db, player.Id, "WOM_RANK_MISMATCH_IGNORED", "WOM_RANK_MISMATCH_REQUIRED");
            candidate.Status = PromotionStatus.APPROVED;
            ScheduleDelete(candidate.Id, player.Id);
        }
        else if (action == "dismiss")
        {
            candidate.Status = PromotionStatus.DISMISSED;
            ScheduleDelete(candidate.Id, player.Id);
        }
        else if (action == "rename")
        {
            player.Status = PlayerStatus.MERGE_SUGGESTED;
            await CloseOpenLifecycleEventsAsync(db, player.Id, "NEW_PLAYER", "DISCORD_MARK_RENAME_SUSPECT");
            await EnsureOpenMergeSuggestedEventAsync(db, player.Id, player.Username, component.User.Username);
            ScheduleDelete(candidate.Id, player.Id);
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
                Source = "discord",
                ChannelId = component.Channel.Id,
                DiscordMessageId = component.Message.Id
            }),
            Status = "OPEN",
            CreatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
        var handledText = $"Handled by {component.User.Username} ({action}) via discord";
        await TryUpdatePromotionCardHandledAsync(component, handledText, action);
        // Background reconciliation still runs for web/admin-originated updates and retry/delete scheduling.
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

    private async Task TryUpdatePromotionCardHandledAsync(SocketMessageComponent component, string handledText, string action)
    {
        var embed = BuildHandledEmbed(component.Message.Embeds.FirstOrDefault(), handledText, action);
        try
        {
            if (!component.HasResponded)
            {
                await component.UpdateAsync(props =>
                {
                    props.Components = new ComponentBuilder().Build();
                    props.Embed = embed;
                });
                return;
            }

            if (component.Message is IUserMessage userMessage)
            {
                await userMessage.ModifyAsync(props =>
                {
                    props.Components = new ComponentBuilder().Build();
                    props.Embed = embed;
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to apply immediate promotion card handled update for message {MessageId}.", component.Message.Id);
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
        var scheduleOwnerPlayerId = playerId;

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
            scheduleOwnerPlayerId = await db.Players
                .Where(x => x.Id != player.Id)
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .FirstOrDefaultAsync();

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
            await ReassignOrRemoveStatusEventsAsync(db, player.Id);
            db.LifecycleEvents.RemoveRange(db.LifecycleEvents.Where(x => x.PlayerId == player.Id && x.EventType != AppStatusConstants.EventType));
            db.PlayerSnapshots.RemoveRange(db.PlayerSnapshots.Where(x => x.PlayerId == player.Id));
            db.PromotionCandidates.RemoveRange(db.PromotionCandidates.Where(x => x.PlayerId == player.Id));
            db.Players.Remove(player);
        }
        else
        {
            await RespondToComponentAsync(component, "Unknown action.", ephemeral: true);
            return;
        }

        var actionEventPlayerId = action == "remove" && scheduleOwnerPlayerId > 0 ? scheduleOwnerPlayerId : playerId;
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = actionEventPlayerId,
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

        if (scheduleOwnerPlayerId > 0)
        {
            ScheduleChannelMessageDelete(
                db,
                scheduleOwnerPlayerId,
                component.Channel.Id,
                component.Message.Id,
                "TEMPLE_MISSING_DISCORD_DELETE_SCHEDULED",
                new { Reason = "temple-missing-action-handled", Action = action });
            await db.SaveChangesAsync();
        }
        else if (action == "remove")
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

        var scheduleOwnerPlayerId = playerId;
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
            scheduleOwnerPlayerId = await db.Players
                .Where(x => x.Id != player.Id)
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .FirstOrDefaultAsync();

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

            await ReassignOrRemoveStatusEventsAsync(db, player.Id);
            db.LifecycleEvents.RemoveRange(db.LifecycleEvents.Where(x => x.PlayerId == player.Id && x.EventType != AppStatusConstants.EventType));
            db.PlayerSnapshots.RemoveRange(db.PlayerSnapshots.Where(x => x.PlayerId == player.Id));
            db.PromotionCandidates.RemoveRange(db.PromotionCandidates.Where(x => x.PlayerId == player.Id));
            db.Players.Remove(player);
        }
        else
        {
            await RespondToComponentAsync(component, "Unknown action.", ephemeral: true);
            return;
        }

        var actionEventPlayerId = action == "remove" && scheduleOwnerPlayerId > 0 ? scheduleOwnerPlayerId : playerId;
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = actionEventPlayerId,
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

        if (scheduleOwnerPlayerId > 0)
        {
            ScheduleChannelMessageDelete(
                db,
                scheduleOwnerPlayerId,
                component.Channel.Id,
                component.Message.Id,
                "WOM_MISSING_DISCORD_DELETE_SCHEDULED",
                new { Reason = "wom-missing-action-handled", Action = action });
            await db.SaveChangesAsync();
        }
        else if (action == "remove")
        {
            try { await component.Message.DeleteAsync(); } catch { }
        }
    }

    private async Task HandleSelectMenuAsync(SocketMessageComponent component)
    {
        try
        {
            if (!component.HasResponded) await component.DeferAsync(ephemeral: true);
            var customId = component.Data.CustomId ?? "";
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

        if (messageId.HasValue)
        {
            var openDeleteEvents = db.LifecycleEvents
                .Where(x =>
                    x.EventType == "PROMOTION_DISCORD_DELETE_SCHEDULED" &&
                    x.Status == "OPEN")
                .ToList();
            var hasOpenDeleteForMessage = openDeleteEvents.Any(x =>
                MetadataUlongEquals(x.MetadataJson, "DiscordMessageId", messageId.Value));
            if (hasOpenDeleteForMessage) return;
        }
        else
        {
            var candidateDeleteEvents = db.LifecycleEvents
                .Where(x => x.EventType == "PROMOTION_DISCORD_DELETE_SCHEDULED")
                .ToList();
            var hasAnyDeleteForCandidate = candidateDeleteEvents.Any(x =>
                MetadataIntEquals(x.MetadataJson, "CandidateId", candidateId));
            if (hasAnyDeleteForCandidate) return;
        }

        var now = DateTimeOffset.UtcNow;
        ScheduleChannelMessageDelete(
            db,
            playerId,
            channelId,
            messageId,
            "PROMOTION_DISCORD_DELETE_SCHEDULED",
            new { CandidateId = candidateId, Reason = "promotion-action-handled" },
            now.AddSeconds(10),
            now.AddMinutes(1),
            dedupeCompletedSchedules: false);
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
        bool dedupeCompletedSchedules = true)
    {
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
            var hasScheduledForMessage = scheduledDeleteEvents.Any(x =>
                MetadataUlongEquals(x.MetadataJson, "DiscordMessageId", messageId.Value));
            if (hasScheduledForMessage) return;
        }

        var now = DateTimeOffset.UtcNow;
        var deleteAfter = deleteAfterUtc ?? now.AddMinutes(Math.Min(_discordDeleteDelayMinutes, _discordDeleteHardCapMinutes));
        var hardDeleteAfter = hardDeleteAfterUtc ?? now.AddMinutes(_discordDeleteHardCapMinutes);
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
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
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
        CancellationToken ct)
    {
        var hasScheduledDelete = await db.LifecycleEvents.AnyAsync(x =>
            (x.EventType == "PROMOTION_DISCORD_DELETE_SCHEDULED" ||
             x.EventType == "TEMPLE_MISSING_DISCORD_DELETE_SCHEDULED" ||
             x.EventType == "WOM_MISSING_DISCORD_DELETE_SCHEDULED" ||
             x.EventType == "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED") &&
            x.Status == "OPEN", ct);
        if (hasScheduledDelete)
        {
            var deleteRows = await db.LifecycleEvents
                .Where(x =>
                    (x.EventType == "PROMOTION_DISCORD_DELETE_SCHEDULED" ||
                     x.EventType == "TEMPLE_MISSING_DISCORD_DELETE_SCHEDULED" ||
                     x.EventType == "WOM_MISSING_DISCORD_DELETE_SCHEDULED" ||
                     x.EventType == "DISCORD_CHANNEL_RESPONSE_DELETE_SCHEDULED") &&
                    x.Status == "OPEN")
                .ToListAsync(ct);
            hasScheduledDelete = deleteRows.Any(x => MetadataUlongEquals(x.MetadataJson, "DiscordMessageId", messageId));
        }
        if (hasScheduledDelete) return;

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
                    return;
                }
                catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound ||
                                                           ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
                {
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
            scheduleHard);
    }

    private async Task<int?> ResolveLifecycleOwnerPlayerIdAsync(TrackerDbContext db, int preferredPlayerId, CancellationToken ct)
    {
        var preferredExists = await db.Players.AnyAsync(x => x.Id == preferredPlayerId, ct);
        if (preferredExists) return preferredPlayerId;
        var fallback = await db.Players.OrderBy(x => x.Id).Select(x => (int?)x.Id).FirstOrDefaultAsync(ct);
        return fallback;
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
            catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound ||
                                                       ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
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

        var promotionPosted = await db.LifecycleEvents
            .Where(x => x.EventType == "PROMOTION_DISCORD_POSTED")
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
                    await EnsureMessageDeleteScheduledOrDeletedAsync(
                        db,
                        ev.PlayerId,
                        chId,
                        msgId,
                        "PROMOTION_DISCORD_DELETE_SCHEDULED",
                        new { CandidateId = candidateId, Reason = "reconcile-completed-promotion" },
                        ev.CreatedAt,
                        ct);
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
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound ||
                                                   ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
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
                var msg = await channel.SendMessageAsync(pages[i]);
                db.LifecycleEvents.Add(new LifecycleEvent
                {
                    PlayerId = ownerPlayerId.Value,
                    EventType = "PET_HISCORES_DISCORD_POSTED",
                    MetadataJson = JsonUtil.Serialize(new
                    {
                        ChannelId = _options.PetHiscoresChannelId,
                        DiscordMessageId = msg.Id,
                        Page = i
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
                MessageId = ExtractUlong(x.MetadataJson, "DiscordMessageId")
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
                var msg = await channel.SendMessageAsync(pages[i]);
                db.LifecycleEvents.Add(new LifecycleEvent
                {
                    PlayerId = ownerId.Value,
                    EventType = "PET_HISCORES_DISCORD_POSTED",
                    MetadataJson = JsonUtil.Serialize(new
                    {
                        ChannelId = _options.PetHiscoresChannelId,
                        DiscordMessageId = msg.Id,
                        Page = i
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
            if (existing is not null && existing.MessageId.HasValue)
            {
                var (messageState, userMessage) = await TryGetTrackedUserMessageAsync(channel, existing.MessageId.Value);
                if (messageState == TrackedMessageState.Found && userMessage is not null)
                {
                    await userMessage.ModifyAsync(p => p.Content = pages[i]);
                    continue;
                }
                if (messageState == TrackedMessageState.Unknown)
                {
                    logger.LogWarning("Pet hiscore page message lookup was inconclusive for page {Page} in channel {ChannelId}; skipping recreate this cycle to avoid duplicates.", i, _options.PetHiscoresChannelId);
                    continue;
                }
            }

            var newMsg = await channel.SendMessageAsync(pages[i]);
            db.LifecycleEvents.Add(new LifecycleEvent
            {
                PlayerId = ownerId.Value,
                EventType = "PET_HISCORES_DISCORD_POSTED",
                MetadataJson = JsonUtil.Serialize(new
                {
                    ChannelId = _options.PetHiscoresChannelId,
                    DiscordMessageId = newMsg.Id,
                    Page = i
                }),
                Status = "DONE",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        if (mapped.Count > pages.Count)
        {
            for (var i = pages.Count; i < mapped.Count; i++)
            {
                try
                {
                    var msg = await channel.GetMessageAsync(mapped[i].MessageId!.Value);
                    if (msg is IUserMessage userMessage)
                    {
                        await userMessage.ModifyAsync(p => p.Content = "Pet Hiscores\n\nNo entries for this page.");
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
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
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
            "Om du har 30+ pets så har du chansen att få dina pets tillagda på din Templeprofil via Petcord <http://discord.gg/petcord>, Alice (sugarbunny.) är den som lägger till på din profil.\n\n**Pet Leaderboards**\n";
        const string continuedHeader = "**Pet Leaderboards (forts.)**\n";
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
            catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound ||
                                                       ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
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
        ScheduleChannelMessageDelete(
            db,
            playerId,
            channelId.Value,
            messageId.Value,
            "PROMOTION_DISCORD_DELETE_SCHEDULED",
            new { CandidateId = candidateId, Reason = reason },
            now.AddSeconds(10),
            now.AddMinutes(1),
            dedupeCompletedSchedules: false);
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
            }
        }
        catch
        {
            // best-effort metadata for Discord display/action handling
        }
        return values;
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

                var candidateOwnerId = await db.PromotionCandidates
                    .Where(x => x.Id == candidateId)
                    .Select(x => (int?)x.PlayerId)
                    .FirstOrDefaultAsync(ct);
                var candidatePostedEvents = posted
                    .Where(x => MetadataIntEquals(x.MetadataJson, "CandidateId", candidateId))
                    .ToList();
                if (candidateOwnerId.HasValue)
                {
                    var ownerSpecific = candidatePostedEvents
                        .Where(x => x.PlayerId == candidateOwnerId.Value)
                        .ToList();
                    if (ownerSpecific.Count > 0)
                    {
                        candidatePostedEvents = ownerSpecific;
                    }
                }

                var postEvent = candidatePostedEvents.FirstOrDefault(x => !IsPromotionPostedSupersededByMerge(x));
                postEvent ??= candidatePostedEvents.FirstOrDefault();
                if (postEvent is null) { u.Status = "DONE"; continue; }

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
                    new { CandidateId = candidateId, Reason = "promotion-action-handled-web" },
                    deleteNow.AddSeconds(10),
                    deleteNow.AddMinutes(1),
                    dedupeCompletedSchedules: false);
                if (msg is IUserMessage userMessage)
                {
                    var handled = $"Handled by {handledBy} ({action}) via {source}";
                    try
                    {
                        await userMessage.ModifyAsync(props =>
                        {
                            props.Components = new ComponentBuilder().Build();
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

    private async Task PostTempleNameChangeNeededMessages(CancellationToken ct)
    {
        if (_client is null) return;
        var channel = await ResolveMessageChannelAsync(_options.ChannelId);
        if (channel is null) return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();

        var detectionInput = await BuildTempleNameChangeDetectionInputAsync(db, ct);
        var detection = TempleNameChangeDetector.Detect(detectionInput);
        if (detection is null)
        {
            var openRequirements = await db.LifecycleEvents
                .Where(x => x.EventType == TempleNameChangeReviewEventTypes.Required && x.Status == "OPEN")
                .ToListAsync(ct);
            foreach (var openRequirement in openRequirements)
            {
                await SuppressTempleNameChangeNoisyCardsAsync(db, openRequirement, ct);
            }
            await PostDueConfirmedTempleNameChangeRemindersAsync(db, channel, openRequirements, ct);
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

    private async Task PostDueConfirmedTempleNameChangeRemindersAsync(
        TrackerDbContext db,
        IMessageChannel channel,
        IReadOnlyList<LifecycleEvent> openRequirements,
        CancellationToken ct)
    {
        if (openRequirements.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        var postedEvents = await db.LifecycleEvents
            .Where(x => x.EventType == TempleNameChangeReviewEventTypes.DiscordPosted)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

        foreach (var requiredEvent in openRequirements)
        {
            var metadata = ReadLifecycleMetadata(requiredEvent.MetadataJson);
            var confirmedAt = PickLifecycleDateTimeOffset(metadata, "ConfirmedAt");
            if (!confirmedAt.HasValue) continue;

            var latestPostedEvent = postedEvents.FirstOrDefault(x => MetadataIntEquals(x.MetadataJson, "RequiredEventId", requiredEvent.Id));
            var latestPostedMetadata = latestPostedEvent is null ? [] : ReadLifecycleMetadata(latestPostedEvent.MetadataJson);
            var reminderPostedAt = PickLifecycleDateTimeOffset(latestPostedMetadata, "ReminderPostedAt");
            var lastVisibleReminderAt = new DateTimeOffset?[]
                {
                    confirmedAt.Value,
                    latestPostedEvent is not null && latestPostedEvent.CreatedAt > confirmedAt.Value ? latestPostedEvent.CreatedAt : null,
                    reminderPostedAt
                }
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .Max();
            if (now - lastVisibleReminderAt < TempleNameChangeReminderInterval)
            {
                continue;
            }

            var previousUsername = NormalizeUsername(PickLifecycleValue(metadata, "PreviousUsername") ?? "");
            var newUsername = NormalizeUsername(PickLifecycleValue(metadata, "NewUsername") ?? "");
            if (string.IsNullOrWhiteSpace(previousUsername) || string.IsNullOrWhiteSpace(newUsername)) continue;

            var rank = PickLifecycleValue(metadata, "Rank") ?? "Unknown";
            var womRole = PickLifecycleValue(metadata, "WomRole") ?? "Unknown";
            var previousDiscordGuess = await GuessDiscordMemberForNamesAsync([previousUsername], ct);
            var newDiscordGuess = await GuessDiscordMemberForNamesAsync([newUsername], ct);
            var renderFingerprint = ComputeRenderFingerprint(new
            {
                Type = "temple-name-change-needed-card",
                Reminder = true,
                RequiredEventId = requiredEvent.Id,
                PreviousUsername = previousUsername,
                NewUsername = newUsername,
                Rank = rank,
                WomRole = womRole,
                ConfirmedAt = confirmedAt.Value,
                PreviousDiscordGuess = FormatDiscordGuessForFingerprint(previousDiscordGuess),
                NewDiscordGuess = FormatDiscordGuessForFingerprint(newDiscordGuess)
            });
            var embed = BuildTempleNameChangeNeededEmbed(
                previousUsername,
                newUsername,
                rank,
                womRole,
                previousDiscordGuess,
                newDiscordGuess);
            var components = BuildTempleNameChangeNeededComponents(requiredEvent.Id);

            if (latestPostedEvent is not null)
            {
                var lookupKey = $"temple-name-change-reminder:{requiredEvent.Id}";
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
                if (lookupState == PostedMessageLookupState.Found && liveDiscordMessage is not null)
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
                    latestPostedEvent.MetadataJson = JsonUtil.Serialize(new
                    {
                        RequiredEventId = requiredEvent.Id,
                        PreviousUsername = previousUsername,
                        NewUsername = newUsername,
                        Reminder = true,
                        ReminderForConfirmedAt = confirmedAt.Value,
                        ReminderPostedAt = DateTimeOffset.UtcNow,
                        PreviousDiscordGuess = FormatDiscordGuessForFingerprint(previousDiscordGuess),
                        NewDiscordGuess = FormatDiscordGuessForFingerprint(newDiscordGuess),
                        ChannelId = liveDiscordMessage.Channel.Id,
                        DiscordMessageId = liveDiscordMessage.Id,
                        RenderFingerprint = renderFingerprint
                    });
                    continue;
                }
                if (lookupState == PostedMessageLookupState.Missing)
                {
                    await RecordMissingTrackedMessageEventAsync(
                        db,
                        requiredEvent.PlayerId,
                        "temple-name-change-reminder",
                        latestPostedEvent.Id,
                        requiredEvent.Id,
                        channelId,
                        messageId,
                        "post-temple-name-change-reminder",
                        ct);
                }
            }

            var lease = await TryAcquirePostLeaseAsync(db, requiredEvent.PlayerId, $"temple-name-change-reminder:{requiredEvent.Id}", ct);
            if (lease is null) continue;
            try
            {
                await db.Entry(requiredEvent).ReloadAsync(ct);
                if (requiredEvent.Status != "OPEN") continue;
                var latestMetadata = ReadLifecycleMetadata(requiredEvent.MetadataJson);
                if (!IsTempleNameChangeConfirmed(latestMetadata)) continue;
                var latestConfirmedAt = PickLifecycleDateTimeOffset(latestMetadata, "ConfirmedAt");
                if (!latestConfirmedAt.HasValue || DateTimeOffset.UtcNow - latestConfirmedAt.Value < TempleNameChangeReminderInterval)
                {
                    continue;
                }

                var msg = await channel.SendMessageAsync(embed: embed, components: components);
                db.LifecycleEvents.Add(new LifecycleEvent
                {
                    PlayerId = requiredEvent.PlayerId,
                    EventType = TempleNameChangeReviewEventTypes.DiscordPosted,
                    MetadataJson = JsonUtil.Serialize(new
                    {
                        RequiredEventId = requiredEvent.Id,
                        PreviousUsername = previousUsername,
                        NewUsername = newUsername,
                        Reminder = true,
                        ReminderForConfirmedAt = latestConfirmedAt.Value,
                        ReminderPostedAt = DateTimeOffset.UtcNow,
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
    }

    private async Task<TempleNameChangeDetectionInput> BuildTempleNameChangeDetectionInputAsync(TrackerDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var recentCutoff = now - TempleNameChangeDetectionWindow;
        var openEvents = await db.LifecycleEvents
            .Where(x =>
                x.Status == "OPEN" &&
                (x.EventType == "WOM_MISSING_ACTION_REQUIRED" ||
                 x.EventType == "TEMPLE_MISSING_ACTION_REQUIRED" ||
                 x.EventType == "WOM_ONLY_ACTION_REQUIRED" ||
                 x.EventType == "MERGE_ACTION_REQUIRED"))
            .ToListAsync(ct);

        var playerIds = openEvents
            .Where(x => x.EventType is "WOM_MISSING_ACTION_REQUIRED" or "TEMPLE_MISSING_ACTION_REQUIRED")
            .Select(x => x.PlayerId)
            .Distinct()
            .ToList();
        var players = await db.Players
            .Where(x => playerIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Username, x.CurrentRank, x.Status })
            .ToListAsync(ct);

        var oldCandidates = players
            .Where(x => x.Status == PlayerStatus.MISSING_PENDING_REVIEW)
            .Select(x =>
            {
                var womMissing = openEvents
                    .Where(ev => ev.PlayerId == x.Id && ev.EventType == "WOM_MISSING_ACTION_REQUIRED")
                    .OrderByDescending(ev => ev.CreatedAt)
                    .FirstOrDefault();
                var templeMissing = openEvents
                    .Where(ev => ev.PlayerId == x.Id && ev.EventType == "TEMPLE_MISSING_ACTION_REQUIRED")
                    .OrderByDescending(ev => ev.CreatedAt)
                    .FirstOrDefault();
                return new TempleNameChangeOldCandidate(
                    x.Id,
                    x.Username,
                    x.CurrentRank,
                    womMissing?.Id,
                    womMissing?.CreatedAt,
                    templeMissing?.Id,
                    templeMissing?.CreatedAt);
            })
            .Where(x =>
                (x.WomMissingCreatedAt.HasValue && x.WomMissingCreatedAt >= recentCutoff) ||
                (x.TempleMissingCreatedAt.HasValue && x.TempleMissingCreatedAt >= recentCutoff))
            .ToList();

        var womOnlyCandidates = openEvents
            .Where(x => x.EventType == "WOM_ONLY_ACTION_REQUIRED")
            .Select(x =>
            {
                var metadata = ReadLifecycleMetadata(x.MetadataJson);
                var username = PickLifecycleValue(metadata, "Username", "Player") ?? "";
                var role = PickLifecycleValue(metadata, "ActualWomRole") ?? "";
                return new TempleNameChangeWomOnlyCandidate(x.Id, username, role, x.CreatedAt);
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Username))
            .ToList();

        var openMerges = openEvents
            .Where(x => x.EventType == "MERGE_ACTION_REQUIRED")
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

        if (matching is null && openRequirements.Count > 0) return null;

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
        CancellationToken ct)
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
            return;
        }
        if (lookupState == PostedMessageLookupState.Malformed)
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
                    $"Suppressed by Temple name change review: {previousUsername} -> {newUsername}",
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

    private static async Task CloseOpenTempleNameChangeForMergeAsync(
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
                var isImp = await ReevaluatePlayerForManualPetsAsync(petDb, petWom, petsPlayer);
                await petDb.SaveChangesAsync();

                await RespondAndAutoDeleteAsync(command, isImp
                    ? $"Manual pets set: `{petsPlayer.Username}` -> `{petCount}`. Player has WiseOldMan role `imp` and is excluded from rank upgrades."
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

            if (string.Equals(command.Data.Name, "discord-guess", StringComparison.OrdinalIgnoreCase))
            {
                await HandleDiscordGuessSlashCommandAsync(command);
                return;
            }

            if (string.Equals(command.Data.Name, "help", StringComparison.OrdinalIgnoreCase))
            {
                await HandleHelpSlashCommandAsync(command);
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

        string ToDisplay(IReadOnlyList<string> names)
        {
            if (names.Count == 0) return "None";
            const int maxNames = 40;
            var shown = names.Take(maxNames).Select(x => $"• {x}");
            var text = string.Join("\n", shown);
            if (names.Count > maxNames)
            {
                text += $"\n...and {names.Count - maxNames} more";
            }
            return text;
        }

        var embed = new EmbedBuilder()
            .WithTitle("Ignored Players")
            .WithColor(new Color(59, 130, 246))
            .WithDescription("Currently open ignore flags by category.")
            .AddField($"WOM-only ignored ({womOnlyIgnored.Count})", ToDisplay(womOnlyIgnored), false)
            .AddField($"WOM rank mismatch ignored ({womRankMismatchIgnored.Count})", ToDisplay(womRankMismatchIgnored), false)
            .WithTimestamp(DateTimeOffset.Now)
            .Build();

        await RespondAndAutoDeleteAsync(command, embed, ephemeral: true);
    }

    private async Task HandleHelpSlashCommandAsync(SocketSlashCommand command)
    {
        var helpText = """
### Info / Sync
**/discord-guess <player>**  
Visar botens bästa Discord-matchning för spelaren med klickbar mention

**/lookup <player>**  
Visar spelarens sammanfattning (rank, stats, pets, Temple/WOM-status, senaste sync)

**/update <player>**  
Prioriterar spelaren i uppdateringskön för snabb sync

**/set-pets <player> <count>**  
Sätter manuell pet count override för spelaren

### Temple / WOM medlemskap
**/temple-add <players>**  
Lägger till en eller flera spelare i TempleOSRS  
Format: kommaseparerat, t.ex. A, B, C

**/temple-remove <players>**  
Tar bort en eller flera spelare från TempleOSRS

**/wom-add <players>**  
Lägger till en eller flera spelare i WiseOldMan

**/wom-remove <players>**  
Tar bort en eller flera spelare från WiseOldMan

**/add <players>**  
Kombokommando: lägger till spelare i både TempleOSRS och WiseOldMan

**/remove <players>**  
Kombokommando: tar bort spelare från både TempleOSRS och WiseOldMan

**/wom-role-update <player> <rank>**  
Uppdaterar spelarens rank i WiseOldMan

### Review / Ignore-hantering
**/requeue-review-card <player>**  
Tvingar ompostning/återskapande av review-kort för spelaren om review fortfarande är aktiv

**/unignore <player>**  
Tar bort ignore för spelaren i:
- WOM-only ignore
- WOM rank mismatch ignore

**/show-ignored**  
Visar alla spelare som just nu är ignorerade i:
- WOM-only
- WOM rank mismatch
""";

        await RespondAndAutoDeleteAsync(command, helpText, ephemeral: true);
    }

    private async Task LogSlashCommandAsync(SocketSlashCommand command, bool adminLocked, bool allowed)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
            var ownerId = await ResolveLifecycleOwnerPlayerIdAsync(db, 0, CancellationToken.None);
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
               string.Equals(commandName, "help", StringComparison.OrdinalIgnoreCase) ||
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
             string.Equals(prefix, "merge", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAdminLockedSlashCommand(string commandName)
    {
        return string.Equals(commandName, "update", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "discord-guess", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "set-pets", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "add", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "remove", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "temple-add", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "temple-remove", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "wom-add", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "wom-remove", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandName, "wom-role-update", StringComparison.OrdinalIgnoreCase) ||
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
        var expected = RankOrder(expectedRank);
        var actual = RankOrder(actualWomRole);
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

        var isImpAccount = await wiseOldManClient.IsImpAccountAsync(player.Username, CancellationToken.None);
        if (isImpAccount)
        {
            var pendingForImp = await db.PromotionCandidates
                .Where(x => x.PlayerId == player.Id && x.Status == PromotionStatus.PENDING)
                .ToListAsync();
            if (pendingForImp.Count > 0)
            {
                db.PromotionCandidates.RemoveRange(pendingForImp);
            }
            return true;
        }

        if (RankOrder(player.EligibleRank) <= RankOrder(player.CurrentRank))
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

    private static int RankOrder(string rank)
    {
        var normalized = NormalizeRankName(rank);
        string[] order = ["Recruit", "Officer", "Commander", "Lieutenant", "Captain", "Astral", "General", "Brigadier", "Admiral", "Marshal", "Beast"];
        for (var i = 0; i < order.Length; i++)
        {
            if (string.Equals(order[i], normalized, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return 0;
    }

    private static string NormalizeRankName(string rank) => RankRules.NormalizeRankName(rank);
}

public class DiscordBotOptions
{
    public bool Enabled { get; set; }
    public string Token { get; set; } = "";
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong PetHiscoresChannelId { get; set; }
    public ulong AdminRoleId { get; set; }
}

public record WomRoleChoice(string Label, string Value);
