using DietTime.Persistence;
namespace DietTime.Meal.Api;
public sealed class StaffNotificationWorker(IServiceScopeFactory scopes,ILogger<StaffNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(30));
        do {
            try {
                await using var scope=scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<StaffNotificationService>().ProjectAsync(stoppingToken);
            } catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { return; }
            catch(Exception error) { logger.LogError(error,"Staff notification projection failed; durable events will be retried."); }
        } while(await timer.WaitForNextTickAsync(stoppingToken));
    }
}
