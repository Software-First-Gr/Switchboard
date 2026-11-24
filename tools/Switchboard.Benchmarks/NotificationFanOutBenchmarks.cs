using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Switchboard;

namespace Switchboard.Benchmarks;

[Config(typeof(RuntimeAsyncConfig))]
[MemoryDiagnoser]
public class NotificationFanOutBenchmarks
{
    private static readonly BenchmarkNotification Notification = new();
    private ServiceProvider? _provider;
    private InvocationLog _log = null!;

    [Params(1, 8, 32)]
    public int HandlerCount { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        RuntimeAsyncGuard.AssertExpectedMode();

        _log = new InvocationLog();
        var services = new ServiceCollection();
        var expectedHandlers = new List<INotificationHandler<BenchmarkNotification>>(HandlerCount);

        for (var i = 0; i < HandlerCount; i++)
        {
            var handler = new BenchmarkNotificationHandler(i, _log);
            expectedHandlers.Add(handler);
            services.AddSingleton<INotificationHandler<BenchmarkNotification>>(handler);
        }

        services.AddSwitchboard(_ => { });
        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        var resolved = _provider.GetServices<INotificationHandler<BenchmarkNotification>>().ToArray();
        if (resolved.Length != expectedHandlers.Count ||
            resolved.Where((handler, index) => !ReferenceEquals(handler, expectedHandlers[index])).Any())
        {
            throw new InvalidOperationException("Microsoft DI did not return the registered singleton handlers in order.");
        }

        IEnumerable<INotificationHandler<BenchmarkNotification>> nonArrayHandlers = new EnumerableAdapter(resolved);
        if (nonArrayHandlers is INotificationHandler<BenchmarkNotification>[])
        {
            throw new InvalidOperationException("The fallback fixture unexpectedly has an array runtime type.");
        }

        _log.Enabled = true;
        await RunEnumerable(resolved);
        VerifyInvocationOrder();

        _log.Reset();
        await RunArrayBranch(resolved);
        VerifyInvocationOrder();

        _log.Reset();
        await RunArrayBranch(nonArrayHandlers);
        VerifyInvocationOrder();
        _log.Enabled = false;
    }

    [Benchmark(Baseline = true)]
    public Task InterfaceEnumeration()
    {
        _log.Reset();
        return RunEnumerable(_provider!.GetServices<INotificationHandler<BenchmarkNotification>>());
    }

    [Benchmark]
    public Task ArrayBranchWithEnumerableFallback()
    {
        _log.Reset();
        return RunArrayBranch(_provider!.GetServices<INotificationHandler<BenchmarkNotification>>());
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    private async Task RunEnumerable(IEnumerable<INotificationHandler<BenchmarkNotification>> handlers)
    {
        foreach (var handler in handlers)
        {
            await handler.Handle(Notification, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RunArrayBranch(IEnumerable<INotificationHandler<BenchmarkNotification>> handlers)
    {
        if (handlers is INotificationHandler<BenchmarkNotification>[] array)
        {
            for (var i = 0; i < array.Length; i++)
            {
                await array[i].Handle(Notification, CancellationToken.None).ConfigureAwait(false);
            }

            return;
        }

        foreach (var handler in handlers)
        {
            await handler.Handle(Notification, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void VerifyInvocationOrder()
    {
        if (_log.Ids.Count != HandlerCount)
        {
            throw new InvalidOperationException($"Expected {HandlerCount} handlers, but observed {_log.Ids.Count}.");
        }

        for (var i = 0; i < HandlerCount; i++)
        {
            if (_log.Ids[i] != i)
            {
                throw new InvalidOperationException($"Expected handler {i} at position {i}, but observed {_log.Ids[i]}.");
            }
        }
    }
}

public sealed class BenchmarkNotification : INotification
{
}

internal sealed class BenchmarkNotificationHandler : INotificationHandler<BenchmarkNotification>
{
    private static readonly Task Completed = Task.CompletedTask;
    private readonly int _id;
    private readonly InvocationLog _log;

    public BenchmarkNotificationHandler(int id, InvocationLog log)
    {
        _id = id;
        _log = log;
    }

    public Task Handle(BenchmarkNotification notification, CancellationToken cancellationToken)
    {
        if (_log.Enabled)
        {
            _log.Ids.Add(_id);
        }

        return Completed;
    }
}

internal sealed class InvocationLog
{
    public List<int> Ids { get; } = new();
    public bool Enabled { get; set; }

    public void Reset()
        => Ids.Clear();
}

internal sealed class EnumerableAdapter : IEnumerable<INotificationHandler<BenchmarkNotification>>
{
    private readonly IEnumerable<INotificationHandler<BenchmarkNotification>> _handlers;

    public EnumerableAdapter(IEnumerable<INotificationHandler<BenchmarkNotification>> handlers)
        => _handlers = handlers;

    public IEnumerator<INotificationHandler<BenchmarkNotification>> GetEnumerator()
        => _handlers.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();
}