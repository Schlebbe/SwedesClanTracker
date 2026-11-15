using System.Collections;
using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SwedesClanTracker.Core;
using SwedesClanTracker.Worker;

namespace SwedesClanTracker.Core.Tests;

public sealed class DiscordMissingActionBehaviorTests
{
    [Theory]
    [InlineData("temple", "add")]
    [InlineData("wom", "reinstate")]
    [InlineData("wom", "remove")]
    public async Task Controlled_previous_identity_cannot_execute_missing_action(
        string cardType,
        string action)
    {
        await using var db = CreateDbContext();
        var previous = new Player { Username = "Yntg", Status = PlayerStatus.MISSING_PENDING_REVIEW };
        var current = new Player { Username = "Lindal", Status = PlayerStatus.MERGE_SUGGESTED };
        db.Players.AddRange(previous, current);
        await db.SaveChangesAsync();
        db.LifecycleEvents.AddRange(
            new LifecycleEvent
            {
                PlayerId = previous.Id,
                EventType = "TEMPLE_MISSING_ACTION_REQUIRED",
                MetadataJson = JsonUtil.Serialize(new { previous.Username }),
                Status = "OPEN",
                CreatedAt = DateTimeOffset.UtcNow
            },
            new LifecycleEvent
            {
                PlayerId = current.Id,
                EventType = "MERGE_ACTION_REQUIRED",
                MetadataJson = JsonUtil.Serialize(new
                {
                    NewPlayer = current.Username,
                    SuggestedPrevious = previous.Username,
                    SuggestedPreviousPlayerId = previous.Id
                }),
                Status = "OPEN",
                CreatedAt = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync();
        var beforeEventIds = await db.LifecycleEvents
            .Select(x => x.Id)
            .ToArrayAsync();
        var responses = new List<string>();
        var http = new RecordingHttpClientFactory();
        await using var provider = BuildProvider(db, new FakeWiseOldManClient());
        var worker = CreateWorker(
            provider,
            http,
            new DiscordPromotionBotWorkerTestHooks
            {
                RespondToComponentAsync = (_, text, _) =>
                {
                    responses.Add(text);
                    return Task.CompletedTask;
                }
            });
        var component = CreateComponent();

        var customParts = cardType == "temple"
            ? new[] { "missing", action, previous.Id.ToString() }
            : new[] { "wommissing", action, previous.Id.ToString() };
        await InvokeHandlerAsync(worker, cardType == "temple"
            ? "HandleMissingTempleButtonAsync"
            : "HandleMissingWomButtonAsync", component, customParts);

        db.ChangeTracker.Clear();
        var persistedPrevious = await db.Players.SingleAsync(x => x.Id == previous.Id);
        var persistedEvents = await db.LifecycleEvents.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(PlayerStatus.MISSING_PENDING_REVIEW, persistedPrevious.Status);
        Assert.Equal(beforeEventIds.OrderBy(x => x), persistedEvents.Select(x => x.Id).OrderBy(x => x));
        Assert.Equal(0, http.RequestCount);
        Assert.Single(responses);
        Assert.Contains("controlled by an open rename review", responses[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unrelated_missing_identity_keeps_the_existing_remove_behavior()
    {
        await using var db = CreateDbContext();
        var player = new Player { Username = "Unrelated", Status = PlayerStatus.MISSING_PENDING_REVIEW };
        db.Players.Add(player);
        await db.SaveChangesAsync();
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = player.Id,
            EventType = "WOM_MISSING_ACTION_REQUIRED",
            MetadataJson = JsonUtil.Serialize(new { player.Username }),
            Status = "OPEN",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var http = new RecordingHttpClientFactory();
        await using var provider = BuildProvider(db, new FakeWiseOldManClient());
        var updates = 0;
        var worker = CreateWorker(
            provider,
            http,
            new DiscordPromotionBotWorkerTestHooks
            {
                UpdateComponentMessageAsync = (_, _) =>
                {
                    updates++;
                    return Task.CompletedTask;
                }
            });

        await InvokeHandlerAsync(
            worker,
            "HandleMissingWomButtonAsync",
            CreateComponent(),
            ["wommissing", "remove", player.Id.ToString()]);

        db.ChangeTracker.Clear();
        Assert.Equal(2, http.RequestCount);
        Assert.Equal(PlayerStatus.REMOVED_CONFIRMED, (await db.Players.SingleAsync(x => x.Id == player.Id)).Status);
        Assert.Equal("DONE", (await db.LifecycleEvents.SingleAsync(x => x.EventType == "WOM_MISSING_ACTION_REQUIRED")).Status);
        Assert.True(await db.LifecycleEvents.AnyAsync(x => x.EventType == "WOM_MISSING_ACTION_APPLIED"));
        Assert.Equal(1, updates);
    }

    [Theory]
    [InlineData("temple", "abort")]
    [InlineData("temple", "reassign")]
    [InlineData("wom", "abort")]
    [InlineData("wom", "reassign")]
    public async Task Restored_missing_card_restores_actual_enabled_controls(
        string cardType,
        string transition)
    {
        await using var db = CreateDbContext();
        var isWomCard = cardType == "wom";
        var requiredEventType = isWomCard
            ? "WOM_MISSING_ACTION_REQUIRED"
            : "TEMPLE_MISSING_ACTION_REQUIRED";
        var postedEventType = isWomCard
            ? "WOM_MISSING_DISCORD_POSTED"
            : "TEMPLE_MISSING_DISCORD_POSTED";
        var deleteEventType = isWomCard
            ? "WOM_MISSING_DISCORD_DELETE_SCHEDULED"
            : "TEMPLE_MISSING_DISCORD_DELETE_SCHEDULED";
        var originalPrevious = new Player
        {
            Username = "Yntg",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = PlayerStatus.MISSING_PENDING_REVIEW,
            LastSeen = DateTimeOffset.UtcNow.AddHours(-1)
        };
        var selectedPrevious = new Player
        {
            Username = "Other Old",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = PlayerStatus.ACTIVE,
            LastSeen = DateTimeOffset.UtcNow.AddHours(-1)
        };
        var current = new Player
        {
            Username = "Lindal",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = PlayerStatus.MERGE_SUGGESTED,
            LastSeen = DateTimeOffset.UtcNow
        };
        db.Players.AddRange(originalPrevious, selectedPrevious, current);
        await db.SaveChangesAsync();

        var required = new LifecycleEvent
        {
            PlayerId = originalPrevious.Id,
            EventType = requiredEventType,
            MetadataJson = JsonUtil.Serialize(new { originalPrevious.Username }),
            Status = "OPEN",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2)
        };
        var missing = new LifecycleEvent
        {
            PlayerId = originalPrevious.Id,
            EventType = "MISSING_IN_ROSTER",
            MetadataJson = JsonUtil.Serialize(new { originalPrevious.Username }),
            Status = "OPEN",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2)
        };
        db.LifecycleEvents.AddRange(required, missing);
        await db.SaveChangesAsync();
        var requiredId = required.Id;

        var messageState = new FakeMessageState();
        var channel = CreateFakeMessageChannel(messageState, 321UL, 900UL);
        var templeRoster = isWomCard
            ? new[] { originalPrevious.Username }
            : Array.Empty<string>();
        var wiseOldManRoles = isWomCard
            ? new Dictionary<string, string?> { [originalPrevious.Username] = null }
            : new Dictionary<string, string?> { [originalPrevious.Username] = "member" };
        var http = new RecordingHttpClientFactory(templeRoster);
        var wiseOldMan = new FakeWiseOldManClient(wiseOldManRoles);
        await using var serviceProvider = BuildProvider(db, wiseOldMan);
        var worker = CreateWorker(
            serviceProvider,
            http,
            new DiscordPromotionBotWorkerTestHooks
            {
                ResolveMessageChannelAsync = _ => Task.FromResult<object?>(channel)
            });
        SetWorkerDiscordState(worker, 321UL);

        // Establish the original card through the normal posting path.
        await InvokePrivateAsync(
            worker,
            isWomCard ? "PostWomMissingActionMessages" : "PostTempleMissingActionMessages",
            CancellationToken.None);
        db.ChangeTracker.Clear();
        LifecycleEvent posted = await db.LifecycleEvents.SingleAsync(x =>
            x.PlayerId == originalPrevious.Id && x.EventType == postedEventType);
        var postedId = posted.Id;
        posted.CreatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        await db.SaveChangesAsync();
        var originalFingerprint = JsonDocument.Parse(posted.MetadataJson)
            .RootElement.GetProperty("RenderFingerprint").GetString();
        Assert.False(string.IsNullOrWhiteSpace(originalFingerprint));
        AssertActualButtons(
            messageState,
            ExpectedMissingButtonIds(cardType, originalPrevious.Id));

        // Open the merge review and run the same suppression path used by the
        // worker before a merge card is posted.
        db.LifecycleEvents.AddRange(
            CreateMergeEvent(current.Id, "MERGE_SUGGESTED", originalPrevious.Username, originalPrevious.Id),
            CreateMergeEvent(current.Id, "MERGE_ACTION_REQUIRED", originalPrevious.Username, originalPrevious.Id));
        await db.SaveChangesAsync();
        Assert.True(await db.LifecycleEvents.AnyAsync(x =>
            x.PlayerId == current.Id &&
            x.EventType == "MERGE_ACTION_REQUIRED" &&
            x.Status == "OPEN"));

        await InvokePrivateAsync(
            worker,
            "SuppressTempleMissingForMergeAsync",
            db,
            originalPrevious.Username,
            current.Id,
            CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.True(messageState.ComponentsWereSupplied);
        Assert.Empty(ReadMessageButtons(messageState.Message!));
        var superseded = await db.LifecycleEvents.SingleAsync(x =>
            x.EventType == DiscordMergeCardRules.SupersededCardHandledEventType &&
            x.PlayerId == originalPrevious.Id);
        Assert.Equal("DONE", superseded.Status);
        var openSchedule = await db.LifecycleEvents.SingleAsync(x =>
            x.PlayerId == current.Id &&
            x.EventType == deleteEventType &&
            x.Status == "OPEN");
        Assert.Equal(900UL, ExtractMetadataUlong(openSchedule.MetadataJson, "DiscordMessageId"));

        var mergeService = new MergeReviewService(db, new FakeWiseOldManClient());
        var transitionResult = transition == "abort"
            ? await mergeService.AbortAsync(current.Id, "tester", "test", CancellationToken.None)
            : await mergeService.ReassignAsync(current.Id, selectedPrevious.Username, "tester", "test", CancellationToken.None);
        Assert.True(transitionResult.Success);

        await InvokePrivateAsync(
            worker,
            isWomCard ? "PostWomMissingActionMessages" : "PostTempleMissingActionMessages",
            CancellationToken.None);
        db.ChangeTracker.Clear();
        var restoredRequired = await db.LifecycleEvents.SingleAsync(x => x.Id == requiredId);
        var restoredPosted = await db.LifecycleEvents.SingleAsync(x => x.Id == postedId);
        var cancelledSchedule = await db.LifecycleEvents.SingleAsync(x => x.Id == openSchedule.Id);
        Assert.Equal(requiredId, restoredRequired.Id);
        Assert.Equal(postedId, restoredPosted.Id);
        Assert.Equal("OPEN", restoredRequired.Status);
        Assert.Equal(originalFingerprint, JsonDocument.Parse(restoredPosted.MetadataJson).RootElement.GetProperty("RenderFingerprint").GetString());
        Assert.Equal("DONE", cancelledSchedule.Status);
        Assert.True(DiscordDeleteScheduleMatcher.IsCancelled(cancelledSchedule));
        AssertActualButtons(
            messageState,
            ExpectedMissingButtonIds(cardType, originalPrevious.Id));

        var callsAfterRecovery = messageState.ModifyCalls;
        await InvokePrivateAsync(
            worker,
            isWomCard ? "PostWomMissingActionMessages" : "PostTempleMissingActionMessages",
            CancellationToken.None);
        db.ChangeTracker.Clear();
        Assert.Equal(callsAfterRecovery, messageState.ModifyCalls);
        Assert.Equal(
            1,
            await db.LifecycleEvents.CountAsync(x =>
                x.PlayerId == originalPrevious.Id &&
                x.EventType == postedEventType));
        Assert.Equal(1, await db.LifecycleEvents.CountAsync(x => x.EventType == deleteEventType));

        await InvokePrivateAsync(worker, "ProcessScheduledDeletes", CancellationToken.None);
        Assert.Equal(0, messageState.DeleteCalls);

        var restoredPlayer = await db.Players.SingleAsync(x => x.Id == originalPrevious.Id);
        var requirementToResolve = await db.LifecycleEvents.SingleAsync(x => x.Id == requiredId);
        restoredPlayer.Status = PlayerStatus.ACTIVE;
        requirementToResolve.Status = "DONE";
        await db.SaveChangesAsync();

        await InvokePrivateAsync(worker, "ReconcileCompletedMessageDeletes", CancellationToken.None);
        db.ChangeTracker.Clear();
        Assert.Equal(1, messageState.DeleteCalls);
        var cleanup = await db.LifecycleEvents.SingleAsync(x =>
            x.EventType == deleteEventType && x.Id != openSchedule.Id);
        Assert.Equal("DONE", cleanup.Status);
        Assert.False(DiscordDeleteScheduleMatcher.IsCancelled(cleanup));
    }

    [Theory]
    [InlineData(PlayerStatus.ACTIVE, false)]
    [InlineData(PlayerStatus.REMOVED_CONFIRMED, false)]
    [InlineData(PlayerStatus.MISSING_PENDING_REVIEW, true)]
    public async Task Missing_card_posting_skips_active_removed_and_suppressed_identities(
        PlayerStatus status,
        bool suppressed)
    {
        await using var db = CreateDbContext();
        var player = new Player
        {
            Username = "Yntg",
            CurrentRank = "Brassican",
            EligibleRank = "Brassican",
            Status = status,
            LastSeen = DateTimeOffset.UtcNow.AddHours(-1)
        };
        db.Players.Add(player);
        await db.SaveChangesAsync();
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = player.Id,
            EventType = "TEMPLE_MISSING_ACTION_REQUIRED",
            MetadataJson = JsonUtil.Serialize(new { player.Username }),
            Status = "OPEN",
            CreatedAt = DateTimeOffset.UtcNow
        });
        if (suppressed)
        {
            var current = new Player { Username = "Lindal", Status = PlayerStatus.MERGE_SUGGESTED };
            db.Players.Add(current);
            await db.SaveChangesAsync();
            db.LifecycleEvents.AddRange(
                CreateMergeEvent(current.Id, "MERGE_SUGGESTED", player.Username, player.Id),
                CreateMergeEvent(current.Id, "MERGE_ACTION_REQUIRED", player.Username, player.Id));
        }
        await db.SaveChangesAsync();

        var state = new FakeMessageState();
        var channel = CreateFakeMessageChannel(state, 321UL, 900UL);
        await using var provider = BuildProvider(db, new FakeWiseOldManClient());
        var worker = CreateWorker(
            provider,
            new RecordingHttpClientFactory(),
            new DiscordPromotionBotWorkerTestHooks
            {
                ResolveMessageChannelAsync = _ => Task.FromResult<object?>(channel)
            });
        SetWorkerDiscordState(worker, 321UL);

        await InvokePrivateAsync(worker, "PostTempleMissingActionMessages", CancellationToken.None);

        Assert.Equal(0, state.GetCalls);
        Assert.Equal(0, state.SendCalls);
        Assert.Equal(0, state.ModifyCalls);
    }

    private static DiscordPromotionBotWorker CreateWorker(
        IServiceProvider provider,
        IHttpClientFactory httpClientFactory,
        DiscordPromotionBotWorkerTestHooks hooks)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TempleOsrs:GroupId"] = "449",
                ["TempleOsrs:ApiKey"] = "temple-key",
                ["WiseOldMan:GroupId"] = "7173",
                ["WiseOldMan:VerificationCode"] = "wom-code"
            })
            .Build();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var reporter = new AppStatusReporter(scopeFactory, NullLogger<AppStatusReporter>.Instance);
        return new DiscordPromotionBotWorker(
            scopeFactory,
            configuration,
            httpClientFactory,
            new TempleRosterCache(),
            new PlayerUpdateQueue(),
            reporter,
            NullLogger<DiscordPromotionBotWorker>.Instance,
            hooks);
    }

    private static ServiceProvider BuildProvider(TrackerDbContext db, IWiseOldManClient wiseOldManClient)
    {
        return new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton(wiseOldManClient)
            .BuildServiceProvider();
    }

    private static async Task InvokeHandlerAsync(
        DiscordPromotionBotWorker worker,
        string methodName,
        object component,
        string[] parts)
    {
        var method = typeof(DiscordPromotionBotWorker).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = method!.Invoke(worker, [component, parts]);
        await Assert.IsAssignableFrom<Task>(task);
    }

    private static async Task InvokePrivateAsync(
        DiscordPromotionBotWorker worker,
        string methodName,
        params object?[] args)
    {
        var method = typeof(DiscordPromotionBotWorker).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = method!.Invoke(worker, args);
        await Assert.IsAssignableFrom<Task>(task);
    }

    private static void SetWorkerDiscordState(DiscordPromotionBotWorker worker, ulong channelId)
    {
        var clientType = Type.GetType("Discord.WebSocket.DiscordSocketClient, Discord.Net.WebSocket")!;
        var client = RuntimeHelpers.GetUninitializedObject(clientType);
        SetField(worker, "_client", client);
        SetField(worker, "_options", new DiscordBotOptions { ChannelId = channelId });
    }

    private static object CreateFakeMessageChannel(FakeMessageState state, ulong channelId, ulong messageId)
    {
        var channelType = Type.GetType("Discord.IMessageChannel, Discord.Net.Core")!;
        var userMessageType = Type.GetType("Discord.IUserMessage, Discord.Net.Core")!;
        var channel = (FakeMessageChannelProxy)DispatchProxy.Create(channelType, typeof(FakeMessageChannelProxy));
        var message = (FakeUserMessageProxy)DispatchProxy.Create(userMessageType, typeof(FakeUserMessageProxy));
        state.Channel = channel;
        state.Message = message;
        channel.State = state;
        channel.ChannelId = channelId;
        message.State = state;
        message.Channel = channel;
        message.MessageId = messageId;
        return channel;
    }

    private static object? CreateTaskResult(Type returnType, object? value)
    {
        if (returnType == typeof(Task)) return Task.CompletedTask;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            var result = value is not null && resultType.IsInstanceOfType(value)
                ? value
                : resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
            return typeof(Task)
                .GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(resultType)
                .Invoke(null, [result]);
        }

        return returnType == typeof(void)
            ? null
            : returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }

    private static string[] ExpectedMissingButtonIds(string cardType, int playerId) =>
        cardType == "wom"
            ? [$"wommissing:reinstate:{playerId}", $"wommissing:remove:{playerId}"]
            : [$"missing:add:{playerId}", $"missing:remove:{playerId}"];

    private static void AssertActualButtons(FakeMessageState state, IReadOnlyCollection<string> expectedIds)
    {
        Assert.True(state.ComponentsWereSupplied);
        var buttons = ReadMessageButtons(state.Message!);
        Assert.Equal(expectedIds, buttons.Select(x => x.CustomId));
        Assert.All(buttons, button => Assert.False(button.IsDisabled));
    }

    private static IReadOnlyList<DiscordButtonState> ReadMessageButtons(object message)
    {
        var components = message.GetType().GetProperty("Components")!.GetValue(message) as IEnumerable;
        if (components is null) return [];

        return components
            .Cast<object>()
            .SelectMany(FlattenComponentTree)
            .ToArray();
    }

    private static IEnumerable<DiscordButtonState> FlattenComponentTree(object component)
    {
        var customIdProperty = component.GetType().GetProperty("CustomId");
        if (customIdProperty?.GetValue(component) is string customId && !string.IsNullOrWhiteSpace(customId))
        {
            var disabled = component.GetType().GetProperty("IsDisabled")?.GetValue(component) is bool value && value;
            yield return new DiscordButtonState(customId, disabled);
        }

        var childrenProperty = component.GetType().GetProperty("Components");
        if (childrenProperty?.GetValue(component) is not IEnumerable children) yield break;
        foreach (var child in children)
        {
            if (child is null) continue;
            foreach (var button in FlattenComponentTree(child)) yield return button;
        }
    }

    private static ulong ExtractMetadataUlong(string metadataJson, string propertyName)
    {
        using var document = JsonDocument.Parse(metadataJson);
        return document.RootElement.GetProperty(propertyName).GetUInt64();
    }

    private sealed record DiscordButtonState(string CustomId, bool IsDisabled);

    private static void CaptureComponentCollection(FakeMessageState state, object? messageComponent, bool supplied)
    {
        state.ComponentsWereSupplied = supplied;
        state.ComponentCollection = supplied
            ? messageComponent?.GetType().GetProperty("Components")?.GetValue(messageComponent)
            : null;
    }

    private static object? ReadOptionalValue(object? optional)
    {
        if (optional is null) return null;
        try
        {
            return optional.GetType().GetProperty("Value")?.GetValue(optional);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static object CreateComponent()
    {
        var userType = Type.GetType("Discord.WebSocket.SocketGlobalUser, Discord.Net.WebSocket")!;
        var messageType = Type.GetType("Discord.WebSocket.SocketUserMessage, Discord.Net.WebSocket")!;
        var componentType = Type.GetType("Discord.WebSocket.SocketMessageComponent, Discord.Net.WebSocket")!;
        var channelType = Type.GetType("Discord.WebSocket.ISocketMessageChannel, Discord.Net.WebSocket")!;
        var user = RuntimeHelpers.GetUninitializedObject(userType);
        SetField(user, "<Username>k__BackingField", "tester");
        SetField(user, "<Id>k__BackingField", 77UL);

        var channel = DispatchProxy.Create(channelType, typeof(SocketMessageChannelProxy));
        var message = RuntimeHelpers.GetUninitializedObject(messageType);
        SetField(message, "<Id>k__BackingField", 900UL);
        SetField(message, "<Channel>k__BackingField", channel);
        SetField(message, "<Author>k__BackingField", user);
        var embedType = Type.GetType("Discord.Embed, Discord.Net.Core")!;
        var componentInterfaceType = Type.GetType("Discord.IMessageComponent, Discord.Net.Core")!;
        var emptyEmbeds = typeof(ImmutableArray<>).MakeGenericType(embedType)
            .GetField("Empty", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
        SetField(message, "_embeds", emptyEmbeds);
        SetField(message, "<Components>k__BackingField", Array.CreateInstance(componentInterfaceType, 0));

        var component = RuntimeHelpers.GetUninitializedObject(componentType);
        SetField(component, "<Channel>k__BackingField", channel);
        SetField(component, "<User>k__BackingField", user);
        SetField(component, "<Message>k__BackingField", message);
        SetField(component, "<HasResponded>k__BackingField", true);
        return component;
    }

    private static void SetField(object target, string name, object value)
    {
        var fields = new List<FieldInfo>();
        for (var type = target.GetType(); type is not null; type = type.BaseType)
        {
            fields.AddRange(type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly));
        }

        var field = fields.FirstOrDefault(x => x.Name == name);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static TrackerDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<TrackerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TrackerDbContext(options);
    }

    private static LifecycleEvent CreateMergeEvent(
        int playerId,
        string eventType,
        string suggestedPrevious,
        int? suggestedPreviousPlayerId = null) => new()
    {
        PlayerId = playerId,
        EventType = eventType,
        MetadataJson = JsonUtil.Serialize(new
        {
            NewPlayer = "Lindal",
            SuggestedPrevious = suggestedPrevious,
            SuggestedPreviousPlayerId = suggestedPreviousPlayerId
        }),
        Status = "OPEN",
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
    };

    private class SocketMessageChannelProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_Id") return 321UL;
            return targetMethod is null || targetMethod.ReturnType == typeof(void)
                ? null
                : targetMethod.ReturnType.IsValueType
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
        }
    }

    private sealed class FakeMessageState
    {
        public object? Channel { get; set; }
        public object? Message { get; set; }
        public object? ComponentCollection { get; set; }
        public bool ComponentsWereSupplied { get; set; }
        public int GetCalls { get; set; }
        public int SendCalls { get; set; }
        public int ModifyCalls { get; set; }
        public int DeleteCalls { get; set; }
    }

    private class FakeMessageChannelProxy : DispatchProxy
    {
        public FakeMessageState State { get; set; } = null!;
        public ulong ChannelId { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (targetMethod.Name == "get_Id") return ChannelId;
            if (targetMethod.Name == "GetMessageAsync")
            {
                State.GetCalls++;
                return CreateTaskResult(targetMethod.ReturnType, State.Message);
            }
            if (targetMethod.Name == "SendMessageAsync")
            {
                State.SendCalls++;
                if (args is { Length: > 6 })
                {
                    CaptureComponentCollection(State, args[6], supplied: args[6] is not null);
                }
                return CreateTaskResult(targetMethod.ReturnType, State.Message);
            }
            if (targetMethod.Name == "DeleteMessageAsync")
            {
                State.DeleteCalls++;
                return Task.CompletedTask;
            }

            return CreateTaskResult(targetMethod.ReturnType, null);
        }
    }

    private class FakeUserMessageProxy : DispatchProxy
    {
        public FakeMessageState State { get; set; } = null!;
        public object? Channel { get; set; }
        public ulong MessageId { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (targetMethod.Name == "get_Id") return MessageId;
            if (targetMethod.Name == "get_Channel") return Channel;
            if (targetMethod.Name == "get_Embeds")
            {
                var embedType = Type.GetType("Discord.Embed, Discord.Net.Core")!;
                return Array.CreateInstance(embedType, 0);
            }
            if (targetMethod.Name == "get_Components")
            {
                var componentType = Type.GetType("Discord.IMessageComponent, Discord.Net.Core")!;
                if (State.ComponentCollection is object components)
                {
                    return components;
                }

                return Array.CreateInstance(componentType, 0);
            }
            if (targetMethod.Name == "ModifyAsync")
            {
                State.ModifyCalls++;
                if (args is { Length: > 0 } && args[0] is Delegate callback)
                {
                    var propertiesType = Type.GetType("Discord.MessageProperties, Discord.Net.Core")!;
                    var properties = Activator.CreateInstance(propertiesType)!;
                    callback.DynamicInvoke(properties);
                    var optionalComponents = propertiesType.GetProperty("Components")!.GetValue(properties);
                    var componentValue = ReadOptionalValue(optionalComponents);
                    CaptureComponentCollection(State, componentValue, componentValue is not null);
                }
                return Task.CompletedTask;
            }
            if (targetMethod.Name == "DeleteAsync")
            {
                State.DeleteCalls++;
                return Task.CompletedTask;
            }

            return CreateTaskResult(targetMethod.ReturnType, null);
        }
    }

    private sealed class RecordingHttpClientFactory : IHttpClientFactory
    {
        private readonly IReadOnlyCollection<string> templeRoster;

        public RecordingHttpClientFactory(IEnumerable<string>? templeRoster = null)
        {
            this.templeRoster = templeRoster?.ToArray() ?? [];
        }

        public int RequestCount { get; private set; }

        public HttpClient CreateClient(string name) => new(new RecordingHandler(this));

        private sealed class RecordingHandler(RecordingHttpClientFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.RequestCount++;
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                if (request.Method == HttpMethod.Get)
                {
                    response.Content = new StringContent(JsonSerializer.Serialize(owner.templeRoster));
                }
                return Task.FromResult(response);
            }
        }
    }

    private sealed class FakeWiseOldManClient : IWiseOldManClient
    {
        private readonly IReadOnlyDictionary<string, string?> roles;

        public FakeWiseOldManClient(IReadOnlyDictionary<string, string?>? roles = null)
        {
            this.roles = roles is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?>(roles, StringComparer.OrdinalIgnoreCase);
        }

        public Task<string?> GetMemberRoleAsync(string username, CancellationToken ct) =>
            Task.FromResult(roles.TryGetValue(username, out var role) ? role : "member");

        public Task<string?> GetMemberRoleFreshAsync(string username, CancellationToken ct) =>
            GetMemberRoleAsync(username, ct);

        public Task<IReadOnlyDictionary<string, string>> GetMemberRolesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(
                roles
                    .Where(x => !string.IsNullOrWhiteSpace(x.Value))
                    .ToDictionary(x => x.Key, x => x.Value!, StringComparer.OrdinalIgnoreCase));
        public Task<bool> IsImpAccountAsync(string username, CancellationToken ct) => Task.FromResult(false);
        public Task<WomRoleUpdateResult> UpdateMemberRoleAsync(string username, string role, CancellationToken ct, bool invalidateCache = true) =>
            Task.FromResult(new WomRoleUpdateResult(true, 200, "ok", role, null, username));
        public Task<(bool Success, string Details)> RemoveMemberAsync(string username, CancellationToken ct) =>
            Task.FromResult((true, "ok"));
        public Task InvalidateCacheAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
