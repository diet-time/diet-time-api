using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Net;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit.Abstractions;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    private readonly ITestOutputHelper performanceOutput;
    public UpcomingMealServiceTests(ITestOutputHelper output) => performanceOutput = output;

    [UpcomingPostgresFact]
    public async Task Performance_dashboard_excludes_history_preserves_old_active_plan_and_customer_isolation()
    {
        await PrepareAsync();
        for (var i = 0; i < 80; i++)
        {
            var historical = (Order)db.Entry(order).CurrentValues.ToObject();
            historical.Id = Guid.NewGuid(); historical.OrderNumber = $"HISTORY-{i}";
            historical.IdempotencyKey = Guid.NewGuid().ToString();
            historical.Status = "CANCELLED"; historical.PlacedAt = order.PlacedAt.AddMinutes(i + 1);
            db.Add(historical);
        }
        await db.SaveChangesAsync();
        var capture = new PerformanceQueryCapture();
        var pooledConnection = new NpgsqlConnectionStringBuilder(connection) { Pooling = true }.ConnectionString;
        await using var measuredDb = new DietTimeDbContext(new DbContextOptionsBuilder<DietTimeDbContext>()
            .UseNpgsql(pooledConnection).UseSnakeCaseNamingConvention().AddInterceptors(capture).Options);
        var service = new OrderService(measuredDb, clock, Options.Create(new OrderOptions()),
            new BookingQueue(), NullLogger<OrderService>.Instance);
        async Task<CustomerOrdersResponse> Baseline()
        {
            CustomerOrdersResponse page = null!;
            for (var number = 1; number <= 5; number++)
                page = (await service.GetCustomerOrdersAsync(order.CustomerProfileId, userId, null, number, 20, default))!;
            await service.GetAsync(order.Id, userId, default);
            return page;
        }
        // Warm both query shapes before collecting comparable local service timings.
        await Baseline();
        await service.GetDashboardAsync(order.CustomerProfileId, userId, default);
        var report = new StringBuilder("Local PostgreSQL; pooling=true; 80 cancelled orders plus one old active plan.\n");
        foreach (var mode in new[] { "before", "after" })
        {
            var times = new List<double>();
            var counts = new List<int>();
            for (var run = 0; run < 5; run++)
            {
                capture.Commands.Clear();
                var watch = Stopwatch.StartNew();
                if (mode == "before") await Baseline();
                else
                {
                    var summary = (await service.GetDashboardAsync(order.CustomerProfileId, userId, default))!;
                    Assert.Equal(4, summary.Items.Count);
                    Assert.Contains(summary.Items, item => item.Id == order.Id);
                    Assert.Equal(summary.Items.Count, summary.TotalCount);
                    Assert.NotNull(summary.Items.Single(item => item.Id == order.Id).PackageSchedule);
                }
                watch.Stop(); times.Add(watch.Elapsed.TotalMilliseconds); counts.Add(capture.Commands.Count);
            }
            times.Sort();
            report.AppendLine($"{mode}: service median_ms={times[2]:F3}; min_ms={times[0]:F3}; max_ms={times[^1]:F3}; commands={counts[0]}; runs=5");
        }
        Assert.Null(await service.GetDashboardAsync(order.CustomerProfileId, Guid.NewGuid(), default));
        capture.Commands.Clear();
        await service.GetDashboardAsync(order.CustomerProfileId, userId, default);
        var plans = new StringBuilder();
        await using var planConnection = new NpgsqlConnection(connection);
        await planConnection.OpenAsync();
        foreach (var query in capture.Commands)
        {
            await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + query.Sql, planConnection);
            foreach (var parameter in query.Parameters)
                explain.Parameters.Add(new NpgsqlParameter(parameter.Name, parameter.Value));
            var plan = (string)(await explain.ExecuteScalarAsync())!;
            plans.AppendLine(query.Sql).AppendLine(plan).AppendLine();
            using var json = JsonDocument.Parse(plan);
            report.AppendLine($"plan_execution_ms={json.RootElement[0].GetProperty("Execution Time").GetDouble():F3}; query={query.Sql.Split('\n').FirstOrDefault(line => line.StartsWith("SELECT"))}");
        }
        performanceOutput.WriteLine(report.ToString());
        var reportDirectory = Environment.GetEnvironmentVariable("PERFORMANCE_REPORT_DIRECTORY");
        if (!string.IsNullOrEmpty(reportDirectory))
        {
            Directory.CreateDirectory(reportDirectory);
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "postgres-benchmark.txt"), report.ToString());
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "postgres-plans.txt"), plans.ToString());
        }
    }

    [UpcomingPostgresFact]
    public async Task Performance_dashboard_http_ownership_cache_headers_and_local_endpoint_timings()
    {
        await PrepareAsync();
        var pooledConnection = new NpgsqlConnectionStringBuilder(connection) { Pooling = true }.ConnectionString;
        await using var factory = new ApiFactory(pooledConnection, services => {
            services.AddSingleton<TimeProvider>(clock);
            services.RemoveAll<IHostedService>();
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Development-User-Id", userId.ToString());
        var dashboardPath = $"/api/v1/customer-profiles/{order.CustomerProfileId}/orders/dashboard";
        using (var response = await client.GetAsync(dashboardPath))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(order.Id, json.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
        }
        client.DefaultRequestHeaders.Remove("X-Development-User-Id");
        client.DefaultRequestHeaders.Add("X-Development-User-Id", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(dashboardPath)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Development-User-Id");
        client.DefaultRequestHeaders.Add("X-Development-User-Id", userId.ToString());
        var measured = new List<(string Endpoint, double Median, long Bytes)>();
        foreach (var (name, path) in new[] {
            ("dashboard_summary", dashboardPath),
            ("customer_orders", $"/api/v1/customer-profiles/{order.CustomerProfileId}/orders"),
            ("order_details", $"/api/v1/orders/{order.Id}"),
            ("package_management", $"/api/v1/orders/{order.Id}/package-management"),
            ("tomorrow_menu", $"/api/v1/orders/{order.Id}/tomorrow-menu?language=en"),
            ("delivery_calendar", $"/api/v1/orders/{order.Id}/upcoming-meals") })
        {
            using var warmup = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, warmup.StatusCode);
            var times = new List<double>(); long bytes = 0;
            for (var run = 0; run < 3; run++)
            {
                var watch = Stopwatch.StartNew();
                using var response = await client.GetAsync(path);
                var body = await response.Content.ReadAsByteArrayAsync();
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                watch.Stop(); times.Add(watch.Elapsed.TotalMilliseconds); bytes = body.Length;
            }
            times.Sort(); measured.Add((name, times[1], bytes));
        }
        var report = "Local in-process ASP.NET TestServer + PostgreSQL, pooling=true, one active plan; warm medians, 3 runs per endpoint. No mobile/Railway/TLS latency.\n" +
            string.Join("\n", measured.OrderByDescending(m => m.Median).Select(m => $"{m.Endpoint}: median_ms={m.Median:F3}; payload_bytes={m.Bytes}"));
        performanceOutput.WriteLine(report);
        var reportDirectory = Environment.GetEnvironmentVariable("PERFORMANCE_REPORT_DIRECTORY");
        if (!string.IsNullOrEmpty(reportDirectory))
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "local-http-endpoints.txt"), report);
    }
}

internal sealed class PerformanceQueryCapture : DbCommandInterceptor
{
    internal sealed record Parameter(string Name, object Value);
    internal sealed record Query(string Sql, Parameter[] Parameters);
    public List<Query> Commands { get; } = [];
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data,
        DbDataReader result, CancellationToken cancellationToken = default)
    {
        Commands.Add(new(command.CommandText, command.Parameters.Cast<DbParameter>()
            .Select(p => new Parameter(p.ParameterName, p.Value!)).ToArray()));
        return ValueTask.FromResult(result);
    }
}
