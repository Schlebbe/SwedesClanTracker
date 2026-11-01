using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SwedesClanTracker.RankTransition;

namespace SwedesClanTracker.RankTransition.Tests;

public sealed class DiscordRoleGatewayTests
{
    [Fact]
    public async Task Observation_reads_lowercase_payloads_paginates_and_hashes_exact_member_ids()
    {
        var handler = new DiscordHandler { UsePaging = true };
        var gateway = CreateGateway(handler);

        var guild = await gateway.GetGuildObservationAsync(CancellationToken.None);

        Assert.NotNull(guild);
        Assert.True(guild.MembersFullyObserved);
        var role = Assert.Single(guild.Roles, x => x.RoleId == "10");
        var expectedMemberIds = new[] { "u-0001", "u-0002", "u-1001", "u-1002" };
        Assert.Equal("Officer", role.NameBefore);
        Assert.Equal("8", role.Permissions);
        Assert.Equal(4, role.Position);
        Assert.Equal(expectedMemberIds, role.MemberIds);
        Assert.Equal(expectedMemberIds.Length, role.MembershipCount);
        Assert.Equal(HashMembers(expectedMemberIds), role.MembershipHash);
        Assert.False(role.Protected);

        Assert.NotNull(guild.Members);
        var observedMember = Assert.Single(guild.Members!, x => x.UserId == "u-1001");
        Assert.Equal("player1001", observedMember.Username);
        Assert.Equal("Player 1001", observedMember.GlobalName);
        Assert.Equal("Nick 1001", observedMember.Nick);
        Assert.Equal(new[] { "10" }, observedMember.RoleIds);
        Assert.Equal(1002, guild.Members!.Count);
        Assert.False(string.IsNullOrWhiteSpace(guild.MemberHash));

        var everyone = Assert.Single(guild.Roles, x => x.RoleId == "99");
        Assert.True(everyone.Protected);
        Assert.Equal("@everyone role; protected.", everyone.Notes);

        Assert.Equal(1, handler.Requests.Count(x => x.Method == HttpMethod.Get && x.Uri.AbsolutePath.EndsWith("/roles", StringComparison.Ordinal)));
        var memberRequests = handler.Requests
            .Where(x => x.Method == HttpMethod.Get && x.Uri.AbsolutePath.EndsWith("/members", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, memberRequests.Length);
        Assert.Contains("after=u-1000", memberRequests[1].Uri.Query, StringComparison.Ordinal);
        Assert.All(handler.Requests, request => Assert.Equal("test-token", request.AuthorizationParameter));
        Assert.DoesNotContain(handler.Requests, x => x.Method == HttpMethod.Patch);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Role_observation_unavailable_throws_without_attempting_other_requests(HttpStatusCode statusCode)
    {
        var handler = new DiscordHandler { RolesStatusCode = statusCode };
        var gateway = CreateGateway(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => gateway.GetGuildObservationAsync(CancellationToken.None));

        Assert.Contains(((int)statusCode).ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Incomplete_member_observation_is_not_treated_as_empty_membership(HttpStatusCode statusCode)
    {
        var handler = new DiscordHandler { MembersStatusCode = statusCode };
        var gateway = CreateGateway(handler);

        var guild = await gateway.GetGuildObservationAsync(CancellationToken.None);

        Assert.NotNull(guild);
        Assert.False(guild.MembersFullyObserved);
        Assert.Contains(((int)statusCode).ToString(), guild.Notes, StringComparison.Ordinal);
        var role = Assert.Single(guild.Roles, x => x.RoleId == "10");
        Assert.Empty(role.MemberIds!);

        var rename = await gateway.RenameRoleAsync("10", "Officer", "Brassican", CancellationToken.None);

        Assert.False(rename.Success);
        Assert.Equal(503, rename.HttpStatus);
        Assert.DoesNotContain(handler.Requests, x => x.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task Rename_uses_patch_name_only_and_preserves_observed_role_state()
    {
        var handler = new DiscordHandler { MinimalPatchResponse = true };
        var gateway = CreateGateway(handler);

        var result = await gateway.RenameRoleAsync("10", "Officer", "Brassican", CancellationToken.None);

        Assert.True(result.Success);
        var patch = Assert.Single(handler.Requests, x => x.Method == HttpMethod.Patch);
        Assert.Equal("/api/v10/guilds/99/roles/10", patch.Uri.AbsolutePath);
        Assert.Equal("Bot test-token", patch.AuthorizationHeader);
        Assert.Equal("{\"name\":\"Brassican\"}", patch.Body);
        Assert.DoesNotContain("permissions", patch.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("position", patch.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("icon", patch.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(handler.Requests, x => x.Method == HttpMethod.Put);

        Assert.NotNull(result.RoleAfter);
        Assert.Equal("Brassican", result.RoleAfter!.NameBefore);
        Assert.Equal("8", result.RoleAfter.Permissions);
        Assert.Equal(4, result.RoleAfter.Position);
        Assert.Equal(new[] { "u-1", "u-2" }, result.RoleAfter.MemberIds);
        Assert.Equal(HashMembers(["u-1", "u-2"]), result.RoleAfter.MembershipHash);
    }

    [Theory]
    [InlineData("SomeoneElse")]
    [InlineData("officer")]
    public async Task Rename_fails_closed_when_fresh_expected_name_does_not_match_exactly(string currentName)
    {
        var handler = new DiscordHandler { RoleName = currentName };
        var gateway = CreateGateway(handler);

        var result = await gateway.RenameRoleAsync("10", "Officer", "Brassican", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(409, result.HttpStatus);
        Assert.DoesNotContain(handler.Requests, x => x.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task Rename_requires_an_existing_role_id_in_the_fresh_guild_observation()
    {
        var handler = new DiscordHandler();
        var gateway = CreateGateway(handler, new Dictionary<string, string?>
        {
            ["Officer"] = "11"
        });

        var result = await gateway.RenameRoleAsync("11", "Officer", "Brassican", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(404, result.HttpStatus);
        Assert.DoesNotContain(handler.Requests, x => x.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task Rename_rejects_role_ids_outside_configured_rank_role_scope()
    {
        var handler = new DiscordHandler();
        var gateway = CreateGateway(handler, new Dictionary<string, string?>
        {
            ["Officer"] = "11"
        });

        var result = await gateway.RenameRoleAsync("10", "Officer", "Brassican", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(403, result.HttpStatus);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Rename_fails_closed_when_rank_role_scope_is_missing()
    {
        var handler = new DiscordHandler();
        var gateway = CreateGateway(handler, includeDefaultRoleIds: false);

        var result = await gateway.RenameRoleAsync("10", "Officer", "Brassican", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(403, result.HttpStatus);
        Assert.Contains("not configured", result.Details, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Rename_refuses_everyone_role_by_guild_id()
    {
        var handler = new DiscordHandler();
        var gateway = CreateGateway(handler, new Dictionary<string, string?>
        {
            ["Officer"] = "99"
        });

        var result = await gateway.RenameRoleAsync("99", "@everyone", "Colonel", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(403, result.HttpStatus);
        Assert.DoesNotContain(handler.Requests, x => x.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task Rename_rejects_a_response_that_changes_permissions()
    {
        var handler = new DiscordHandler { PatchPermissions = "16" };
        var gateway = CreateGateway(handler);

        var result = await gateway.RenameRoleAsync("10", "Officer", "Brassican", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(502, result.HttpStatus);
        Assert.Contains("permissions", result.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_configuration_returns_without_external_requests()
    {
        var handler = new DiscordHandler();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var gateway = new DiscordRoleGateway(new HttpClient(handler), configuration);

        var observation = await gateway.GetGuildObservationAsync(CancellationToken.None);
        var rename = await gateway.RenameRoleAsync("10", "Officer", "Brassican", CancellationToken.None);

        Assert.Null(observation);
        Assert.False(rename.Success);
        Assert.Empty(handler.Requests);
    }

    private static DiscordRoleGateway CreateGateway(
        DiscordHandler handler,
        IReadOnlyDictionary<string, string?>? rankRoleIds = null,
        bool includeDefaultRoleIds = true)
    {
        var values = new Dictionary<string, string?>
        {
            ["DiscordBot:Token"] = "test-token",
            ["DiscordBot:GuildId"] = "99"
        };
        if (includeDefaultRoleIds && rankRoleIds is null)
        {
            rankRoleIds = new Dictionary<string, string?>
            {
                ["Officer"] = "10",
                ["Astral"] = "20",
                ["General"] = "30"
            };
        }
        if (rankRoleIds is not null)
        {
            foreach (var (rank, roleId) in rankRoleIds)
            {
                values[$"DiscordBot:RankRoleIds:{rank}"] = roleId;
            }
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        return new DiscordRoleGateway(new HttpClient(handler), configuration);
    }

    private static string HashMembers(IEnumerable<string> memberIds) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", memberIds)))).ToLowerInvariant();

    private sealed class DiscordHandler : HttpMessageHandler
    {
        public string RoleName { get; set; } = "Officer";
        public bool UsePaging { get; init; }
        public bool MinimalPatchResponse { get; init; }
        public string? PatchPermissions { get; init; }
        public HttpStatusCode? RolesStatusCode { get; init; }
        public HttpStatusCode? MembersStatusCode { get; init; }
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!,
                body,
                request.Headers.Authorization?.ToString(),
                request.Headers.Authorization?.Parameter));

            if (request.Method == HttpMethod.Patch)
            {
                using var document = JsonDocument.Parse(body);
                RoleName = document.RootElement.GetProperty("name").GetString()!;
                var patchPermissions = PatchPermissions ?? "8";
                var patchJson = MinimalPatchResponse
                    ? $"{{\"id\":\"10\",\"name\":{JsonSerializer.Serialize(RoleName)}}}"
                    : $"{{\"id\":\"10\",\"name\":{JsonSerializer.Serialize(RoleName)},\"permissions\":{JsonSerializer.Serialize(patchPermissions)},\"position\":4,\"managed\":false}}";
                return JsonResponse(HttpStatusCode.OK, patchJson);
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/roles", StringComparison.Ordinal))
            {
                if (RolesStatusCode is { } roleStatus)
                {
                    return ErrorResponse(roleStatus);
                }

                var roleName = JsonSerializer.Serialize(RoleName);
                return JsonResponse(
                    HttpStatusCode.OK,
                    $"[{{\"id\":\"99\",\"name\":\"@everyone\",\"permissions\":\"0\",\"position\":0,\"managed\":false}},{{\"id\":\"10\",\"name\":{roleName},\"permissions\":\"8\",\"position\":4,\"managed\":false}}]");
            }

            if (MembersStatusCode is { } memberStatus)
            {
                return ErrorResponse(memberStatus, includeRetryAfter: true);
            }

            var after = GetQueryValue(request.RequestUri, "after");
            return JsonResponse(HttpStatusCode.OK, BuildMembersJson(after));
        }

        private string BuildMembersJson(string? after)
        {
            if (!UsePaging)
            {
                return "[{\"user\":{\"id\":\"u-1\"},\"roles\":[\"10\"]},{\"user\":{\"id\":\"u-2\"},\"roles\":[\"10\"]}]";
            }

            if (string.Equals(after, "0", StringComparison.Ordinal))
            {
                var firstPage = Enumerable.Range(1, 1000)
                    .Select(index => new
                    {
                        user = new { id = $"u-{index:0000}", username = $"player{index}", global_name = $"Player {index}" },
                        nick = $"Nick {index}",
                        roles = index <= 2 ? new[] { "10" } : Array.Empty<string>()
                    });
                return JsonSerializer.Serialize(firstPage);
            }

            if (string.Equals(after, "u-1000", StringComparison.Ordinal))
            {
                var secondPage = Enumerable.Range(1001, 2)
                    .Select(index => new
                    {
                        user = new { id = $"u-{index:0000}", username = $"player{index}", global_name = $"Player {index}" },
                        nick = $"Nick {index}",
                        roles = new[] { "10" }
                    });
                return JsonSerializer.Serialize(secondPage);
            }

            throw new InvalidOperationException($"Unexpected member page cursor '{after}'.");
        }

        private static string? GetQueryValue(Uri uri, string name)
        {
            var pair = uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(x => x.StartsWith($"{name}=", StringComparison.Ordinal));
            return pair is null ? null : Uri.UnescapeDataString(pair[(name.Length + 1)..]);
        }

        private static HttpResponseMessage ErrorResponse(HttpStatusCode statusCode, bool includeRetryAfter = false)
        {
            var response = JsonResponse(statusCode, "{}");
            if (includeRetryAfter)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
            }

            return response;
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        string Body,
        string? AuthorizationHeader,
        string? AuthorizationParameter);
}
