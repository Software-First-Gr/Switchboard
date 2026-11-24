using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Switchboard.Benchmarks.Study;

// An application-shaped workload against a real database: GetOrder through three typical async behaviors to a handler that
// queries Postgres with Npgsql, scope per dispatch on, handlers and behaviors transient, many concurrent callers.
// Wall-clock latency is dominated by the database round trip, so the metrics that show the mediator's async cost are the
// client CPU time and bytes allocated per request.

public sealed record GetOrder(int Id) : IRequest<OrderDto>;

public sealed record OrderDto(int Id, string Customer, decimal Total);

public sealed class GetOrderHandler(NpgsqlDataSource dataSource) : IRequestHandler<GetOrder, OrderDto>
{
    public async Task<OrderDto> Handle(GetOrder request, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT id, customer, total FROM orders WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = request.Id });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new KeyNotFoundException($"Order {request.Id} not found.");
        }

        return new OrderDto(reader.GetInt32(0), reader.GetString(1), reader.GetDecimal(2));
    }
}

public sealed class RequestCounter
{
    private long _count;

    public void Increment() => Interlocked.Increment(ref _count);
}

/// <summary>Logging-style behavior: records the request, awaits next, records the outcome.</summary>
public sealed class CountingBehavior<TRequest, TResponse>(RequestCounter counter) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        counter.Increment();
        var response = await next();
        counter.Increment();
        return response;
    }
}

/// <summary>Validation-style behavior: an async validator that completes synchronously, as most do.</summary>
public sealed class ValidatingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (!await ValidateAsync(request))
        {
            throw new ArgumentException("Invalid request.");
        }

        return await next();
    }

    private static ValueTask<bool> ValidateAsync(TRequest request) => ValueTask.FromResult(request is not GetOrder { Id: <= 0 });
}

/// <summary>Timing-style behavior: measures next() and swallows nothing.</summary>
public sealed class TimingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await next();
        }
        finally
        {
            _ = Stopwatch.GetElapsedTime(started);
        }
    }
}

internal static class PostgresLoad
{
    public const int Rows = 10_000;

    public static async Task Seed(string connectionString)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using (var create = dataSource.CreateCommand(
            "DROP TABLE IF EXISTS orders; CREATE TABLE orders (id int PRIMARY KEY, customer text NOT NULL, total numeric(12,2) NOT NULL);"))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using var insert = dataSource.CreateCommand(
            $"INSERT INTO orders SELECT g, 'customer-' || g, (g % 1000) + 0.99 FROM generate_series(1, {Rows}) g; ANALYZE orders;");
        await insert.ExecuteNonQueryAsync();
    }

    public static async Task<object> Run(string connectionString, int concurrency, TimeSpan warmup, TimeSpan duration)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.ConnectionStringBuilder.MaxPoolSize = concurrency;
        builder.ConnectionStringBuilder.MinPoolSize = concurrency;
        await using var dataSource = builder.Build();

        var services = new ServiceCollection()
            .AddSingleton(dataSource)
            .AddSingleton<RequestCounter>()
            .AddTransient<IRequestHandler<GetOrder, OrderDto>, GetOrderHandler>();
        services.AddSwitchboard(cfg => cfg
            .AddOpenBehavior(typeof(CountingBehavior<,>))
            .AddOpenBehavior(typeof(ValidatingBehavior<,>))
            .AddOpenBehavior(typeof(TimingBehavior<,>))
            .UseScopePerDispatch());
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        var sender = provider.GetRequiredService<ISender>();

        // Warm up: open every pooled connection, tier up the hot path.
        await Drive(sender, concurrency, warmup, record: false);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var process = Process.GetCurrentProcess();
        var cpu0 = process.TotalProcessorTime;
        var allocated0 = GC.GetTotalAllocatedBytes(precise: true);
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var pause0 = GC.GetTotalPauseDuration();
        var wall = Stopwatch.StartNew();

        var latencies = await Drive(sender, concurrency, duration, record: true);

        wall.Stop();
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpu0;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated0;
        var ops = latencies.Length;
        Array.Sort(latencies);

        double Percentile(double p) => Stopwatch.GetElapsedTime(0, latencies[(int)Math.Min(ops - 1, p * ops)]).TotalMicroseconds;

        return new
        {
            ops,
            opsPerSecond = ops / wall.Elapsed.TotalSeconds,
            p50Us = Percentile(0.50),
            p95Us = Percentile(0.95),
            p99Us = Percentile(0.99),
            cpuUsPerOp = cpu.TotalMicroseconds / ops,
            bytesPerOp = (double)allocated / ops,
            gen0 = GC.CollectionCount(0) - gen0,
            gen1 = GC.CollectionCount(1) - gen1,
            gen2 = GC.CollectionCount(2) - gen2,
            gcPauseMs = (GC.GetTotalPauseDuration() - pause0).TotalMilliseconds,
        };
    }

    private static async Task<long[]> Drive(ISender sender, int concurrency, TimeSpan duration, bool record)
    {
        using var stop = new CancellationTokenSource(duration);
        var perWorker = new List<long>[concurrency];

        var workers = Enumerable.Range(0, concurrency).Select(worker => Task.Run(async () =>
        {
            var samples = perWorker[worker] = new List<long>(record ? 200_000 : 0);
            var random = new Random(worker);
            while (!stop.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                var order = await sender.Send(new GetOrder(random.Next(1, Rows + 1)));
                if (order.Id <= 0)
                {
                    throw new InvalidOperationException("Unexpected order.");
                }

                if (record)
                {
                    samples.Add(Stopwatch.GetTimestamp() - started);
                }
            }
        })).ToArray();

        await Task.WhenAll(workers);
        return perWorker.SelectMany(s => s).ToArray();
    }
}
