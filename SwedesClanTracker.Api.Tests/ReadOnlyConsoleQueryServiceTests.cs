using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SwedesClanTracker.Api.Contracts;
using SwedesClanTracker.Api.Controllers;
using SwedesClanTracker.Api.Services;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.Api.Tests;

public sealed class ReadOnlyConsoleQueryServiceTests
{
    [Fact]
    public async Task Roster_supports_search_filter_sort_and_fifty_row_paging()
    {
        await using var db = CreateDatabase();
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 56; index++)
        {
            db.Players.Add(new Player
            {
                Username = index == 55 ? "Åskväder" : $"Member{index:00}",
                CurrentRank = index % 2 == 0 ? "Captain" : "Recruit",
                EligibleRank = "General",
                Status = index == 55 ? PlayerStatus.MISSING_PENDING_REVIEW : PlayerStatus.ACTIVE,
                LastSeen = now,
                LastSynced = now
            });
        }
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var firstPage = await service.GetRosterAsync(null, null, null, null, "username", 1, 50, CancellationToken.None);
        var filtered = await service.GetRosterAsync("Åsk", "MISSING_PENDING_REVIEW", null, null, "username", 1, 50, CancellationToken.None);

        Assert.Equal(56, firstPage.TotalCount);
        Assert.Equal(50, firstPage.Items.Count);
        Assert.Equal(2, firstPage.TotalPages);
        Assert.Single(filtered.Items);
        Assert.Equal("Åskväder", filtered.Items[0].Username);
    }

    [Fact]
    public async Task Profile_defaults_to_ninety_days_and_exposes_supported_snapshot_ranges()
    {
        await using var db = CreateDatabase();
        var now = DateTimeOffset.UtcNow;
        var player = new Player { Username = "Thorgrim", LastSeen = now, LastSynced = now, Status = PlayerStatus.ACTIVE };
        db.Players.Add(player);
        await db.SaveChangesAsync();
        db.PlayerSnapshots.AddRange(
            Snapshot(player.Id, now.AddDays(-100), 2000),
            Snapshot(player.Id, now.AddDays(-40), 2050),
            Snapshot(player.Id, now.AddDays(-10), 2100));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var ninety = await service.GetProfileAsync(player.Id, null, CancellationToken.None);
        var thirty = await service.GetProfileAsync(player.Id, "30d", CancellationToken.None);
        var all = await service.GetProfileAsync(player.Id, "all", CancellationToken.None);

        Assert.NotNull(ninety);
        Assert.Equal("90d", ninety!.TrendRange);
        Assert.Equal(2, ninety.Trend.Count);
        Assert.Single(thirty!.Trend);
        Assert.Equal(3, all!.Trend.Count);
        Assert.Equal(2100, ninety.LatestSnapshot!.TotalLevel);
    }

    [Fact]
    public async Task Review_projection_is_typed_factual_and_non_interactive()
    {
        await using var db = CreateDatabase();
        var now = DateTimeOffset.UtcNow;
        var player = new Player { Username = "Velkora", CurrentRank = "Captain", EligibleRank = "Captain", LastSeen = now, LastSynced = now, Status = PlayerStatus.MISSING_PENDING_REVIEW };
        db.Players.Add(player);
        await db.SaveChangesAsync();
        db.LifecycleEvents.Add(new LifecycleEvent
        {
            PlayerId = player.Id,
            EventType = "TEMPLE_MISSING_ACTION_REQUIRED",
            MetadataJson = JsonUtil.Serialize(new { player.Username, InTemple = false, InWom = true, ObservedAt = now, Source = "Discord" }),
            Status = "OPEN",
            CreatedAt = now
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var queue = await service.GetReviewQueueAsync("temple-missing", null, null, 1, 50, CancellationToken.None);
        var detail = await service.GetReviewDetailAsync(queue.Items.Single().CaseId, CancellationToken.None);

        Assert.Single(queue.Items);
        Assert.Equal("temple-missing", queue.Items[0].Family);
        Assert.NotNull(detail);
        Assert.Contains(detail!.Case.Actions, action => action.Label == "Add back to Temple and ensure WOM membership");
        Assert.All(detail.Case.Actions, action => Assert.False(action.Interactive));
        Assert.Null(detail.Case.CaseVersion);
        Assert.False(detail.CanExecute);
        Assert.Contains(detail.Case.Evidence, evidence => evidence.Label == "Temple membership" && evidence.Value == "False");
    }

    [Fact]
    public async Task Review_projection_derives_merge_precedence_and_suppression()
    {
        await using var db = CreateDatabase();
        var now = DateTimeOffset.UtcNow;
        var player = new Player { Username = "Stonehall", LastSeen = now, LastSynced = now, Status = PlayerStatus.MERGE_SUGGESTED };
        db.Players.Add(player);
        await db.SaveChangesAsync();
        db.LifecycleEvents.AddRange(
            new LifecycleEvent { PlayerId = player.Id, EventType = "MERGE_ACTION_REQUIRED", MetadataJson = JsonUtil.Serialize(new { player.Username, SuggestedPrevious = "Ironhold" }), Status = "OPEN", CreatedAt = now },
            new LifecycleEvent { PlayerId = player.Id, EventType = "TEMPLE_MISSING_ACTION_REQUIRED", MetadataJson = JsonUtil.Serialize(new { player.Username, InTemple = false }), Status = "OPEN", CreatedAt = now.AddMinutes(-2) });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var queue = await service.GetReviewQueueAsync(null, null, null, 1, 50, CancellationToken.None);
        var merge = Assert.Single(queue.Items, x => x.Family == "rename-merge");
        var temple = Assert.Single(queue.Items, x => x.Family == "temple-missing");

        Assert.Equal("open", merge.State);
        Assert.Equal("superseded", temple.State);
        Assert.Contains(merge.SupersededCases, related => related.CaseId == temple.CaseId);
    }

    [Fact]
    public async Task Overview_health_readiness_and_work_lanes_are_factual()
    {
        await using var db = CreateDatabase();
        var now = DateTimeOffset.UtcNow;
        var player = new Player { Username = "Thorgrim", LastSeen = now, LastSynced = now, Status = PlayerStatus.ACTIVE };
        db.Players.Add(player);
        await db.SaveChangesAsync();
        db.PromotionCandidates.Add(new PromotionCandidate { PlayerId = player.Id, Player = player, OldRank = "Captain", NewRank = "General", Reason = "Snapshot fact", Status = PromotionStatus.PENDING, CreatedAt = now });
        await db.SaveChangesAsync();
        var service = CreateService(db, new Dictionary<string, string?>
        {
            ["TempleOsrs:ApiKey"] = "configured-only-in-test",
            ["TempleOsrs:GroupId"] = "449",
            ["WiseOldMan:VerificationCode"] = "configured-only-in-test",
            ["WiseOldMan:GroupId"] = "7173"
        });

        var overview = await service.GetOverviewAsync(CancellationToken.None);

        Assert.Equal("configured", overview.Health.Readiness.State);
        Assert.Contains(overview.OpenWork, lane => lane.Lane == "promotion" && lane.Count == 1);
        Assert.DoesNotContain(overview.RecentChanges, change => change.Detail.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    private static PlayerSnapshot Snapshot(int playerId, DateTimeOffset timestamp, int totalLevel) => new()
    {
        PlayerId = playerId,
        Timestamp = timestamp,
        TotalLevel = totalLevel,
        Ehb = 500,
        Ehp = 600,
        Collections = 100,
        PetCount = 2
    };

    private static TrackerDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<TrackerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TrackerDbContext(options);
    }

    private static ReadOnlyConsoleQueryService CreateService(TrackerDbContext db, IReadOnlyDictionary<string, string?>? values = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values ?? new Dictionary<string, string?>()).Build();
        return new ReadOnlyConsoleQueryService(db, configuration);
    }
}

public sealed class AuthSessionControllerTests
{
    [Fact]
    public void Unauthenticated_session_returns_401()
    {
        var controller = new AuthSessionController
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) } }
        };

        var result = controller.GetSession();

        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    [Fact]
    public void Authenticated_session_returns_username_roles_and_admin_capability()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "Admin")],
            "Cookies");
        var controller = new AuthSessionController
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } }
        };

        var result = controller.GetSession();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var session = Assert.IsType<AuthSessionResponse>(ok.Value);
        Assert.Equal("admin", session.Username);
        Assert.Contains("Admin", session.Roles);
        Assert.True(session.IsAdmin);
    }
}

public sealed class ReadOnlyApiBoundaryTests
{
    [Fact]
    public void Read_only_console_controller_exposes_only_get_endpoints()
    {
        var methods = typeof(ReadOnlyConsoleController).GetMethods().Where(method => method.DeclaringType == typeof(ReadOnlyConsoleController));
        Assert.NotEmpty(methods);
        Assert.All(methods, method => Assert.NotNull(method.GetCustomAttributes(typeof(HttpGetAttribute), inherit: true).SingleOrDefault()));
        Assert.DoesNotContain(methods, method => method.GetCustomAttributes(true).Any(attribute => attribute is HttpPostAttribute or HttpPutAttribute or HttpDeleteAttribute or HttpPatchAttribute));
    }

    [Fact]
    public void Legacy_app_controller_keeps_its_existing_route_shape()
    {
        var route = typeof(AppController).GetCustomAttributes(typeof(RouteAttribute), true).Single();
        Assert.Equal("api/app", ((RouteAttribute)route).Template);
        Assert.Contains(typeof(AppController).GetMethods(), method => method.GetCustomAttributes(typeof(HttpGetAttribute), true).Any(attribute => ((HttpGetAttribute)attribute).Template == "home"));
    }
}
