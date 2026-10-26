using DietTime.Application;

namespace DietTime.Meal.Api;

// Runs at startup and every 15 seconds: handles 21:00 Qatar and missed executions.
public sealed class TomorrowMealFinalizationWorker(IServiceScopeFactory scopes,
    ILogger<TomorrowMealFinalizationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IUpcomingMealService>().FinalizeDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogError(error, "Tomorrow meal finalization failed; retrying on the next pass."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
