using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace Switchboard.Benchmarks.Study;

/// <summary>
/// Which call shapes between async methods keep runtime async's advantage? Eight levels each, ending in a leaf that
/// completes or suspends once. The shapes mirror what a refactored pipeline could use:
/// <list type="bullet">
/// <item>Direct: async method awaits another async method (the best case).</item>
/// <item>Interface: async method awaits an interface call (IPipelineBehavior.Handle is one).</item>
/// <item>Delegate: async method awaits a delegate it was given (a behavior awaiting next()).</item>
/// <item>NonAsyncHop: an async method awaits a plain Task-returning method that forwards to the next level
/// (Switchboard's Pipeline.Invoke and its next() lambda today).</item>
/// </list>
/// </summary>
[Config(typeof(AbConfig))]
public class ChainShapeBenchmarks
{
    private const int Depth = 8;
    private readonly Gate _gate = new();
    private IStep _interfaceChain = null!;
    private Func<Task<int>> _delegateChain = null!;

    [Params(false, true)]
    public bool Suspend { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Variant.AssertAsBuilt();

        IStep step = new LeafStep(_gate);
        for (var i = 0; i < Depth; i++)
        {
            step = new ForwardingStep(step);
        }

        _interfaceChain = step;

        Func<Task<int>> next = Leaf;
        for (var i = 0; i < Depth; i++)
        {
            var inner = next;
            next = () => AwaitNext(inner);
        }

        _delegateChain = next;
    }

    [Benchmark(Baseline = true)]
    public int Direct() => Run(() => DirectLevel(Depth));

    [Benchmark]
    public int Interface() => Run(() => _interfaceChain.Run());

    [Benchmark]
    public int Delegate() => Run(_delegateChain);

    [Benchmark]
    public int NonAsyncHop() => Run(() => HopLevel(Depth));

    private int Run(Func<Task<int>> start)
    {
        if (!Suspend)
        {
            return start().GetAwaiter().GetResult();
        }

        _gate.Arm();
        var pending = start();
        _gate.Release();
        return pending.GetAwaiter().GetResult();
    }

    private async Task<int> Leaf()
    {
        await _gate.Wait;
        return 42;
    }

    private async Task<int> DirectLevel(int remaining)
    {
        if (remaining == 0)
        {
            return await Leaf();
        }

        var value = await DirectLevel(remaining - 1);
        return value;
    }

    private static async Task<int> AwaitNext(Func<Task<int>> next)
    {
        var value = await next();
        return value;
    }

    private async Task<int> HopLevel(int remaining)
    {
        if (remaining == 0)
        {
            return await Leaf();
        }

        var value = await Hop(remaining - 1);
        return value;
    }

    // Not async: returns the inner task as is, as Pipeline.Invoke does.
    private Task<int> Hop(int remaining) => HopLevel(remaining);

    private interface IStep
    {
        Task<int> Run();
    }

    private sealed class LeafStep(Gate gate) : IStep
    {
        public async Task<int> Run()
        {
            await gate.Wait;
            return 42;
        }
    }

    private sealed class ForwardingStep(IStep inner) : IStep
    {
        public async Task<int> Run()
        {
            var value = await inner.Run();
            return value;
        }
    }
}
