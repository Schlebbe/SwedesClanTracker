using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SwedesClanTracker.Api.Contracts;
using SwedesClanTracker.Api.Services;

namespace SwedesClanTracker.Api.Controllers;

/// <summary>Read-only operator-console projections. No endpoint in this controller mutates state.</summary>
[ApiController]
[Authorize]
[Route("api/app/console")]
public sealed class ReadOnlyConsoleController(ReadOnlyConsoleQueryService queries) : ControllerBase
{
    [HttpGet("overview")]
    [ProducesResponseType(typeof(ConsoleOverviewResponse), StatusCodes.Status200OK)]
    public Task<ConsoleOverviewResponse> GetOverview(CancellationToken ct) => queries.GetOverviewAsync(ct);

    [HttpGet("roster")]
    [ProducesResponseType(typeof(ConsoleRosterResponse), StatusCodes.Status200OK)]
    public Task<ConsoleRosterResponse> GetRoster(
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] string? rank,
        [FromQuery] string? attention,
        [FromQuery] string? sort,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default) => queries.GetRosterAsync(search, status, rank, attention, sort, page, pageSize, ct);

    [HttpGet("players/{playerId:int}/profile")]
    [ProducesResponseType(typeof(ConsoleProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConsoleProfileResponse>> GetProfile(int playerId, [FromQuery] string? range, CancellationToken ct)
    {
        var result = await queries.GetProfileAsync(playerId, range, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("reviews")]
    [ProducesResponseType(typeof(ConsoleReviewQueueResponse), StatusCodes.Status200OK)]
    public Task<ConsoleReviewQueueResponse> GetReviews(
        [FromQuery] string? family,
        [FromQuery] string? state,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default) => queries.GetReviewQueueAsync(family, state, search, page, pageSize, ct);

    [HttpGet("reviews/{caseId}")]
    [ProducesResponseType(typeof(ConsoleReviewDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConsoleReviewDetailResponse>> GetReviewDetail(string caseId, CancellationToken ct)
    {
        var result = await queries.GetReviewDetailAsync(caseId, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("reviews/ignored-policies")]
    [ProducesResponseType(typeof(ConsoleIgnoredPoliciesResponse), StatusCodes.Status200OK)]
    public Task<ConsoleIgnoredPoliciesResponse> GetIgnoredPolicies(CancellationToken ct) => queries.GetIgnoredPoliciesAsync(ct);

    [HttpGet("activity")]
    [ProducesResponseType(typeof(ConsoleActivityResponse), StatusCodes.Status200OK)]
    public Task<ConsoleActivityResponse> GetActivity(
        [FromQuery] string? eventType,
        [FromQuery] string? status,
        [FromQuery] string? player,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? source,
        [FromQuery] string? correlationId,
        [FromQuery] int take = 100,
        CancellationToken ct = default) => queries.GetActivityAsync(eventType, status, player, from, to, source, correlationId, take, ct);

    [HttpGet("system")]
    [ProducesResponseType(typeof(ConsoleSystemResponse), StatusCodes.Status200OK)]
    public Task<ConsoleSystemResponse> GetSystem(CancellationToken ct) => queries.GetSystemAsync(ct);
}
