using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Switchboard.Tests;

/// <summary>Never registered, on purpose: a handler that asks for it cannot be constructed.</summary>
public interface IUnregisteredDependency;

public sealed record Constructed : IRequest<string>;

/// <summary>Records its own construction, so a test can tell when the container built it.</summary>
public sealed class ConstructedHandler : IRequestHandler<Constructed, string>
{
    private readonly Recorder _recorder;

    public ConstructedHandler(Recorder recorder)
    {
        _recorder = recorder;
        _recorder.Add("handler:ctor");
    }

    public Task<string> Handle(Constructed request, CancellationToken cancellationToken)
    {
        _recorder.Add("handler:handle");
        return Task.FromResult("handled");
    }
}

public sealed record VoidConstructed : IRequest;

public sealed class VoidConstructedHandler : IRequestHandler<VoidConstructed>
{
    private readonly Recorder _recorder;

    public VoidConstructedHandler(Recorder recorder)
    {
        _recorder = recorder;
        _recorder.Add("handler:ctor");
    }

    public Task Handle(VoidConstructed request, CancellationToken cancellationToken)
    {
        _recorder.Add("handler:handle");
        return Task.CompletedTask;
    }
}

public sealed record Unconstructible : IRequest<string>;

/// <summary>Registered by assembly scanning like any other handler, but the container cannot build it.</summary>
public sealed class UnconstructibleHandler : IRequestHandler<Unconstructible, string>
{
    public UnconstructibleHandler(IUnregisteredDependency dependency)
    {
    }

    public Task<string> Handle(Unconstructible request, CancellationToken cancellationToken)
        => Task.FromResult("unreachable");
}

public sealed record VoidUnconstructible : IRequest;

public sealed class VoidUnconstructibleHandler : IRequestHandler<VoidUnconstructible>
{
    public VoidUnconstructibleHandler(IUnregisteredDependency dependency)
    {
    }

    public Task Handle(VoidUnconstructible request, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A void request with no registered handler, on purpose.</summary>
public sealed record VoidOrphan : IRequest;

/// <summary>What a logging or metrics behavior does: records the failure of whatever is inside it and lets it through.</summary>
public sealed class ObservingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    private readonly Recorder _recorder;

    public ObservingBehavior(Recorder recorder) => _recorder = recorder;

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        _recorder.Add("observe:before");

        try
        {
            var response = await next(cancellationToken);
            _recorder.Add("observe:after");
            return response;
        }
        catch (Exception exception)
        {
            _recorder.Add("observe:caught " + exception.GetType().Name);
            throw;
        }
    }
}

public sealed class RequestRejectedException : Exception
{
    public RequestRejectedException()
        : base("rejected")
    {
    }
}

/// <summary>What a validation behavior does with an invalid request: fails it instead of calling <c>next</c>.</summary>
public sealed class RejectingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        => Task.FromException<TResponse>(new RequestRejectedException());
}

/// <summary>What a caching behavior does on a hit: answers without calling <c>next</c>.</summary>
public sealed class AnsweringBehavior : IPipelineBehavior<Constructed, string>
{
    public Task<string> Handle(Constructed request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        => Task.FromResult("answered");
}

/// <summary>Calls <c>next</c> twice, the way a retry behavior does after a transient failure.</summary>
public sealed class TwiceBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        await next(cancellationToken);
        return await next(cancellationToken);
    }
}

/// <summary>Not an async method: whatever <c>next</c> throws or returns passes straight through it.</summary>
public sealed class PassThroughBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        => next(cancellationToken);
}

/// <summary>
/// The handler is resolved by the innermost step of the pipeline, as in MediatR, not before the pipeline is
/// entered. Shares a collection with <see cref="TelemetryTests"/> so it never runs while a tracing or metrics
/// listener is attached: an observed dispatch reports every failure through its task, and some tests below
/// pin the ones <c>Send</c> throws itself.
/// </summary>
[Collection(TelemetryTests.CollectionName)]
public sealed class HandlerResolutionTests
{
    private const string ThrownBySend = "thrown by Send: ";
    private const string CarriedByTheTask = "carried by the task: ";

    private static ServiceProvider BuildProvider(Action<SwitchboardConfiguration>? configure = null) =>
        new ServiceCollection()
            .AddSingleton<Recorder>()
            .AddSwitchboard(cfg =>
            {
                cfg.RegisterServicesFromAssemblyContaining<HandlerResolutionTests>();
                configure?.Invoke(cfg);
            })
            .BuildServiceProvider();

    /// <summary>How a dispatch ended for a caller: a caller that does not await only ever sees what <c>Send</c> throws itself.</summary>
    private static async Task<string> Outcome(Func<Task> send)
    {
        Task task;

        try
        {
            task = send();
        }
        catch (Exception exception)
        {
            return ThrownBySend + exception.GetType().Name;
        }

        try
        {
            await task;
            return "completed";
        }
        catch (Exception exception)
        {
            return CarriedByTheTask + exception.GetType().Name;
        }
    }

    private static void AssertNoListenerIsAttached() =>
        Assert.False(
            SwitchboardTelemetry.IsRequestObserved,
            "a tracing or metrics listener is still attached, so every dispatch is observed and reports its failures through the task");

    // --- (a) Behaviors run before the handler is constructed ----------------------------------

    private const string BehaviorsThenHandler = "first:before|second:before|handler:ctor|handler:handle|second:after|first:after";

    private static async Task<string> RecordedInsideTwoBehaviors(Func<ISender, Task> send)
    {
        await using var provider = BuildProvider(cfg => cfg
            .AddOpenBehavior(typeof(FirstBehavior<,>))
            .AddOpenBehavior(typeof(SecondBehavior<,>)));

        await send(provider.GetRequiredService<ISender>());

        return provider.GetRequiredService<Recorder>().Joined;
    }

    [Fact]
    public async Task Behaviors_run_before_the_handler_is_constructed()
        => Assert.Equal(BehaviorsThenHandler, await RecordedInsideTwoBehaviors(sender => sender.Send(new Constructed())));

    [Fact]
    public async Task Behaviors_run_before_the_handler_of_a_void_request_is_constructed()
        => Assert.Equal(BehaviorsThenHandler, await RecordedInsideTwoBehaviors(sender => sender.Send(new VoidConstructed())));

    [Fact]
    public async Task Behaviors_run_before_the_handler_of_an_untyped_send_is_constructed()
    {
        Assert.Equal(BehaviorsThenHandler, await RecordedInsideTwoBehaviors(sender => sender.Send((object)new Constructed())));
        Assert.Equal(BehaviorsThenHandler, await RecordedInsideTwoBehaviors(sender => sender.Send((object)new VoidConstructed())));
    }

    [Fact]
    public async Task Behaviors_run_before_the_handler_of_a_covariant_send_is_constructed()
        => Assert.Equal(BehaviorsThenHandler, await RecordedInsideTwoBehaviors(sender => sender.Send<object>(new Constructed())));

    [Fact]
    public async Task With_scope_per_dispatch_the_handler_is_still_constructed_inside_the_behaviors()
    {
        await using var provider = BuildProvider(cfg => cfg
            .UseScopePerDispatch()
            .AddOpenBehavior(typeof(FirstBehavior<,>))
            .AddOpenBehavior(typeof(SecondBehavior<,>)));

        await provider.GetRequiredService<ISender>().Send(new Constructed());

        Assert.Equal(BehaviorsThenHandler, provider.GetRequiredService<Recorder>().Joined);
    }

    [Fact]
    public async Task A_behavior_that_calls_next_again_gets_a_handler_resolved_again()
    {
        await using var provider = BuildProvider(cfg => cfg.AddOpenBehavior(typeof(TwiceBehavior<,>)));

        await provider.GetRequiredService<ISender>().Send(new Constructed());

        Assert.Equal("handler:ctor|handler:handle|handler:ctor|handler:handle", provider.GetRequiredService<Recorder>().Joined);
    }

    // --- (b) A handler that cannot be resolved fails inside the pipeline ----------------------

    private static async Task AssertTheOutermostBehaviorCatchesIt(Func<ISender, Task> send, string namedInTheMessage)
    {
        await using var provider = BuildProvider(cfg => cfg
            .AddOpenBehavior(typeof(ObservingBehavior<,>))
            .AddOpenBehavior(typeof(SecondBehavior<,>)));
        var sender = provider.GetRequiredService<ISender>();

        // Not wrapped in Assert.ThrowsAsync: were Send to throw the exception itself, the test would fail right here.
        var task = send(sender);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Contains(namedInTheMessage, exception.Message);
        Assert.Equal(
            "observe:before|second:before|observe:caught InvalidOperationException",
            provider.GetRequiredService<Recorder>().Joined);
    }

    [Fact]
    public Task An_unresolvable_handler_dependency_is_a_faulted_task_the_outermost_behavior_catches()
        => AssertTheOutermostBehaviorCatchesIt(sender => sender.Send(new Unconstructible()), nameof(IUnregisteredDependency));

    [Fact]
    public Task An_unresolvable_dependency_of_a_void_handler_is_a_faulted_task_the_outermost_behavior_catches()
        => AssertTheOutermostBehaviorCatchesIt(sender => sender.Send(new VoidUnconstructible()), nameof(IUnregisteredDependency));

    [Fact]
    public Task An_unresolvable_handler_dependency_of_an_untyped_send_is_a_faulted_task_the_outermost_behavior_catches()
        => AssertTheOutermostBehaviorCatchesIt(sender => sender.Send((object)new Unconstructible()), nameof(IUnregisteredDependency));

    [Fact]
    public Task An_unresolvable_dependency_of_a_void_handler_sent_untyped_is_a_faulted_task_the_outermost_behavior_catches()
        => AssertTheOutermostBehaviorCatchesIt(sender => sender.Send((object)new VoidUnconstructible()), nameof(IUnregisteredDependency));

    [Fact]
    public Task A_missing_handler_is_a_faulted_task_the_outermost_behavior_catches()
        => AssertTheOutermostBehaviorCatchesIt(sender => sender.Send(new Orphan()), nameof(Orphan));

    [Fact]
    public Task A_missing_handler_of_a_void_request_is_a_faulted_task_the_outermost_behavior_catches()
        => AssertTheOutermostBehaviorCatchesIt(sender => sender.Send(new VoidOrphan()), nameof(VoidOrphan));

    [Fact]
    public Task A_missing_handler_of_an_untyped_send_is_a_faulted_task_the_outermost_behavior_catches()
        => AssertTheOutermostBehaviorCatchesIt(sender => sender.Send((object)new Orphan()), nameof(Orphan));

    // --- (c) A request a behavior stops never constructs its handler --------------------------

    [Fact]
    public async Task A_request_rejected_by_a_behavior_never_constructs_its_handler()
    {
        await using var provider = BuildProvider(cfg => cfg
            .AddOpenBehavior(typeof(ObservingBehavior<,>))
            .AddOpenBehavior(typeof(RejectingBehavior<,>)));

        await Assert.ThrowsAsync<RequestRejectedException>(() => provider.GetRequiredService<ISender>().Send(new Constructed()));

        Assert.Equal("observe:before|observe:caught RequestRejectedException", provider.GetRequiredService<Recorder>().Joined);
    }

    [Fact]
    public async Task A_void_request_rejected_by_a_behavior_never_constructs_its_handler()
    {
        await using var provider = BuildProvider(cfg => cfg
            .AddOpenBehavior(typeof(ObservingBehavior<,>))
            .AddOpenBehavior(typeof(RejectingBehavior<,>)));

        await Assert.ThrowsAsync<RequestRejectedException>(() => provider.GetRequiredService<ISender>().Send(new VoidConstructed()));

        Assert.Equal("observe:before|observe:caught RequestRejectedException", provider.GetRequiredService<Recorder>().Joined);
    }

    [Fact]
    public async Task A_rejected_request_does_not_even_need_a_handler_that_can_be_constructed()
    {
        await using var provider = BuildProvider(cfg => cfg.AddOpenBehavior(typeof(RejectingBehavior<,>)));

        await Assert.ThrowsAsync<RequestRejectedException>(() => provider.GetRequiredService<ISender>().Send(new Unconstructible()));
    }

    [Fact]
    public async Task A_request_answered_by_a_behavior_never_constructs_its_handler()
    {
        await using var provider = BuildProvider(cfg => cfg
            .AddOpenBehavior(typeof(ObservingBehavior<,>))
            .AddBehavior<AnsweringBehavior>());

        var response = await provider.GetRequiredService<ISender>().Send(new Constructed());

        Assert.Equal("answered", response);
        Assert.Equal("observe:before|observe:after", provider.GetRequiredService<Recorder>().Joined);
    }

    // --- What a caller that does not await sees ------------------------------------------------
    //
    // Pinned so that it only ever changes on purpose. The exception is raised where the handler is resolved,
    // like anything a handler throws before its first await: Send throws it itself unless an async method on
    // the way out (a behavior, the untyped or covariant Send, telemetry, scope-per-dispatch) turns it into a
    // faulted task.

    [Fact]
    public async Task With_no_behaviors_a_missing_handler_is_still_thrown_by_Send_itself()
    {
        await using var provider = BuildProvider();
        var sender = provider.GetRequiredService<ISender>();
        AssertNoListenerIsAttached();

        Assert.Equal(ThrownBySend + nameof(InvalidOperationException), await Outcome(() => sender.Send(new Orphan())));
        Assert.Equal(ThrownBySend + nameof(InvalidOperationException), await Outcome(() => sender.Send(new VoidOrphan())));
    }

    [Fact]
    public async Task With_no_behaviors_an_unresolvable_handler_dependency_is_still_thrown_by_Send_itself()
    {
        await using var provider = BuildProvider();
        var sender = provider.GetRequiredService<ISender>();
        AssertNoListenerIsAttached();

        Assert.Equal(ThrownBySend + nameof(InvalidOperationException), await Outcome(() => sender.Send(new Unconstructible())));
        Assert.Equal(ThrownBySend + nameof(InvalidOperationException), await Outcome(() => sender.Send(new VoidUnconstructible())));
    }

    [Fact]
    public async Task With_no_behaviors_untyped_and_covariant_sends_still_carry_the_failure_in_their_task()
    {
        await using var provider = BuildProvider();
        var sender = provider.GetRequiredService<ISender>();

        Assert.Equal(CarriedByTheTask + nameof(InvalidOperationException), await Outcome(() => sender.Send((object)new Orphan())));
        Assert.Equal(CarriedByTheTask + nameof(InvalidOperationException), await Outcome(() => sender.Send((object)new VoidOrphan())));
        Assert.Equal(CarriedByTheTask + nameof(InvalidOperationException), await Outcome(() => sender.Send<object>(new Unconstructible())));
    }

    [Fact]
    public async Task An_async_behavior_turns_a_missing_handler_into_a_faulted_task()
    {
        await using var provider = BuildProvider(cfg => cfg.AddOpenBehavior(typeof(ObservingBehavior<,>)));
        var sender = provider.GetRequiredService<ISender>();

        Assert.Equal(CarriedByTheTask + nameof(InvalidOperationException), await Outcome(() => sender.Send(new Orphan())));
        Assert.Equal(CarriedByTheTask + nameof(InvalidOperationException), await Outcome(() => sender.Send(new VoidOrphan())));
    }

    [Fact]
    public async Task A_behavior_that_is_not_an_async_method_lets_a_missing_handler_be_thrown_by_Send_itself()
    {
        await using var provider = BuildProvider(cfg => cfg.AddOpenBehavior(typeof(PassThroughBehavior<,>)));
        var sender = provider.GetRequiredService<ISender>();
        AssertNoListenerIsAttached();

        Assert.Equal(ThrownBySend + nameof(InvalidOperationException), await Outcome(() => sender.Send(new Orphan())));
        Assert.Equal(ThrownBySend + nameof(InvalidOperationException), await Outcome(() => sender.Send(new VoidOrphan())));
    }
}
