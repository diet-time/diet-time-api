using System.Threading.Channels;
using DietTime.Application;

namespace DietTime.Meal.Api;

/// <summary>Wakes durable outbox processing without blocking checkout; polls also recover missed wake-ups.</summary>
public sealed class OrderNotificationWorker(
    IServiceScopeFactory scopes,
    ILogger<OrderNotificationWorker> logger) : BackgroundService, IOrderNotificationQueue
{
    private readonly Channel<Guid> pending = Channel.CreateBounded<Guid>(new BoundedChannelOptions(1024)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false
    });

    // Never wait for Twilio or queue capacity on the order request.
    public bool TryEnqueue(Guid orderId) => pending.Writer.TryWrite(orderId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wake.CancelAfter(TimeSpan.FromSeconds(5));
                Guid orderId = Guid.Empty;
                try { orderId = await pending.Reader.ReadAsync(wake.Token); }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
                catch (ChannelClosedException) { return; }
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(60));
                    await using var scope = scopes.CreateAsyncScope();
                    var sender = scope.ServiceProvider.GetRequiredService<IOrderNotificationSender>();
                    if (orderId == Guid.Empty) await sender.SendPendingAsync(timeout.Token);
                    else await sender.SendAsync(orderId, timeout.Token);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    logger.LogError(error, "Order notification failed in background. OrderId={OrderId}", orderId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        pending.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }
}
