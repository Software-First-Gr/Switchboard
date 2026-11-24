using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Switchboard;

namespace Switchboard.Benchmarks;

[Config(typeof(RuntimeAsyncConfig))]
[MemoryDiagnoser]
public class RequestDispatchBenchmarks
{
    private ServiceProvider? _provider;
    private ISender _sender = null!;

    [Params(false, true)]
    public bool ScopePerDispatch { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        RuntimeAsyncGuard.AssertExpectedMode();

        var services = new ServiceCollection()
            .AddSingleton<IRequestHandler<CompletedRequest, int>, CompletedRequestHandler>()
            .AddSingleton<IRequestHandler<GatedRequest, int>, GatedRequestHandler>();

        services.AddSwitchboard(cfg =>
        {
            if (ScopePerDispatch)
            {
                cfg.UseScopePerDispatch();
            }
        });

        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        _sender = _provider.GetRequiredService<ISender>();

        if (await _sender.Send(new CompletedRequest()) != 42)
        {
            throw new InvalidOperationException("Completed request setup returned an unexpected response.");
        }

        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = _sender.Send(new GatedRequest(release.Task));
        if (pending.IsCompleted)
        {
            throw new InvalidOperationException("The gated request did not suspend before its caller released it.");
        }

        release.SetResult(42);
        if (await pending != 42)
        {
            throw new InvalidOperationException("Gated request setup returned an unexpected response.");
        }
    }

    [Benchmark(Baseline = true)]
    public Task<int> CompletedRequest()
        => _sender.Send(new CompletedRequest());

    [Benchmark]
    public async Task<int> CallerReleasedSuspension()
    {
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = _sender.Send(new GatedRequest(release.Task));
        if (pending.IsCompleted)
        {
            throw new InvalidOperationException("The gated request completed before the caller released it.");
        }

        release.SetResult(42);
        return await pending.ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }
}

public sealed record CompletedRequest : IRequest<int>;

public sealed class CompletedRequestHandler : IRequestHandler<CompletedRequest, int>
{
    private static readonly Task<int> Response = Task.FromResult(42);

    public Task<int> Handle(CompletedRequest request, CancellationToken cancellationToken)
        => Response;
}

public sealed class GatedRequest : IRequest<int>
{
    public GatedRequest(Task<int> completion)
        => Completion = completion;

    public Task<int> Completion { get; }
}

public sealed class GatedRequestHandler : IRequestHandler<GatedRequest, int>
{
    public Task<int> Handle(GatedRequest request, CancellationToken cancellationToken)
        => request.Completion;
}