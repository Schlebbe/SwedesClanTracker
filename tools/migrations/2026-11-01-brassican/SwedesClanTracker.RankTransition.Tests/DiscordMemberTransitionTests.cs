using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SwedesClanTracker.RankTransition;

namespace SwedesClanTracker.RankTransition.Tests;

public sealed class DiscordMemberIdentityResolverTests
{
    [Fact]
    public void Uses_compact_exact_identity_and_preserves_all_discord_display_fields()
    {
        var members = new[]
        {
            Member("u-1", "Arvld", "Arvld", "Ärvld")
        };

        var result = Assert.Single(DiscordMemberIdentityResolver.Resolve(
            [new DiscordPlayerIdentity(7, "ÄR-VLD")],
            members));

        Assert.Equal(DiscordMemberIdentityStatus.UniqueExact, result.Status);
        Assert.Equal("u-1", result.DiscordUserId);
        Assert.True(result.IsResolved);
    }

    [Fact]
    public void Ambiguous_or_missing_identity_requires_review_without_guessing()
    {
        var members = new[]
        {
            Member("u-1", "shared", null, null),
            Member("u-2", "other", null, "shared")
        };

        var results = DiscordMemberIdentityResolver.Resolve(
            [
                new DiscordPlayerIdentity(1, "shared"),
                new DiscordPlayerIdentity(2, "unknown")
            ],
            members);

        Assert.Equal(DiscordMemberIdentityStatus.Ambiguous, results[0].Status);
        Assert.Equal(new[] { "u-1", "u-2" }, results[0].CandidateUserIds);
        Assert.Equal(DiscordMemberIdentityStatus.Missing, results[1].Status);
        Assert.All(results, x => Assert.True(x.RequiresReview));
    }

    [Fact]
    public void Reviewed_override_resolves_ambiguous_identity_only_to_the_explicit_member()
    {
        var members = new[]
        {
            Member("u-1", "shared", null, null),
            Member("u-2", "other", null, "shared")
        };

        var result = Assert.Single(DiscordMemberIdentityResolver.Resolve(
            [new DiscordPlayerIdentity(42, "shared")],
            members,
            new Dictionary<int, string> { [42] = "u-2" }));

        Assert.Equal(DiscordMemberIdentityStatus.ReviewedOverride, result.Status);
        Assert.Equal("u-2", result.DiscordUserId);
        Assert.Equal(new[] { "u-2" }, result.CandidateUserIds);
    }

    [Fact]
    public void Leet_or_fuzzy_similarity_is_not_auto_applied()
    {
        var result = Assert.Single(DiscordMemberIdentityResolver.Resolve(
            [new DiscordPlayerIdentity(9, "Arv1d")],
            [Member("u-1", "Arvld", null, null)]));

        Assert.Equal(DiscordMemberIdentityStatus.Missing, result.Status);
        Assert.Null(result.DiscordUserId);
    }

    private static DiscordMemberObservation Member(
        string userId,
        string username,
        string? globalName,
        string? nick,
        params string[] roleIds) =>
        new(userId, username, globalName, nick, roleIds);
}

public sealed class DiscordRankMembershipPlannerTests
{
    [Fact]
    public void Groups_accounts_to_highest_confirmed_floor_and_plans_direct_role_replacement()
    {
        var member = new DiscordMemberObservation("u-1", "arvld", "Arvld", null, ["100", "900"]);
        var candidates = new[]
        {
            new DiscordRankMembershipCandidate(7, "Arvld", "u-1", "Brassican", "100", 1),
            new DiscordRankMembershipCandidate(8, "Arvld-alt", "u-1", "Commander", "200", 2)
        };

        var plan = Assert.Single(DiscordRankMembershipPlanner.BuildPlans(
            candidates,
            [member],
            ["100", "200", "300"],
            rankFloorByRoleId: new Dictionary<string, int>
            {
                ["100"] = 1,
                ["200"] = 2,
                ["300"] = 3
            }));

        Assert.Equal(new[] { 7, 8 }, plan.PlayerIds);
        Assert.Equal("200", plan.TargetRoleId);
        Assert.Equal(new[] { "100", "200", "300" }, plan.ManagedRankRoleIds);
        Assert.Equal(new[] { "100", "900" }, plan.OriginalRoleIds);
        Assert.Equal(new[] { "200", "900" }, plan.ExpectedAfterRoleIds);
        Assert.Equal(new[] { "100" }, plan.SupersededManagedRoleIds);
        Assert.Contains("900", plan.PreservedRoleIds);
        Assert.False(plan.RequiresReview);
        Assert.False(plan.IsNoOp);
    }

    [Fact]
    public void Existing_higher_rank_is_a_hold_and_is_never_downgraded()
    {
        var member = new DiscordMemberObservation("u-1", "member", null, null, ["200", "900"]);
        var candidate = new DiscordRankMembershipCandidate(7, "member", "u-1", "Brassican", "100", 1);

        var plan = Assert.Single(DiscordRankMembershipPlanner.BuildPlans(
            [candidate],
            [member],
            ["100", "200", "300"],
            rankFloorByRoleId: new Dictionary<string, int>
            {
                ["100"] = 1,
                ["200"] = 2,
                ["300"] = 3
            }));

        Assert.True(plan.RequiresReview);
        Assert.Empty(plan.SupersededManagedRoleIds);
        Assert.Equal(plan.OriginalRoleIds, plan.ExpectedAfterRoleIds);
        Assert.Contains("higher", plan.ReviewReason!, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class DiscordMemberRankGatewayTests
{
    [Fact]
    public async Task Adds_target_before_deleting_exact_superseded_role_and_preserves_unmanaged_roles()
    {
        var handler = new MemberHandler(["100", "900"]);
        var gateway = CreateGateway(handler);
        var request = Request(
            before: ["100", "900"],
            after: ["200", "900"],
            target: "200",
            intent: "intent-add-remove");

        var result = await gateway.ApplyRankMembershipAsync(request, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new[] { "200", "900" }, result.MemberAfter!.RoleIds);
        Assert.Equal(new[] { "200" }, result.AddedRoleIds);
        Assert.Equal(new[] { "100" }, result.RemovedRoleIds);
        Assert.Equal(
            new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Get },
            handler.Requests.Select(x => x.Method));
        Assert.Equal(
            "/api/v10/guilds/999/members/u-1/roles/200",
            handler.Requests[1].Uri.AbsolutePath);
        Assert.Equal(
            "/api/v10/guilds/999/members/u-1/roles/100",
            handler.Requests[2].Uri.AbsolutePath);
        Assert.All(handler.Requests.Where(x => x.Method == HttpMethod.Put || x.Method == HttpMethod.Delete), x => Assert.Equal("", x.Body));
    }

    [Fact]
    public async Task Fresh_role_drift_blocks_without_put_or_delete()
    {
        var handler = new MemberHandler(["300", "900"]);
        var gateway = CreateGateway(handler);
        var request = Request(
            before: ["100", "900"],
            after: ["200", "900"],
            target: "200",
            intent: "intent-drift");

        var result = await gateway.ApplyRankMembershipAsync(request, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(409, result.HttpStatus);
        Assert.DoesNotContain(handler.Requests, x => x.Method == HttpMethod.Put || x.Method == HttpMethod.Delete);
        Assert.Contains("drift", result.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Partial_delete_failure_is_owned_and_resume_only_deletes_remaining_role()
    {
        var handler = new MemberHandler(["100", "900"]) { FailFirstDelete = true };
        var gateway = CreateGateway(handler);
        var request = Request(
            before: ["100", "900"],
            after: ["200", "900"],
            target: "200",
            intent: "intent-partial");

        var first = await gateway.ApplyRankMembershipAsync(request, CancellationToken.None);

        Assert.False(first.Success);
        Assert.True(first.Partial);
        Assert.True(first.OwnedIntent);
        Assert.Equal(new[] { "100", "200", "900" }, first.MemberAfter!.RoleIds.OrderBy(x => x, StringComparer.Ordinal));

        var resumed = await gateway.ApplyRankMembershipAsync(request with { ResumeOwnedIntent = true }, CancellationToken.None);

        Assert.True(resumed.Success);
        Assert.Equal(new[] { "200", "900" }, resumed.MemberAfter!.RoleIds);
        Assert.Equal(1, handler.Requests.Count(x => x.Method == HttpMethod.Put));
        Assert.Equal(2, handler.Requests.Count(x => x.Method == HttpMethod.Delete));
    }

    [Fact]
    public async Task Already_target_is_verified_without_writes()
    {
        var handler = new MemberHandler(["200", "900"]);
        var gateway = CreateGateway(handler);
        var request = Request(
            before: ["100", "900"],
            after: ["200", "900"],
            target: "200",
            intent: "intent-existing");

        var result = await gateway.ApplyRankMembershipAsync(request, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.AlreadyApplied);
        Assert.Empty(result.AddedRoleIds);
        Assert.Empty(result.RemovedRoleIds);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Missing_configured_rank_scope_blocks_member_mutation_without_requests()
    {
        var handler = new MemberHandler(["100"]);
        var gateway = new DiscordRoleGateway(
            new HttpClient(handler),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DiscordBot:Token"] = "test-token",
                    ["DiscordBot:GuildId"] = "999"
                })
                .Build());

        var result = await gateway.ApplyRankMembershipAsync(
            Request(["100"], ["200"], "200", "intent-scope"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(403, result.HttpStatus);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Rollback_adds_frozen_before_role_then_removes_target()
    {
        var handler = new MemberHandler(["200", "900"]);
        var gateway = CreateGateway(handler);
        var request = Request(
            before: ["100", "900"],
            after: ["200", "900"],
            target: "200",
            intent: "intent-rollback").ForRollback();

        var result = await gateway.ApplyRankMembershipAsync(request, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new[] { "100", "900" }, result.MemberAfter!.RoleIds);
        Assert.Equal(
            new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Get },
            handler.Requests.Select(x => x.Method));
        Assert.EndsWith("/roles/100", handler.Requests[1].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith("/roles/200", handler.Requests[2].Uri.AbsolutePath, StringComparison.Ordinal);
    }

    private static DiscordRankMembershipMutationRequest Request(
        IReadOnlyList<string> before,
        IReadOnlyList<string> after,
        string target,
        string intent) =>
        new(
            "u-1",
            target,
            ["100", "200", "300"],
            before,
            after,
            DiscordMemberTransitionHelpers.ComputeRoleHash(before),
            DiscordMemberTransitionHelpers.ComputeRoleHash(after),
            intent);

    private static DiscordRoleGateway CreateGateway(MemberHandler handler) =>
        new(
            new HttpClient(handler),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DiscordBot:Token"] = "test-token",
                    ["DiscordBot:GuildId"] = "999",
                    ["DiscordBot:RankRoleIds:Brassican"] = "100",
                    ["DiscordBot:RankRoleIds:Commander"] = "200",
                    ["DiscordBot:RankRoleIds:Lieutenant"] = "300"
                })
                .Build());

    private sealed class MemberHandler(IReadOnlyList<string> initialRoles) : HttpMessageHandler
    {
        private readonly List<string> roles = initialRoles.ToList();
        public bool FailFirstDelete { get; init; }
        private bool failedDelete;
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, body));

            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/members/u-1", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            {
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    user = new { id = "u-1", username = "Arvld", global_name = "Arvld Global" },
                    nick = "Arvld nick",
                    roles
                }));
            }

            if (path.Contains("/roles/", StringComparison.Ordinal) && request.Method == HttpMethod.Put)
            {
                roles.Add(path[(path.LastIndexOf('/') + 1)..]);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path.Contains("/roles/", StringComparison.Ordinal) && request.Method == HttpMethod.Delete)
            {
                if (FailFirstDelete && !failedDelete)
                {
                    failedDelete = true;
                    return Json(HttpStatusCode.InternalServerError, "{}");
                }

                roles.Remove(path[(path.LastIndexOf('/') + 1)..]);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            throw new InvalidOperationException($"Unexpected request {request.Method} {path}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, string Body);
}
