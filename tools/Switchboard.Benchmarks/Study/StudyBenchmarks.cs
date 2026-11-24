using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Switchboard.Benchmarks.Study;

/// <summary>
/// <c>Send</c> through N async pass-through behaviors, the handler either completing synchronously or suspending once and
/// being resumed inline. The suspended case is where the compiler-generated model boxes a state machine per async frame.
/// </summary>
[Config(typeof(AbConfig))]
public class SendDepthBenchmarks
{
    private readonly Gate _gate = new();
    private ServiceProvider _provider = null!;
    private ISender _sender = null!;
    private readonly DeepRequest _request = new(42);

    [Params(0, 1, 4, 8)]
    public int Behaviors { get; set; }

    [Params(false, true)]
    public bool Suspend { get; set; }

    [Params(false, true)]
    public bool ScopePerDispatch { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Variant.AssertAsBuilt();
        _provider = StudyServices.Build(_gate, Behaviors, ScopePerDispatch);
        _sender = _provider.GetRequiredService<ISender>();

        if (Send() != 42)
        {
            throw new InvalidOperationException("Unexpected response.");
        }
    }

    [Benchmark]
    public int Send()
    {
        if (!Suspend)
        {
            return _sender.Send(_request).GetAwaiter().GetResult();
        }

        _gate.Arm();
        var pending = _sender.Send(_request);
        if (pending.IsCompleted)
        {
            throw new InvalidOperationException("The dispatch did not suspend.");
        }

        _gate.Release();
        if (!pending.IsCompleted)
        {
            throw new InvalidOperationException("The dispatch did not resume inline.");
        }

        return pending.GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup() => _provider.Dispose();
}

/// <summary>A handler failure propagating out through N async behaviors to the caller's catch.</summary>
[Config(typeof(AbConfig))]
public class ExceptionDepthBenchmarks
{
    private readonly Gate _gate = new();
    private ServiceProvider _provider = null!;
    private ISender _sender = null!;
    private readonly DeepFailingRequest _request = new(42);

    [Params(0, 4, 8)]
    public int Behaviors { get; set; }

    [Params(false, true)]
    public bool Suspend { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Variant.AssertAsBuilt();
        _provider = StudyServices.Build(_gate, Behaviors, scopePerDispatch: false);
        _sender = _provider.GetRequiredService<ISender>();
    }

    [Benchmark]
    public int SendThatThrows()
    {
        Task<int> pending;
        if (Suspend)
        {
            _gate.Arm();
            pending = _sender.Send(_request);
            _gate.Release();
        }
        else
        {
            pending = _sender.Send(_request);
        }

        try
        {
            return pending.GetAwaiter().GetResult();
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _provider.Dispose();
}

/// <summary>Publish to four async handlers, and enumerate a 32-item async-iterator stream.</summary>
[Config(typeof(AbConfig))]
public class PublishAndStreamBenchmarks
{
    private readonly Gate _gate = new();
    private ServiceProvider _provider = null!;
    private IMediator _mediator = null!;
    private readonly DeepNote _note = new(1);
    private readonly DeepStream _stream = new(32);

    [GlobalSetup]
    public void Setup()
    {
        Variant.AssertAsBuilt();
        var services = new ServiceCollection().AddSingleton(_gate);
        for (var i = 0; i < 4; i++)
        {
            services.AddSingleton<INotificationHandler<DeepNote>>(new DeepNoteHandler(_gate));
        }

        services.AddSingleton<IStreamRequestHandler<DeepStream, int>, DeepStreamHandler>();
        services.AddSwitchboard(_ => { });
        _provider = services.BuildServiceProvider();
        _mediator = _provider.GetRequiredService<IMediator>();
    }

    [Benchmark]
    public void PublishToFourHandlers() => _mediator.Publish(_note).GetAwaiter().GetResult();

    [Benchmark]
    public void PublishToFourHandlersFirstSuspends()
    {
        _gate.Arm();
        var pending = _mediator.Publish(_note);
        _gate.Release();
        pending.GetAwaiter().GetResult();
    }

    [Benchmark]
    public int StreamOf32() => Sum().GetAwaiter().GetResult();

    private async Task<int> Sum()
    {
        var sum = 0;
        await foreach (var item in _mediator.CreateStream(_stream))
        {
            sum += item;
        }

        return sum;
    }

    [GlobalCleanup]
    public void Cleanup() => _provider.Dispose();
}

/// <summary>
/// The ceiling: N nested async methods calling each other directly, no mediator and no delegates in between. This is the
/// shape runtime async optimizes best (async-to-async calls), so it shows what the feature can do at all, next to what
/// Switchboard's delegate-based pipeline lets through.
/// </summary>
[Config(typeof(AbConfig))]
public class RawAsyncChainBenchmarks
{
    private readonly Gate _gate = new();

    [Params(1, 4, 8)]
    public int Depth { get; set; }

    [Params(false, true)]
    public bool Suspend { get; set; }

    [GlobalSetup]
    public void Setup() => Variant.AssertAsBuilt();

    [Benchmark]
    public int Chain()
    {
        if (!Suspend)
        {
            return Level(Depth).GetAwaiter().GetResult();
        }

        _gate.Arm();
        var pending = Level(Depth);
        _gate.Release();
        return pending.GetAwaiter().GetResult();
    }

    [Benchmark]
    public int ChainThatThrows()
    {
        Task<int> pending;
        if (Suspend)
        {
            _gate.Arm();
            pending = Failing(Depth);
            _gate.Release();
        }
        else
        {
            pending = Failing(Depth);
        }

        try
        {
            return pending.GetAwaiter().GetResult();
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private async Task<int> Level(int remaining)
    {
        if (remaining == 0)
        {
            await _gate.Wait;
            return 42;
        }

        var value = await Level(remaining - 1);
        return value;
    }

    private async Task<int> Failing(int remaining)
    {
        if (remaining == 0)
        {
            await _gate.Wait;
            throw new InvalidOperationException("leaf failed");
        }

        var value = await Failing(remaining - 1);
        return value;
    }
}
