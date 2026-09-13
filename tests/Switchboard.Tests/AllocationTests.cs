using System;
using Microsoft.Extensions.DependencyInjection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Switchboard.Tests;

public sealed record AllocationProbe : IRequest<string>;

/// <summary>Returns one cached task, so the handler itself allocates nothing per call.</summary>
public sealed class AllocationProbeHandler : IRequestHandler<AllocationProbe, string>
{
    private static readonly Task<string> Cached = Task.FromResult("probed");

    public Task<string> Handle(AllocationProbe request, CancellationToken cancellationToken) => Cached;
}

public sealed record VoidAllocationProbe : IRequest;

public sealed class VoidAllocationProbeHandler : IRequestHandler<VoidAllocationProbe>
{
    public Task Handle(VoidAllocationProbe request, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Shares a collection with <see cref="TelemetryTests"/> so it never runs while a tracing or metrics
/// listener is attached: an observed dispatch allocates its span and closure by design.
/// </summary>
[Collection(TelemetryTests.CollectionName)]
public sealed class AllocationTests
{
    private static long BytesPerOp(Action op)
    {
        const int warmup = 5_000;
        const int measured = 20_000;

        for (var i = 0; i < warmup; i++)
        {
            op();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < measured; i++)
        {
            op();
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / measured;
    }

    [Fact]
    public void Send_with_no_behaviors_allocates_nothing_beyond_the_handler()
    {
        using var provider = new ServiceCollection()
            .AddSwitchboard(cfg => cfg.RegisterServicesFromAssemblyContaining<AllocationTests>())
            .BuildServiceProvider();
        var sender = provider.GetRequiredService<ISender>();
        var request = new AllocationProbe();
        var voidRequest = new VoidAllocationProbe();

        var handlerOnly = BytesPerOp(() => provider.GetRequiredService<IRequestHandler<AllocationProbe, string>>());
        var send = BytesPerOp(() => sender.Send(request).GetAwaiter().GetResult());
        var voidHandlerOnly = BytesPerOp(() => provider.GetRequiredService<IRequestHandler<VoidAllocationProbe>>());
        var voidSend = BytesPerOp(() => sender.Send(voidRequest).GetAwaiter().GetResult());

        Assert.True(handlerOnly > 0, "resolving the transient handler is expected to allocate it");
        Assert.Equal(handlerOnly, send);
        Assert.Equal(voidHandlerOnly, voidSend);
    }
}
