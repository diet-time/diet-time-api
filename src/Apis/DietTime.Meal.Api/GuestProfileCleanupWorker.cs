using DietTime.Application;

namespace DietTime.Meal.Api;

public sealed class GuestProfileCleanupWorker(
    IServiceScopeFactory scopeFactory,
    GuestProfileOptions options,
    ILogger<GuestProfileCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, options.CleanupIntervalHours));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var total = 0;
                int deleted;
                do
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    deleted = await scope.ServiceProvider
                        .GetRequiredService<IGuestProfileCleanupService>()
                        .DeleteExpiredBatchAsync(stoppingToken);
                    total += deleted;
                }
                while (deleted > 0 && !stoppingToken.IsCancellationRequested);

                if (total > 0)
                    logger.LogInformation("Deleted {Count} expired guest profiles", total);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Expired guest profile cleanup failed");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}
