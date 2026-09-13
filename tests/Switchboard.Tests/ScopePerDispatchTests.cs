using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Switchboard.Tests;

/// <summary>Stands in for a scoped DbContext: one instance per scope, disposed with it.</summary>
public sealed class ScopeMarker : IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();

    public string? SeededUser { get; set; }

    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;
}

public sealed record ScopeProbe : IRequest<ScopeMarker>;

public sealed class ScopeProbeHandler : IRequestHandler<ScopeProbe, ScopeMarker>
{
    private readonly ScopeMarker _marker;

    public ScopeProbeHandler(ScopeMarker marker) => _marker = marker;

    public Task<ScopeMarker> Handle(ScopeProbe request, CancellationToken cancellationToken) => Task.FromResult(_marker);
}

public sealed record NestedScopeProbe : IRequest<(ScopeMarker Outer, ScopeMarker Inner)>;

public sealed class NestedScopeProbeHandler : IRequestHandler<NestedScopeProbe, (ScopeMarker Outer, ScopeMarker Inner)>
{
    private readonly ScopeMarker _marker;
    private readonly ISender _sender;

    public NestedScopeProbeHandler(ScopeMarker marker, ISender sender)
    {
        _marker = marker;
        _sender = sender;
    }

    public async Task<(ScopeMarker Outer, ScopeMarker Inner)> Handle(NestedScopeProbe request, CancellationToken cancellationToken)
        => (_marker, await _sender.Send(new ScopeProbe(), cancellationToken));
}

public sealed record ScopeNote(ConcurrentQueue<ScopeMarker> Seen) : INotification;

public sealed class ScopeNoteHandler : INotificationHandler<ScopeNote>
{
    private readonly ScopeMarker _marker;

    public ScopeNoteHandler(ScopeMarker marker) => _marker = marker;

    public Task Handle(ScopeNote notification, CancellationToken cancellationToken)
    {
        notification.Seen.Enqueue(_marker);
        return Task.CompletedTask;
    }
}

public sealed class ScopePerDispatchTests
{
    private static ServiceProvider BuildProvider(Action<SwitchboardConfiguration>? configure = null) =>
        new ServiceCollection()
            .AddScoped<ScopeMarker>()
            .AddSwitchboard(cfg =>
            {
                cfg.RegisterServicesFromAssemblyContaining<ScopePerDispatchTests>();
                configure?.Invoke(cfg);
            })
            .BuildServiceProvider(validateScopes: true);

    [Fact]
    public async Task By_default_handlers_share_the_callers_scope()
    {
        await using var provider = BuildProvider();
        await using var callerScope = provider.CreateAsyncScope();
        var callerMarker = callerScope.ServiceProvider.GetRequiredService<ScopeMarker>();

        var handled = await callerScope.ServiceProvider.GetRequiredService<ISender>().Send(new ScopeProbe());

        Assert.Same(callerMarker, handled);
    }

    [Fact]
    public async Task Each_top_level_send_gets_its_own_scope_which_is_disposed_afterwards()
    {
        await using var provider = BuildProvider(cfg => cfg.UseScopePerDispatch());
        await using var callerScope = provider.CreateAsyncScope();
        var callerMarker = callerScope.ServiceProvider.GetRequiredService<ScopeMarker>();
        var sender = callerScope.ServiceProvider.GetRequiredService<ISender>();

        var first = await sender.Send(new ScopeProbe());
        var second = await sender.Send(new ScopeProbe());

        Assert.NotSame(callerMarker, first);
        Assert.NotSame(first, second);
        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
        Assert.False(callerMarker.Disposed);
    }

    [Fact]
    public async Task Concurrent_sends_from_one_caller_never_share_a_scope()
    {
        await using var provider = BuildProvider(cfg => cfg.UseScopePerDispatch());
        await using var callerScope = provider.CreateAsyncScope();
        var sender = callerScope.ServiceProvider.GetRequiredService<ISender>();

        var markers = await Task.WhenAll(sender.Send(new ScopeProbe()), sender.Send(new ScopeProbe()));

        Assert.NotSame(markers[0], markers[1]);
    }

    [Fact]
    public async Task A_nested_send_reuses_the_scope_of_the_dispatch_in_flight()
    {
        await using var provider = BuildProvider(cfg => cfg.UseScopePerDispatch());
        await using var callerScope = provider.CreateAsyncScope();

        var (outer, inner) = await callerScope.ServiceProvider.GetRequiredService<ISender>().Send(new NestedScopeProbe());

        Assert.Same(outer, inner);
    }

    [Fact]
    public async Task Publish_also_runs_in_its_own_scope()
    {
        await using var provider = BuildProvider(cfg => cfg.UseScopePerDispatch());
        await using var callerScope = provider.CreateAsyncScope();
        var callerMarker = callerScope.ServiceProvider.GetRequiredService<ScopeMarker>();
        var note = new ScopeNote(new ConcurrentQueue<ScopeMarker>());

        await callerScope.ServiceProvider.GetRequiredService<IPublisher>().Publish(note);

        var seen = Assert.Single(note.Seen);
        Assert.NotSame(callerMarker, seen);
    }

    [Fact]
    public async Task The_callback_carries_state_from_the_callers_scope_into_the_new_one()
    {
        await using var provider = BuildProvider(cfg => cfg.UseScopePerDispatch(scope =>
        {
            var caller = scope.Parent.GetRequiredService<ScopeMarker>();
            scope.ServiceProvider.GetRequiredService<ScopeMarker>().SeededUser = "user-of-" + caller.Id;
            Assert.IsType<ScopeProbe>(scope.Message);
        }));
        await using var callerScope = provider.CreateAsyncScope();
        var callerMarker = callerScope.ServiceProvider.GetRequiredService<ScopeMarker>();

        var handled = await callerScope.ServiceProvider.GetRequiredService<ISender>().Send(new ScopeProbe());

        Assert.Equal("user-of-" + callerMarker.Id, handled.SeededUser);
    }

    [Fact]
    public async Task The_async_callback_is_awaited_before_the_handler_runs()
    {
        await using var provider = BuildProvider(cfg => cfg.UseScopePerDispatch(async (scope, cancellationToken) =>
        {
            await Task.Yield();
            scope.ServiceProvider.GetRequiredService<ScopeMarker>().SeededUser = "seeded";
        }));
        await using var callerScope = provider.CreateAsyncScope();

        var handled = await callerScope.ServiceProvider.GetRequiredService<ISender>().Send((object)new ScopeProbe());

        Assert.Equal("seeded", Assert.IsType<ScopeMarker>(handled).SeededUser);
    }
}
