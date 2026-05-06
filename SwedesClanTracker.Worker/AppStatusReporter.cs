using Microsoft.EntityFrameworkCore;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.Worker;

public class AppStatusReporter(IServiceScopeFactory scopeFactory, ILogger<AppStatusReporter> logger)
{
    public async Task ReportAsync(
        string component,
        string state,
        string message,
        CancellationToken ct,
        object? details = null)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
            var ownerId = await db.Players
                .OrderBy(x => x.Id)
                .Select(x => (int?)x.Id)
                .FirstOrDefaultAsync(ct);
            if (!ownerId.HasValue) return;

            var marker = $"\"Component\":\"{component}\"";
            var status = await db.LifecycleEvents
                .Where(x => x.EventType == AppStatusConstants.EventType && x.MetadataJson.Contains(marker))
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(ct);

            var now = DateTimeOffset.UtcNow;
            var metadata = JsonUtil.Serialize(new
            {
                Component = component,
                State = state,
                Message = message,
                Details = details,
                HeartbeatAt = now
            });

            if (status is null)
            {
                db.LifecycleEvents.Add(new LifecycleEvent
                {
                    PlayerId = ownerId.Value,
                    EventType = AppStatusConstants.EventType,
                    MetadataJson = metadata,
                    Status = "OPEN",
                    CreatedAt = now
                });
            }
            else
            {
                status.PlayerId = ownerId.Value;
                status.MetadataJson = metadata;
                status.Status = "OPEN";
            }

            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to update app status for {Component}", component);
        }
    }
}
