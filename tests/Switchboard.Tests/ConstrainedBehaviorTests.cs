using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Switchboard.Tests;

public interface IAudited;

public sealed record AuditedCommand : IRequest<string>, IAudited;

public sealed class AuditedCommandHandler : IRequestHandler<AuditedCommand, string>
{
    public Task<string> Handle(AuditedCommand request, CancellationToken cancellationToken) => Task.FromResult("audited");
}

public sealed record AuditedVoidCommand : IRequest, IAudited;

public sealed class AuditedVoidCommandHandler : IRequestHandler<AuditedVoidCommand>
{
    public Task Handle(AuditedVoidCommand request, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Applies only to requests marked <see cref="IAudited"/>; the container skips it for everything else.</summary>
public sealed class AuditedOnlyBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IAudited
{
    private readonly Recorder _recorder;

    public AuditedOnlyBehavior(Recorder recorder) => _recorder = recorder;

    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        _recorder.Add("audit:" + typeof(TRequest).Name);
        return next(cancellationToken);
    }
}

public sealed class ConstrainedBehaviorTests
{
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddSingleton<Recorder>()
            .AddSwitchboard(cfg => cfg
                .RegisterServicesFromAssemblyContaining<ConstrainedBehaviorTests>()
                .AddOpenBehavior(typeof(AuditedOnlyBehavior<,>)))
            .BuildServiceProvider();

    [Fact]
    public async Task Constrained_behavior_runs_for_matching_typed_and_void_requests()
    {
        await using var provider = BuildProvider();
        var sender = provider.GetRequiredService<ISender>();
        var recorder = provider.GetRequiredService<Recorder>();

        Assert.Equal("audited", await sender.Send(new AuditedCommand()));
        await sender.Send(new AuditedVoidCommand());

        Assert.Equal("audit:AuditedCommand|audit:AuditedVoidCommand", recorder.Joined);
    }

    [Fact]
    public async Task Constrained_behavior_is_skipped_for_requests_that_do_not_match()
    {
        await using var provider = BuildProvider();
        var sender = provider.GetRequiredService<ISender>();
        var recorder = provider.GetRequiredService<Recorder>();

        Assert.Equal("ping pong", await sender.Send(new Ping("ping")));
        await sender.Send(new FireAndForget());
        await sender.Send((object)new AuditedCommand());

        Assert.Equal("fire-and-forget|audit:AuditedCommand", recorder.Joined);
    }
}
