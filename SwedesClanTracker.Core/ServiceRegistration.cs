using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SwedesClanTracker.Core;

public static class ServiceRegistration
{
    public static IServiceCollection AddTrackerCore(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<TrackerDbContext>(opt =>
            opt.UseSqlServer(config.GetConnectionString("DefaultConnection")));
        services.AddHttpClient<ITempleClient, TempleClient>();
        services.AddHttpClient<IWiseOldManClient, WiseOldManClient>();
        services.AddScoped<ITrackerSyncService, TrackerSyncService>();
        return services;
    }
}
