using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Switchboard.Tests;

public sealed record Tracked(bool ShortCircuit = false) : IRequest<string>;

public sealed class TrackedHandler : IRequestHandler<Tracked, string>
{
    private readonly Recorder _recorder;

    public TrackedHandler(Recorder recorder) => _recorder = recorder;

    public Task<string> Handle(Tracked request, CancellationToken cancellationToken)
    {
        _recorder.Add("handler");
        return Task.FromResult("handled");
    }
}

public sealed record VoidTracked : IRequest;

public sealed class VoidTrackedHandler : IRequestHandler<VoidTracked>
{
    private readonly Recorder _recorder;

    public VoidTrackedHandler(Recorder recorder) => _recorder = recorder;

    public Task Handle(VoidTracked request, CancellationToken cancellationToken)
    {
        _recorder.Add("handler");
        return Task.CompletedTask;
    }
}

public sealed class FirstBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    private readonly Recorder _recorder;

    public FirstBehavior(Recorder recorder) => _recorder = recorder;

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        _recorder.Add("first:before");
        var response = await next(cancellationToken);
        _recorder.Add("first:after");
        return response;
    }
}

public sealed class SecondBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    private readonly Recorder _recorder;

    public SecondBehavior(Recorder recorder) => _recorder = recorder;

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        _recorder.Add("second:before");
        var response = await next(cancellationToken);
        _recorder.Add("second:after");
        return response;
    }
}

public sealed class ClosedTrackedBehavior : IPipelineBehavior<Tracked, string>
{
    private readonly Recorder _recorder;

    public ClosedTrackedBehavior(Recorder recorder) => _recorder = recorder;

    public async Task<string> Handle(Tracked request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
    {
        _recorder.Add("closed:before");
        var response = await next(cancellationToken);
        _recorder.Add("closed:after");
        return response;
    }
}

public sealed class ShortCircuitBehavior : IPipelineBehavior<Tracked, string>
{
    public Task<string> Handle(Tracked request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        => request.ShortCircuit ? Task.FromResult("short-circuited") : next(cancellationToken);
}

public sealed record TokenProbe : IRequest<CancellationToken>;

public sealed class TokenProbeHandler : IRequestHandler<TokenProbe, CancellationToken>
{
    public Task<CancellationToken> Handle(TokenProbe request, CancellationToken cancellationToken)
        => Task.FromResult(cancellationToken);
}

/// <summary>Deliberately calls <c>next()</c> without forwarding the token.</summary>
public sealed class TokenDroppingBehavior : IPipelineBehavior<TokenProbe, CancellationToken>
{
    public Task<CancellationToken> Handle(TokenProbe request, RequestHandlerDelegate<CancellationToken> next, CancellationToken cancellationToken)
        => next();
}

public sealed class PipelineBehaviorTests
{
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddSingleton<Recorder>()
            .AddSwitchboard(cfg => cfg
                .RegisterServicesFromAssemblyContaining<PipelineBehaviorTests>()
                .AddOpenBehavior(typeof(FirstBehavior<,>))
                .AddOpenBehavior(typeof(SecondBehavior<,>)))
            .BuildServiceProvider();

    [Fact]
    public async Task Behaviors_run_in_registration_order_outermost_first()
    {
        await using var provider = BuildProvider();
        var sender = provider.GetRequiredService<ISender>();
        var recorder = provider.GetRequiredService<Recorder>();

        var response = await sender.Send(new Tracked());

        Assert.Equal("handled", response);
        Assert.Equal("first:before|second:before|handler|second:after|first:after", recorder.Joined);
    }

    [Fact]
    public async Task Behaviors_wrap_void_requests_through_unit()
    {
        await using var provider = BuildProvider();
        var sender = provider.GetRequiredService<ISender>();
        var recorder = provider.GetRequiredService<Recorder>();

        await sender.Send(new VoidTracked());

        Assert.Equal("first:before|second:before|handler|second:after|first:after", recorder.Joined);
    }

    [Fact]
    public async Task Behavior_can_short_circuit_without_reaching_handler()
    {
        await using var provider = new ServiceCollection()
            .AddSingleton<Recorder>()
            .AddSwitchboard(cfg => cfg.RegisterServicesFromAssemblyContaining<PipelineBehaviorTests>())
            .AddTransient<IPipelineBehavior<Tracked, string>, ShortCircuitBehavior>()
            .BuildServiceProvider();
        var sender = provider.GetRequiredService<ISender>();
        var recorder = provider.GetRequiredService<Recorder>();

        var response = await sender.Send(new Tracked(ShortCircuit: true));

        Assert.Equal("short-circuited", response);
        Assert.Equal("", recorder.Joined);
    }

    [Fact]
    public async Task AddBehavior_registers_a_closed_behavior_and_it_runs()
    {
        await using var provider = new ServiceCollection()
            .AddSingleton<Recorder>()
            .AddSwitchboard(cfg => cfg
                .RegisterServicesFromAssemblyContaining<PipelineBehaviorTests>()
                .AddBehavior<ClosedTrackedBehavior>())
            .BuildServiceProvider();
        var sender = provider.GetRequiredService<ISender>();
        var recorder = provider.GetRequiredService<Recorder>();

        var response = await sender.Send(new Tracked());

        Assert.Equal("handled", response);
        Assert.Equal("closed:before|handler|closed:after", recorder.Joined);
    }

    [Fact]
    public async Task Open_and_closed_behaviors_share_one_ordering()
    {
        await using var provider = new ServiceCollection()
            .AddSingleton<Recorder>()
            .AddSwitchboard(cfg => cfg
                .RegisterServicesFromAssemblyContaining<PipelineBehaviorTests>()
                .AddOpenBehavior(typeof(FirstBehavior<,>))
                .AddBehavior<ClosedTrackedBehavior>()
                .AddOpenBehavior(typeof(SecondBehavior<,>)))
            .BuildServiceProvider();
        var sender = provider.GetRequiredService<ISender>();
        var recorder = provider.GetRequiredService<Recorder>();

        await sender.Send(new Tracked());

        Assert.Equal(
            "first:before|closed:before|second:before|handler|second:after|closed:after|first:after",
            recorder.Joined);
    }

    [Fact]
    public async Task Closed_behavior_does_not_apply_to_other_request_types()
    {
        await using var provider = new ServiceCollection()
            .AddSingleton<Recorder>()
            .AddSwitchboard(cfg => cfg
                .RegisterServicesFromAssemblyContaining<PipelineBehaviorTests>()
                .AddBehavior<ClosedTrackedBehavior>())
            .BuildServiceProvider();
        var sender = provider.GetRequiredService<ISender>();
        var recorder = provider.GetRequiredService<Recorder>();

        var response = await sender.Send(new Ping("ping"));

        Assert.Equal("ping pong", response);
        Assert.Equal("", recorder.Joined);
    }

    [Fact]
    public async Task Original_token_flows_to_handler_even_when_behavior_drops_it()
    {
        await using var provider = new ServiceCollection()
            .AddSwitchboard(cfg => cfg.RegisterServicesFromAssemblyContaining<PipelineBehaviorTests>())
            .AddTransient<IPipelineBehavior<TokenProbe, CancellationToken>, TokenDroppingBehavior>()
            .BuildServiceProvider();
        var sender = provider.GetRequiredService<ISender>();
        using var cts = new CancellationTokenSource();

        var received = await sender.Send(new TokenProbe(), cts.Token);

        Assert.True(received == cts.Token, "handler should receive the original token");
        Assert.False(received == CancellationToken.None, "token should not degrade to default");
    }
}
