using DietTime.Application;
using DietTime.Meal.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed class OrderNotificationWorkerTests
{
    [Fact]
    public async Task Enqueue_does_not_wait_for_provider_and_worker_continues_after_failure()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var disposed = 0;
        await using var services = new ServiceCollection().AddScoped<IOrderNotificationSender>(_ =>
            new Sender(async (id, ct) =>
            {
                if (id == first)
                {
                    started.SetResult();
                    await release.Task.WaitAsync(ct);
                    throw new HttpRequestException("Twilio is unavailable");
                }
                Assert.Equal(second, id);
                completed.SetResult();
            }, () => Interlocked.Increment(ref disposed))).BuildServiceProvider();
        using var worker = new OrderNotificationWorker(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OrderNotificationWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            Assert.True(worker.TryEnqueue(first));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // A hung provider cannot block enqueue or another caller.
            Assert.True(worker.TryEnqueue(second));
            Assert.False(completed.Task.IsCompleted);
            release.SetResult();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await worker.StopAsync(default); }
        Assert.Equal(2, disposed); // Each send owns a fresh scope, including the failed one.
        Assert.False(worker.TryEnqueue(Guid.NewGuid()));
    }

    [Fact]
    public void Full_queue_rejects_immediately_instead_of_blocking_checkout()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var worker = new OrderNotificationWorker(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OrderNotificationWorker>.Instance);
        for (var i = 0; i < 1024; i++) Assert.True(worker.TryEnqueue(Guid.NewGuid()));
        Assert.False(worker.TryEnqueue(Guid.NewGuid()));
    }

    private sealed class Sender(Func<Guid, CancellationToken, Task> send, Action dispose)
        : IOrderNotificationSender, IDisposable
    {
        public Task SendAsync(Guid orderId, CancellationToken cancellationToken) => send(orderId, cancellationToken);
        public void Dispose() => dispose();
    }
}
