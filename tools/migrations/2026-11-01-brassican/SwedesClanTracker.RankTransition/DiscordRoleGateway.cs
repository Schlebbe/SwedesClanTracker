using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace SwedesClanTracker.RankTransition;

public interface IDiscordRoleGateway
{
    Task<DiscordGuildObservation?> GetGuildObservationAsync(CancellationToken ct);
    Task<DiscordRoleMutationResult> RenameRoleAsync(
        string roleId,
        string expectedName,
        string targetName,
        CancellationToken ct);

    // Default implementations keep existing read-only planner/test doubles
    // source-compatible until the transition applier starts consuming the
    // member mutation path.
    Task<DiscordMemberObservation?> GetMemberObservationAsync(string userId, CancellationToken ct) =>
        throw new NotSupportedException("This Discord gateway does not expose member observations.");

    Task<DiscordRankMembershipMutationResult> ApplyRankMembershipAsync(
        DiscordRankMembershipMutationRequest request,
        CancellationToken ct) =>
        throw new NotSupportedException("This Discord gateway does not expose rank membership mutations.");
}

public sealed record DiscordGuildObservation(
    string GuildId,
    IReadOnlyList<DiscordRoleObservation> Roles,
    bool MembersFullyObserved,
    string? Notes,
    IReadOnlyList<DiscordMemberObservation>? Members = null,
    string? MemberHash = null);

public sealed record DiscordRoleMutationResult(
    bool Success,
    int HttpStatus,
    string Details,
    DiscordRoleObservation? RoleAfter);

/// <summary>
/// Small REST-only gateway. It never connects a Discord client or starts a
/// worker. Planning uses only GET requests; the only write is the explicit
/// role-name PATCH and explicitly planned rank-membership writes used by
/// apply/rollback.
/// </summary>
public sealed class DiscordRoleGateway(HttpClient httpClient, IConfiguration configuration) : IDiscordRoleGateway
{
    private const string ApiBase = "https://discord.com/api/v10";
    private const int MemberPageSize = 1000;
    private const int MaxMemberPages = 100;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<DiscordGuildObservation?> GetGuildObservationAsync(CancellationToken ct)
    {
        var (guildId, token) = ReadConfiguration();
        if (guildId == 0 || string.IsNullOrWhiteSpace(token)) return null;

        using var rolesRequest = CreateRequest(HttpMethod.Get, $"{ApiBase}/guilds/{guildId}/roles", token);
        using var rolesResponse = await httpClient.SendAsync(rolesRequest, ct);
        var rolesJson = await ReadSuccessfulResponseAsync(
            rolesResponse,
            "Discord role observation",
            ct);

        var roles = JsonSerializer.Deserialize<List<DiscordRoleWire>>(rolesJson, JsonOptions) ?? [];

        var roleMembers = roles.ToDictionary(
            x => x.Id,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var membersFullyObserved = true;
        string? memberNotes = null;
        var memberObservations = new List<DiscordMemberObservation>();
        var seenMemberIds = new HashSet<string>(StringComparer.Ordinal);
        var after = "0";
        var pageCount = 0;
        var lastPageWasFull = false;
        while (pageCount < MaxMemberPages)
        {
            pageCount++;
            using var membersRequest = CreateRequest(
                HttpMethod.Get,
                $"{ApiBase}/guilds/{guildId}/members?limit={MemberPageSize}&after={Uri.EscapeDataString(after)}",
                token);
            using var membersResponse = await httpClient.SendAsync(membersRequest, ct);
            if (!membersResponse.IsSuccessStatusCode)
            {
                membersFullyObserved = false;
                memberNotes = $"Discord members could not be fully observed ({(int)membersResponse.StatusCode}); role metadata was still captured.";
                break;
            }

            var membersJson = await membersResponse.Content.ReadAsStringAsync(ct);
            var members = JsonSerializer.Deserialize<List<DiscordMemberWire>>(membersJson, JsonOptions) ?? [];
            foreach (var member in members)
            {
                if (string.IsNullOrWhiteSpace(member.User?.Id))
                {
                    membersFullyObserved = false;
                    memberNotes ??= "Discord member observation contained a member without a stable user ID.";
                    continue;
                }

                var memberObservation = CreateMemberObservation(member);
                if (!seenMemberIds.Add(memberObservation.UserId))
                {
                    membersFullyObserved = false;
                    memberNotes ??= "Discord member pagination returned a duplicate user ID; the member snapshot is not complete.";
                }
                else
                {
                    memberObservations.Add(memberObservation);
                }

                foreach (var roleId in member.Roles ?? [])
                {
                    if (roleMembers.TryGetValue(roleId, out var membersForRole))
                    {
                        membersForRole.Add(member.User.Id);
                    }
                }
            }

            lastPageWasFull = members.Count == MemberPageSize;
            if (members.Count < MemberPageSize) break;
            var nextId = members[^1].User?.Id;
            if (string.IsNullOrWhiteSpace(nextId) || string.Equals(after, nextId, StringComparison.Ordinal))
            {
                membersFullyObserved = false;
                memberNotes = "Discord member pagination stopped without a stable cursor.";
                break;
            }
            after = nextId;
        }

        if (pageCount >= MaxMemberPages && lastPageWasFull && membersFullyObserved)
        {
            membersFullyObserved = false;
            memberNotes = "Discord member pagination reached its safety limit.";
        }

        var observations = roles
            .Select(role =>
            {
                var members = roleMembers.GetValueOrDefault(role.Id)?
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray() ?? [];
                var memberHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", members)))).ToLowerInvariant();
                var everyone = string.Equals(role.Id, guildId.ToString(), StringComparison.Ordinal);
                var managed = role.Managed == true;
                return new DiscordRoleObservation(
                    role.Id,
                    role.Name ?? "",
                    null,
                    role.Permissions ?? "0",
                    role.Position ?? 0,
                    members.Length,
                    Protected: managed || everyone,
                    Notes: managed
                        ? "Managed integration role; protected."
                        : everyone
                            ? "@everyone role; protected."
                            : null,
                    MemberIds: members,
                    MembershipHash: memberHash);
            })
            .ToArray();

        var orderedMembers = memberObservations
            .OrderBy(x => x.UserId, StringComparer.Ordinal)
            .ToArray();
        var memberHash = DiscordMemberTransitionHelpers.ComputeMemberHash(orderedMembers);
        return new DiscordGuildObservation(guildId.ToString(), observations, membersFullyObserved, memberNotes, orderedMembers, memberHash);
    }

    public async Task<DiscordMemberObservation?> GetMemberObservationAsync(
        string userId,
        CancellationToken ct)
    {
        var (guildId, token) = ReadConfiguration();
        if (guildId == 0 || string.IsNullOrWhiteSpace(token)) return null;
        if (string.IsNullOrWhiteSpace(userId)) return null;

        using var request = CreateRequest(
            HttpMethod.Get,
            $"{ApiBase}/guilds/{guildId}/members/{Uri.EscapeDataString(userId.Trim())}",
            token);
        using var response = await httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        var json = await ReadSuccessfulResponseAsync(response, "Discord member observation", ct);
        var member = JsonSerializer.Deserialize<DiscordMemberWire>(json, JsonOptions)
            ?? throw new InvalidDataException("Discord returned an empty member observation.");
        if (string.IsNullOrWhiteSpace(member.User?.Id))
        {
            throw new InvalidDataException("Discord member observation did not contain a stable user ID.");
        }

        return CreateMemberObservation(member);
    }

    public async Task<DiscordRoleMutationResult> RenameRoleAsync(
        string roleId,
        string expectedName,
        string targetName,
        CancellationToken ct)
    {
        var (guildId, token) = ReadConfiguration();
        if (guildId == 0 || string.IsNullOrWhiteSpace(token))
        {
            return new DiscordRoleMutationResult(false, 0, "Discord settings are missing.", null);
        }

        if (!ulong.TryParse(roleId, out _) || string.IsNullOrWhiteSpace(expectedName) || string.IsNullOrWhiteSpace(targetName))
        {
            return new DiscordRoleMutationResult(false, 400, "Discord role ID and names must be present and valid.", null);
        }

        var configuredRoleIds = ReadConfiguredRoleIds();
        if (configuredRoleIds.Count == 0)
        {
            return new DiscordRoleMutationResult(
                false,
                403,
                "Discord rank-role IDs are not configured; refusing to rename an unscoped role.",
                null);
        }
        if (!configuredRoleIds.Contains(roleId))
        {
            return new DiscordRoleMutationResult(false, 403, "Discord role ID is outside the configured rank-role scope.", null);
        }

        var observation = await GetGuildObservationAsync(ct);
        if (observation is null)
        {
            return new DiscordRoleMutationResult(false, 503, "Discord guild observation is unavailable.", null);
        }

        if (!observation.MembersFullyObserved)
        {
            return new DiscordRoleMutationResult(
                false,
                503,
                $"Discord member observation was incomplete; refusing to rename a role. {observation.Notes}",
                null);
        }

        var roleBefore = observation.Roles.FirstOrDefault(x => string.Equals(x.RoleId, roleId, StringComparison.Ordinal));
        if (roleBefore is null)
        {
            return new DiscordRoleMutationResult(false, 404, "Discord role was not found during the pre-write drift check.", null);
        }
        if (!string.Equals(roleBefore.NameBefore, expectedName, StringComparison.Ordinal))
        {
            return new DiscordRoleMutationResult(false, 409, "Discord role changed since planning; refusing to overwrite drift.", roleBefore);
        }
        if (roleBefore.Protected)
        {
            return new DiscordRoleMutationResult(false, 403, "Discord role is managed or @everyone; refusing to rename a protected role.", roleBefore);
        }

        // Discord's Modify Guild Role endpoint is PATCH. Sending only `name`
        // preserves permissions, hierarchy position, icon, and unicode emoji.
        var body = JsonSerializer.Serialize(new { name = targetName });
        using var request = CreateRequest(HttpMethod.Patch, $"{ApiBase}/guilds/{guildId}/roles/{roleId}", token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            return new DiscordRoleMutationResult(
                false,
                (int)response.StatusCode,
                $"Discord role rename failed ({(int)response.StatusCode}).",
                null);
        }

        DiscordRoleObservation? after = roleBefore with { NameBefore = targetName };
        try
        {
            var role = JsonSerializer.Deserialize<DiscordRoleWire>(responseText, JsonOptions);
            if (role is not null && !string.IsNullOrWhiteSpace(role.Id))
            {
                if (!string.Equals(role.Id, roleId, StringComparison.Ordinal))
                {
                    return new DiscordRoleMutationResult(false, 502, "Discord returned a different role ID after rename.", roleBefore);
                }

                var returnedName = role.Name ?? targetName;
                if (!string.Equals(returnedName, targetName, StringComparison.Ordinal))
                {
                    return new DiscordRoleMutationResult(false, 502, "Discord returned an unexpected role name after rename.", roleBefore);
                }

                if ((role.Permissions is not null && !string.Equals(role.Permissions, roleBefore.Permissions, StringComparison.Ordinal)) ||
                    (role.Position.HasValue && role.Position.Value != roleBefore.Position) ||
                    (role.Managed.HasValue && role.Managed.Value))
                {
                    return new DiscordRoleMutationResult(false, 502, "Discord role permissions, position, or management state changed unexpectedly.", roleBefore);
                }

                after = roleBefore with
                {
                    NameBefore = returnedName
                };
            }
        }
        catch (JsonException)
        {
            // Discord acknowledged the write; the next GET verifies the state.
        }

        return new DiscordRoleMutationResult(true, (int)response.StatusCode, "Discord role rename accepted.", after);
    }

    public async Task<DiscordRankMembershipMutationResult> ApplyRankMembershipAsync(
        DiscordRankMembershipMutationRequest request,
        CancellationToken ct)
    {
        var (guildId, token) = ReadConfiguration();
        if (guildId == 0 || string.IsNullOrWhiteSpace(token))
        {
            return MembershipFailure(0, "Discord settings are missing.", request);
        }

        var configuredRoleIds = ReadConfiguredRoleIds();
        if (configuredRoleIds.Count == 0)
        {
            return MembershipFailure(403, "Discord rank-role IDs are not configured; refusing an unscoped member mutation.", request);
        }
        if (request.ManagedRankRoleIds.Any(roleId => !configuredRoleIds.Contains(roleId)) ||
            (!string.IsNullOrWhiteSpace(request.TargetRoleId) && !configuredRoleIds.Contains(request.TargetRoleId)))
        {
            return MembershipFailure(403, "The requested member mutation contains a role ID outside the configured rank-role scope.", request);
        }

        var validation = ValidateMembershipRequest(request);
        if (validation is not null)
        {
            return MembershipFailure(400, validation, request);
        }

        DiscordMemberObservation? memberBefore;
        try
        {
            memberBefore = await GetMemberObservationAsync(request.DiscordUserId, ct);
        }
        catch (HttpRequestException ex)
        {
            return MembershipFailure(HttpStatus(ex), $"Discord member preflight failed: {ex.Message}", request);
        }
        catch (InvalidDataException ex)
        {
            return MembershipFailure(502, ex.Message, request);
        }

        if (memberBefore is null)
        {
            return MembershipFailure(404, "Discord member was not found during the fresh pre-write guard.", request);
        }
        if (!string.Equals(memberBefore.UserId, request.DiscordUserId, StringComparison.Ordinal))
        {
            return MembershipFailure(409, "Discord returned a different member ID during the pre-write guard.", request, memberBefore);
        }
        if (!string.IsNullOrWhiteSpace(request.ExpectedIdentityHash) &&
            !string.Equals(memberBefore.EffectiveIdentityHash, request.ExpectedIdentityHash, StringComparison.OrdinalIgnoreCase))
        {
            return MembershipFailure(409, "Discord member identity changed since the reviewed member observation; refusing the mutation.", request, memberBefore);
        }

        var beforeRoles = DiscordMemberTransitionHelpers.CanonicalRoleIds(memberBefore.RoleIds);
        var frozenBefore = DiscordMemberTransitionHelpers.CanonicalRoleIds(request.ExpectedBeforeRoleIds);
        var frozenAfter = DiscordMemberTransitionHelpers.CanonicalRoleIds(request.ExpectedAfterRoleIds);
        var isRollback = string.IsNullOrWhiteSpace(request.TargetRoleId);
        var expectedCurrentHash = DiscordMemberTransitionHelpers.ComputeRoleHash(frozenBefore);
        if (!string.Equals(expectedCurrentHash, request.ExpectedBeforeRoleHash, StringComparison.OrdinalIgnoreCase))
        {
            return MembershipFailure(400, "The frozen before role hash does not match the frozen before role set.", request, memberBefore);
        }
        var expectedAfterHash = DiscordMemberTransitionHelpers.ComputeRoleHash(frozenAfter);
        if (!string.Equals(expectedAfterHash, request.ExpectedAfterRoleHash, StringComparison.OrdinalIgnoreCase))
        {
            return MembershipFailure(400, "The frozen after role hash does not match the frozen after role set.", request, memberBefore);
        }

        var exactBefore = beforeRoles.SequenceEqual(frozenBefore, StringComparer.Ordinal);
        var exactAfter = beforeRoles.SequenceEqual(frozenAfter, StringComparer.Ordinal);
        if (exactBefore && !string.Equals(memberBefore.EffectiveRoleHash, request.ExpectedBeforeRoleHash, StringComparison.OrdinalIgnoreCase))
        {
            return MembershipFailure(
                409,
                "Discord member role hash did not match the frozen before hash; refusing the mutation.",
                request,
                memberBefore);
        }
        if (exactAfter)
        {
            return MembershipSuccess(
                200,
                "Discord member already has the frozen target role set; no mutation was issued.",
                request,
                memberBefore,
                memberBefore,
                [],
                [],
                alreadyApplied: true);
        }

        if (!exactBefore && !IsOwnedPartialState(beforeRoles, frozenBefore, frozenAfter, request.ManagedRankRoleIds, isRollback))
        {
            return MembershipFailure(
                409,
                "Discord member role membership drifted from both frozen states; refusing to guess or strip roles.",
                request,
                memberBefore);
        }

        if (!exactBefore && !request.ResumeOwnedIntent)
        {
            return MembershipFailure(
                409,
                "Discord member is in a partial rank-change state without a caller-owned resume intent.",
                request,
                memberBefore);
        }
        if (!exactBefore && string.IsNullOrWhiteSpace(request.OwnedIntentId))
        {
            return MembershipFailure(
                409,
                "A partial Discord rank change may only be resumed with a non-empty owned intent.",
                request,
                memberBefore);
        }

        var managed = request.ManagedRankRoleIds.ToHashSet(StringComparer.Ordinal);
        var desired = frozenAfter.ToHashSet(StringComparer.Ordinal);
        var current = beforeRoles.ToHashSet(StringComparer.Ordinal);
        var rolesToAdd = desired
            .Except(current, StringComparer.Ordinal)
            .Where(managed.Contains)
            .OrderBy(x => string.Equals(x, request.TargetRoleId, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(x => x, StringComparer.Ordinal)
            .ToArray();
        var rolesToRemove = current
            .Except(desired, StringComparer.Ordinal)
            .Where(managed.Contains)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var added = new List<string>();
        var removed = new List<string>();
        foreach (var roleId in rolesToAdd)
        {
            var update = await SendMemberRoleMutationAsync(
                guildId,
                token,
                request.DiscordUserId,
                roleId,
                HttpMethod.Put,
                ct);
            if (!update.Success)
            {
                return await PartialMembershipFailureAsync(
                    update.HttpStatus,
                    update.Details,
                    request,
                    memberBefore,
                    added,
                    removed,
                    ct);
            }
            added.Add(roleId);
        }

        foreach (var roleId in rolesToRemove)
        {
            var update = await SendMemberRoleMutationAsync(
                guildId,
                token,
                request.DiscordUserId,
                roleId,
                HttpMethod.Delete,
                ct);
            if (!update.Success)
            {
                return await PartialMembershipFailureAsync(
                    update.HttpStatus,
                    update.Details,
                    request,
                    memberBefore,
                    added,
                    removed,
                    ct);
            }
            removed.Add(roleId);
        }

        DiscordMemberObservation? memberAfter;
        try
        {
            memberAfter = await GetMemberObservationAsync(request.DiscordUserId, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException)
        {
            return new DiscordRankMembershipMutationResult(
                false,
                HttpStatus(ex),
                $"Discord rank membership changed but after-write verification failed: {ex.Message}",
                memberBefore,
                null,
                added,
                removed,
                false,
                true,
                true,
                request.OwnedIntentId);
        }

        if (memberAfter is null)
        {
            return new DiscordRankMembershipMutationResult(
                false,
                404,
                "Discord member disappeared during after-write verification; the owned intent remains for review.",
                memberBefore,
                null,
                added,
                removed,
                false,
                true,
                true,
                request.OwnedIntentId);
        }

        var afterRoles = DiscordMemberTransitionHelpers.CanonicalRoleIds(memberAfter.RoleIds);
        if (!afterRoles.SequenceEqual(frozenAfter, StringComparer.Ordinal) ||
            !string.Equals(memberAfter.EffectiveRoleHash, request.ExpectedAfterRoleHash, StringComparison.OrdinalIgnoreCase))
        {
            return new DiscordRankMembershipMutationResult(
                false,
                409,
                "Discord after-write member role set did not match the frozen target; no further roles were changed.",
                memberBefore,
                memberAfter,
                added,
                removed,
                false,
                true,
                true,
                request.OwnedIntentId);
        }
        if (!string.Equals(memberBefore.EffectiveIdentityHash, memberAfter.EffectiveIdentityHash, StringComparison.Ordinal))
        {
            return new DiscordRankMembershipMutationResult(
                false,
                409,
                "Discord member identity changed during rank membership mutation; review the owned intent before retrying.",
                memberBefore,
                memberAfter,
                added,
                removed,
                false,
                true,
                true,
                request.OwnedIntentId);
        }
        if (!string.IsNullOrWhiteSpace(request.ExpectedIdentityHash) &&
            !string.Equals(memberAfter.EffectiveIdentityHash, request.ExpectedIdentityHash, StringComparison.OrdinalIgnoreCase))
        {
            return new DiscordRankMembershipMutationResult(
                false,
                409,
                "Discord member identity differs from the frozen identity after mutation; review the owned intent before retrying.",
                memberBefore,
                memberAfter,
                added,
                removed,
                false,
                true,
                true,
                request.OwnedIntentId);
        }

        return MembershipSuccess(
            200,
            isRollback
                ? "Discord member rank roles were restored to the frozen before set and verified."
                : "Discord member rank target was added before exact superseded managed roles were removed and verified.",
            request,
            memberBefore,
            memberAfter,
            added,
            removed,
            alreadyApplied: false);
    }

    private async Task<DiscordRankMembershipMutationResult> PartialMembershipFailureAsync(
        int status,
        string details,
        DiscordRankMembershipMutationRequest request,
        DiscordMemberObservation memberBefore,
        IReadOnlyList<string> added,
        IReadOnlyList<string> removed,
        CancellationToken ct)
    {
        DiscordMemberObservation? current = null;
        try
        {
            current = await GetMemberObservationAsync(request.DiscordUserId, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException)
        {
            details = $"{details} Current member state could not be read after the partial write: {ex.Message}";
        }

        var frozenBefore = DiscordMemberTransitionHelpers.CanonicalRoleIds(request.ExpectedBeforeRoleIds);
        var frozenAfter = DiscordMemberTransitionHelpers.CanonicalRoleIds(request.ExpectedAfterRoleIds);
        var observed = current is null
            ? null
            : DiscordMemberTransitionHelpers.CanonicalRoleIds(current.RoleIds);
        var isPartial = observed is null ||
                        (!observed.SequenceEqual(frozenBefore, StringComparer.Ordinal) &&
                         !observed.SequenceEqual(frozenAfter, StringComparer.Ordinal));

        return new DiscordRankMembershipMutationResult(
            false,
            status,
            $"{details} The owned intent remains for an explicit resume/review.",
            memberBefore,
            current,
            added,
            removed,
            false,
            isPartial,
            true,
            request.OwnedIntentId);
    }

    private async Task<DiscordRoleMutationResult> SendMemberRoleMutationAsync(
        ulong guildId,
        string token,
        string userId,
        string roleId,
        HttpMethod method,
        CancellationToken ct)
    {
        using var request = CreateRequest(
            method,
            $"{ApiBase}/guilds/{guildId}/members/{Uri.EscapeDataString(userId)}/roles/{Uri.EscapeDataString(roleId)}",
            token);
        using var response = await httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            return new DiscordRoleMutationResult(
                false,
                (int)response.StatusCode,
                $"Discord member role {(method == HttpMethod.Put ? "add" : "remove")} failed ({(int)response.StatusCode}).",
                null);
        }

        return new DiscordRoleMutationResult(
            true,
            (int)response.StatusCode,
            string.IsNullOrWhiteSpace(body) ? "Discord member role mutation accepted." : body,
            null);
    }

    private static bool IsOwnedPartialState(
        IReadOnlyList<string> current,
        IReadOnlyList<string> frozenBefore,
        IReadOnlyList<string> frozenAfter,
        IReadOnlyList<string> managedRoleIds,
        bool isRollback)
    {
        var currentSet = current.ToHashSet(StringComparer.Ordinal);
        var beforeSet = frozenBefore.ToHashSet(StringComparer.Ordinal);
        var afterSet = frozenAfter.ToHashSet(StringComparer.Ordinal);
        var managed = managedRoleIds.ToHashSet(StringComparer.Ordinal);
        if (!beforeSet.Except(managed).ToHashSet(StringComparer.Ordinal)
                .SetEquals(currentSet.Except(managed))) return false;

        // A partial owned state is exactly the frozen target plus a subset of
        // the managed roles that the operation still intends to remove.  The
        // same rule works in reverse during rollback.
        var desired = isRollback ? afterSet : afterSet;
        var frozenCurrent = isRollback ? beforeSet : beforeSet;
        var removable = frozenCurrent
            .Except(desired, StringComparer.Ordinal)
            .Where(managed.Contains)
            .ToHashSet(StringComparer.Ordinal);
        if (!desired.IsSubsetOf(currentSet)) return false;
        if (currentSet.Except(desired, StringComparer.Ordinal).Any(x => !removable.Contains(x))) return false;
        return currentSet.Count > desired.Count;
    }

    private static string? ValidateMembershipRequest(DiscordRankMembershipMutationRequest request)
    {
        if (request is null) return "The Discord membership mutation request is missing.";
        if (string.IsNullOrWhiteSpace(request.DiscordUserId)) return "Discord user ID is required.";
        if (request.ManagedRankRoleIds is null || request.ManagedRankRoleIds.Count == 0)
            return "At least one configured managed rank role ID is required.";
        if (request.ExpectedBeforeRoleIds is null || request.ExpectedAfterRoleIds is null)
            return "Frozen before and after role sets are required.";
        if (string.IsNullOrWhiteSpace(request.ExpectedBeforeRoleHash) || string.IsNullOrWhiteSpace(request.ExpectedAfterRoleHash))
            return "Frozen before and after role hashes are required.";
        if (string.IsNullOrWhiteSpace(request.TargetRoleId) && string.IsNullOrWhiteSpace(request.OwnedIntentId))
            return "Rollback/resume requests require a caller-owned intent.";

        var managed = request.ManagedRankRoleIds.ToHashSet(StringComparer.Ordinal);
        if (managed.Count != request.ManagedRankRoleIds.Count) return "Managed rank role IDs must be unique.";
        if (request.ManagedRankRoleIds.Any(x => string.IsNullOrWhiteSpace(x))) return "Managed rank role IDs must be non-empty.";
        if (!string.IsNullOrWhiteSpace(request.TargetRoleId) && !managed.Contains(request.TargetRoleId))
            return "The target role must be inside the frozen managed rank-role set.";

        var before = DiscordMemberTransitionHelpers.CanonicalRoleIds(request.ExpectedBeforeRoleIds);
        var after = DiscordMemberTransitionHelpers.CanonicalRoleIds(request.ExpectedAfterRoleIds);
        if (!DiscordMemberTransitionHelpers.UnaffectedRolesPreserved(before, after, managed))
            return "Frozen role sets change an unrelated non-rank role; refusing the mutation.";

        if (!string.IsNullOrWhiteSpace(request.TargetRoleId))
        {
            var expected = DiscordMemberTransitionHelpers.ExpectedAfterRoleSet(before, managed, request.TargetRoleId);
            if (!expected.SetEquals(after)) return "Frozen after role set is not the managed-role transition derived from the before set.";
        }

        return null;
    }

    private static DiscordRankMembershipMutationResult MembershipFailure(
        int status,
        string details,
        DiscordRankMembershipMutationRequest request,
        DiscordMemberObservation? before = null) =>
        new(false, status, details, before, null, [], [], false, false, false, request.OwnedIntentId);

    private static DiscordRankMembershipMutationResult MembershipSuccess(
        int status,
        string details,
        DiscordRankMembershipMutationRequest request,
        DiscordMemberObservation before,
        DiscordMemberObservation after,
        IReadOnlyList<string> added,
        IReadOnlyList<string> removed,
        bool alreadyApplied) =>
        new(true, status, details, before, after, added, removed, alreadyApplied, false, false, request.OwnedIntentId);

    private static int HttpStatus(Exception ex) =>
        ex is HttpRequestException { StatusCode: { } code } ? (int)code : 503;

    private static DiscordMemberObservation CreateMemberObservation(DiscordMemberWire member)
    {
        var user = member.User!;
        var roleIds = DiscordMemberTransitionHelpers.CanonicalRoleIds(member.Roles ?? []);
        var observation = new DiscordMemberObservation(
            user.Id,
            user.Username ?? "",
            user.GlobalName,
            member.Nick,
            roleIds);
        return observation with
        {
            RoleHash = DiscordMemberTransitionHelpers.ComputeRoleHash(roleIds),
            IdentityHash = DiscordMemberTransitionHelpers.ComputeIdentityHash(observation)
        };
    }

    private (ulong GuildId, string Token) ReadConfiguration()
    {
        var guildId = configuration.GetValue<ulong>("DiscordBot:GuildId");
        var token = configuration["DiscordBot:Token"] ?? "";
        return (guildId, token);
    }

    private IReadOnlySet<string> ReadConfiguredRoleIds() =>
        configuration.GetSection("DiscordBot:RankRoleIds")
            .GetChildren()
            .Select(x => x.Value?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x) && ulong.TryParse(x, out _))
            .Select(x => x!)
            .ToHashSet(StringComparer.Ordinal);

    private static HttpRequestMessage CreateRequest(HttpMethod method, string uri, string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
        request.Headers.UserAgent.ParseAdd("SwedesClanTracker-RankTransition/1.0");
        return request;
    }

    private static async Task<string> ReadSuccessfulResponseAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode) return body;

        var retryAfter = response.Headers.RetryAfter?.Delta is { } delta
            ? $" Retry after {delta.TotalSeconds:0.#} seconds."
            : "";
        throw new HttpRequestException(
            $"{operation} failed ({(int)response.StatusCode}).{retryAfter}",
            inner: null,
            statusCode: response.StatusCode);
    }

    private sealed class DiscordRoleWire
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("permissions")]
        public string? Permissions { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("position")]
        public int? Position { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("managed")]
        public bool? Managed { get; set; }
    }

    private sealed class DiscordMemberWire
    {
        [System.Text.Json.Serialization.JsonPropertyName("user")]
        public DiscordUserWire? User { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("roles")]
        public string[]? Roles { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("nick")]
        public string? Nick { get; set; }
    }

    private sealed class DiscordUserWire
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("username")]
        public string? Username { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("global_name")]
        public string? GlobalName { get; set; }
    }
}
