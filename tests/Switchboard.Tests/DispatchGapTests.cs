using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Switchboard.Tests;

/// <summary>A void request whose handler only finishes, or fails, once <see cref="Gate"/> completes.</summary>
public sealed record DeferredVoid(Task Gate, bool Fail = false) : IRequest;

public sealed class DeferredVoidHandler : IRequestHandler<DeferredVoid>
{
    private readonly Recorder _recorder;

    public DeferredVoidHandler(Recorder recorder) => _recorder = recorder;

    public async Task Handle(DeferredVoid request, CancellationToken cancellationToken)
    {
        await request.Gate;

        if (request.Fail)
        {
            throw new InvalidOperationException("deferred failure");
        }

        _recorder.Add("deferred:done");
    }
}

public sealed record FailingNote : INotification;

public sealed class FailingNoteHandler : INotificationHandler<FailingNote>
{
    public async Task Handle(FailingNote notification, CancellationToken cancellationToken)
    {
        await Task.Yield();
        throw new InvalidOperationException("note handler failed");
    }
}

public sealed class AfterFailingNoteHandler : INotificationHandler<FailingNote>
{
    private readonly Recorder _recorder;

    public AfterFailingNoteHandler(Recorder recorder) => _recorder = recorder;

    public Task Handle(FailingNote notification, CancellationToken cancellationToken)
    {
        _recorder.Add("after-failing-note");
        return Task.CompletedTask;
    }
}

/// <summary>A stream request that implements another, non-generic interface besides <see cref="IStreamRequest{TResponse}"/>.</summary>
public interface IStreamMarker;

public sealed class MarkedCountdown : IStreamMarker, IStreamRequest<int>
{
    public MarkedCountdown(int from) => From = from;

    public int From { get; }
}

public sealed class MarkedCountdownHandler : IStreamRequestHandler<MarkedCountdown, int>
{
    public async IAsyncEnumerable<int> Handle(MarkedCountdown request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = request.From; i > 0; i--)
        {
            await Task.Yield();
            yield return i;
        }
    }
}

public sealed record Widget(int Id);

/// <summary>Requests nested in a generic type: generic types whose own name carries no backtick.</summary>
public static class Repository<TEntity>
{
    public sealed record GetById(int Id) : IRequest<TEntity>;
}

public sealed class GetWidgetByIdHandler : IRequestHandler<Repository<Widget>.GetById, Widget>
{
    public Task<Widget> Handle(Repository<Widget>.GetById request, CancellationToken cancellationToken)
        => Task.FromResult(new Widget(request.Id));
}

public sealed class DispatchGapTests
{
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddSingleton<Recorder>()
            .AddSwitchboard(cfg => cfg.RegisterServicesFromAssemblyContaining<DispatchGapTests>())
            .BuildServiceProvider();

    [Fact]
    public async Task Send_of_a_void_request_waits_for_a_handler_that_completes_later()
    {
        await using var provider = BuildProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var send = provider.GetRequiredService<ISender>().Send(new DeferredVoid(gate.Task));

        Assert.False(send.IsCompleted);
        gate.SetResult();
        await send;
        Assert.Equal("deferred:done", provider.GetRequiredService<Recorder>().Joined);
    }

    [Fact]
    public async Task Send_of_a_void_request_surfaces_a_failure_from_an_async_handler()
    {
        await using var provider = BuildProvider();
        var sender = provider.GetRequiredService<ISender>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Faulted before Send returns, and faulted later.
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.Send(new DeferredVoid(Task.CompletedTask, Fail: true)));
        var later = sender.Send(new DeferredVoid(gate.Task, Fail: true));
        gate.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => later);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.Send((object)new DeferredVoid(Task.CompletedTask, Fail: true)));
    }

    [Fact]
    public async Task A_failing_notification_handler_fails_Publish_and_the_handlers_after_it_do_not_run()
    {
        await using var provider = new ServiceCollection()
            .AddSingleton<Recorder>()
            .AddSwitchboard(_ => { })
            .AddTransient<INotificationHandler<FailingNote>, FailingNoteHandler>()
            .AddTransient<INotificationHandler<FailingNote>, AfterFailingNoteHandler>()
            .BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetRequiredService<IPublisher>().Publish(new FailingNote()));

        Assert.Equal("note handler failed", exception.Message);
        Assert.Empty(provider.GetRequiredService<Recorder>().Entries);
    }

    [Fact]
    public async Task Publish_of_an_object_that_is_not_a_notification_names_the_parameter()
    {
        await using var provider = BuildProvider();

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GetRequiredService<IPublisher>().Publish(new Ping("not a notification")));

        Assert.Equal("notification", exception.ParamName);
    }

    [Fact]
    public async Task A_stream_request_that_implements_other_interfaces_too_streams_its_items()
    {
        await using var provider = BuildProvider();
        var sender = provider.GetRequiredService<ISender>();

        Assert.Equal(new[] { 3, 2, 1 }, await sender.CreateStream(new MarkedCountdown(3)).Collect());
        Assert.Equal(new object?[] { 2, 1 }, await sender.CreateStream((object)new MarkedCountdown(2)).Collect());
    }

    [Fact]
    public async Task A_request_nested_in_a_generic_type_is_dispatched_and_named_with_its_type_arguments()
    {
        await using var provider = BuildProvider();

        var widget = await provider.GetRequiredService<ISender>().Send(new Repository<Widget>.GetById(7));

        Assert.Equal(new Widget(7), widget);
        Assert.Equal("GetById<Widget>", SwitchboardTelemetry.DisplayName(typeof(Repository<Widget>.GetById)));
    }

    [Fact]
    public void UseScopePerDispatch_rejects_a_null_callback()
    {
        var configuration = new SwitchboardConfiguration();

        var sync = Assert.Throws<ArgumentNullException>(() => configuration.UseScopePerDispatch((Action<DispatchScope>)null!));
        var async = Assert.Throws<ArgumentNullException>(
            () => configuration.UseScopePerDispatch((Func<DispatchScope, CancellationToken, ValueTask>)null!));

        Assert.Equal("onScopeCreated", sync.ParamName);
        Assert.Equal("onScopeCreated", async.ParamName);
    }

    [Fact]
    public void AddSwitchboard_rejects_null_services_before_running_the_configuration()
    {
        var configured = false;

        var exception = Assert.Throws<ArgumentNullException>(
            () => SwitchboardServiceCollectionExtensions.AddSwitchboard(null!, _ => configured = true));

        Assert.Equal("services", exception.ParamName);
        Assert.False(configured);
    }
}
