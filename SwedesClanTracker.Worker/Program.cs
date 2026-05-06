using SwedesClanTracker.Core;
using SwedesClanTracker.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "SwedesClanTracker-Worker";
});
builder.Services.AddTrackerCore(builder.Configuration);
builder.Services.AddSingleton<IPlayerUpdateQueue, PlayerUpdateQueue>();
builder.Services.AddSingleton<AppStatusReporter>();
builder.Services.AddHostedService<TrackerWorker>();
builder.Services.AddHostedService<DiscordPromotionBotWorker>();

var host = builder.Build();
host.Run();
