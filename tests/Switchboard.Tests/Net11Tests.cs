using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Tracing;
using Xunit;

namespace Switchboard.Tests;

public sealed record StackProbe : IRequest<StackTrace>;

public sealed class StackProbeHandler : IRequestHandler<StackProbe, StackTrace>
{
    // Captured before the handler's first await, so the frames below it are the dispatch path as it runs.
    public Task<StackTrace> Handle(StackProbe request, CancellationToken cancellationToken) => Task.FromResult(new StackTrace());
}

/// <summary>
/// Switchboard is compiled with runtime-native async (see Switchboard.csproj). These fail if the switch silently drops
/// off, for example when the project file is edited or the build is run with UseRuntimeAsync=false.
/// </summary>
public sealed class RuntimeAsyncTests
{

    [Theory]
    [InlineData(typeof(Mediator), "DispatchInNewScope")]
    [InlineData(typeof(Mediator), "SendCovariant")]
    [InlineData(typeof(SwitchboardTelemetry), "Observe")]
    [InlineData(typeof(NotificationHandlerWrapperImpl<>), "Run")]
    public void Async_methods_use_runtime_async(Type type, string method)
    {
        var info = type.GetMethod(method, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;

        // The compiler marks the methods it rewrites into a state machine; runtime-async methods are left as written.
        Assert.False(info.IsDefined(typeof(AsyncStateMachineAttribute)));
    }

    [Fact]
    public async Task Live_stack_traces_show_the_dispatch_path_without_state_machine_frames()
    {
        await using var provider = new ServiceCollection()
            .AddSwitchboard(cfg => cfg.RegisterServicesFromAssemblyContaining<RuntimeAsyncTests>().UseScopePerDispatch())
            .BuildServiceProvider();

        var trace = await provider.GetRequiredService<ISender>().Send(new StackProbe());

        var switchboardFrames = trace.GetFrames()
            .Select(f => f.GetMethod())
            .Where(m => m?.DeclaringType?.Assembly == typeof(Mediator).Assembly)
            .ToList();

        // The async scope-per-dispatch wrapper is on the path under its own name ...
        Assert.Contains(switchboardFrames, m => m!.Name == "DispatchInNewScope");

        // ... and not as a compiler-generated <DispatchInNewScope>d__N.MoveNext under a stub that starts it.
        Assert.DoesNotContain(switchboardFrames, m => typeof(IAsyncStateMachine).IsAssignableFrom(m!.DeclaringType));
    }
}

/// <summary>
/// .NET 11's rule-based tracing configuration (<c>services.AddTracing</c> in Microsoft.Extensions.Diagnostics) switches
/// Switchboard's spans on and off by source and operation name, with no hand-written <see cref="ActivityListener"/>.
/// </summary>
[Collection(TelemetryTests.CollectionName)]
public sealed class TracingRulesTests
{
    private static ServiceProvider BuildProvider(ConcurrentQueue<Activity> stopped, Action<ITracingBuilder> rules) =>
        new ServiceCollection()
            .AddSwitchboard(cfg => cfg.RegisterServicesFromAssemblyContaining<TracingRulesTests>())
            .AddTracing(tracing =>
            {
                tracing.AddListener("test", listener =>
                {
                    listener.Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded;
                    listener.ActivityStopped = stopped.Enqueue;
                });
                rules(tracing);
            })
            .BuildServiceProvider();

    [Fact]
    public async Task Enabling_the_switchboard_source_emits_its_spans()
    {
        var stopped = new ConcurrentQueue<Activity>();
        await using var provider = BuildProvider(stopped, tracing => tracing.EnableTracing(SwitchboardTelemetry.ActivitySourceName));
        await TracingRules.StartAsync(provider);

        Assert.Equal(42, await provider.GetRequiredService<ISender>().Send(new TelemetryProbe()));
        await provider.GetRequiredService<IPublisher>().Publish(new TelemetryNote());

        Assert.Single(stopped, a => a.DisplayName == "Send TelemetryProbe");
        Assert.Single(stopped, a => a.DisplayName == "Publish TelemetryNote");
        Assert.Single(stopped, a => a.DisplayName == "Handle TelemetryNote");
    }

    [Fact]
    public async Task A_rule_can_switch_off_one_operation_and_keep_the_rest()
    {
        var stopped = new ConcurrentQueue<Activity>();
        await using var provider = BuildProvider(stopped, tracing =>
        {
            tracing.EnableTracing(SwitchboardTelemetry.ActivitySourceName);
            tracing.DisableTracing(SwitchboardTelemetry.ActivitySourceName, operationName: "Handle TelemetryNote");
        });
        await TracingRules.StartAsync(provider);

        await provider.GetRequiredService<IPublisher>().Publish(new TelemetryNote());

        Assert.Single(stopped, a => a.DisplayName == "Publish TelemetryNote");
        Assert.DoesNotContain(stopped, a => a.DisplayName == "Handle TelemetryNote");
    }

    [Fact]
    public async Task Without_a_rule_for_the_source_nothing_is_recorded()
    {
        var stopped = new ConcurrentQueue<Activity>();
        await using var provider = BuildProvider(stopped, _ => { });
        await TracingRules.StartAsync(provider);

        await provider.GetRequiredService<ISender>().Send(new TelemetryProbe());

        Assert.DoesNotContain(stopped, a => a.Source.Name == SwitchboardTelemetry.ActivitySourceName);
    }
}

internal static class TracingRules
{
    // AddTracing subscribes its listeners during options startup validation, which a host runs as it starts.
    public static Task StartAsync(IServiceProvider provider)
        => provider.GetRequiredService<Microsoft.Extensions.Options.IAsyncStartupValidator>().ValidateAsync();
}
