using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using SwedesClanTracker.Core;

var options = new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService()
        ? AppContext.BaseDirectory
        : default
};
var builder = WebApplication.CreateBuilder(options);
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "SwedesClanTracker-Api";
});
builder.Services.AddSystemd();
builder.Services.AddTrackerCore(builder.Configuration);
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
builder.Services.AddAuthorization();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection("Auth"))
    .Validate(static o => !string.IsNullOrWhiteSpace(o.Username), "Auth:Username must be configured.")
    .Validate(static o => !string.IsNullOrWhiteSpace(o.Password), "Auth:Password must be configured.")
    .ValidateOnStart();

var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
    db.Database.Migrate();
}

app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.MapPost("/api/auth/login", async ([FromBody] LoginRequest req, HttpContext ctx, IOptions<AuthOptions> authOptions) =>
{
    var user = authOptions.Value.Username;
    var pass = authOptions.Value.Password;
    if (req.Username != user || req.Password != pass) return Results.Unauthorized();
    var claims = new[] { new Claim(ClaimTypes.Name, req.Username), new Claim(ClaimTypes.Role, "Admin") };
    var id = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(id));
    return Results.Ok();
});

app.MapPost("/api/auth/logout", [Authorize] async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
});

app.Run();

public record LoginRequest(string Username, string Password);
public class AuthOptions { public string Username { get; set; } = ""; public string Password { get; set; } = ""; }
