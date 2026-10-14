using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SwedesClanTracker.Api.Contracts;

namespace SwedesClanTracker.Api.Controllers;

/// <summary>Read-only session bootstrap for the authenticated operator console.</summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthSessionController : ControllerBase
{
    /// <summary>Returns the current cookie-authenticated session or 401.</summary>
    [AllowAnonymous]
    [HttpGet("session")]
    [ProducesResponseType(typeof(AuthSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<AuthSessionResponse> GetSession()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return Unauthorized();
        }

        var roles = User.Claims
            .Where(x => x.Type == ClaimTypes.Role)
            .Select(x => x.Value)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var username = User.Identity.Name ?? User.FindFirstValue(ClaimTypes.Name) ?? "";
        return Ok(new AuthSessionResponse(username, roles, roles.Contains("Admin", StringComparer.OrdinalIgnoreCase)));
    }
}
