using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SwedesClanTracker.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/settings")]
public class SettingsController(IConfiguration config) : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        ApiRateLimitPerMinute = config.GetValue<int?>("Tracker:TempleApiCallsPerMinute") ?? 5,
        ConnectionStringConfigured = !string.IsNullOrWhiteSpace(config.GetConnectionString("DefaultConnection"))
    });
}
