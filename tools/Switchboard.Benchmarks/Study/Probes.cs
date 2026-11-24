using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Switchboard.Benchmarks.Study;

// Measurements that need a fresh process or the live stack, run one variant per process by StudyDriver.

public sealed record StackRequest : IRequest<StackReport>;

public sealed record StackReport(string[] BeforeAwait, string[] AfterAwait, string[] Exception);

public sealed class StackRequestHandler : IRequestHandler<StackRequest, StackReport>
{
    public async Task<StackReport> Handle(StackRequest request, CancellationToken cancellationToken)
    {
        var before = Probes.Frames(new StackTrace());
        await Task.Yield(); // A real suspension: the rest runs on a thread-pool thread.
        var after = Probes.Frames(new StackTrace());

        string[] exception = [];
        try
        {
            await Fail();
        }
        catch (InvalidOperationException ex)
        {
            exception = Probes.Frames(new StackTrace(ex));
        }

        return new StackReport(before, after, exception);
    }

    private static async Task Fail()
    {
        await Task.Yield();
        throw new InvalidOperationException("probe");
    }
}

internal static class Probes
{
    public static string[] Frames(StackTrace trace)
        => trace.GetFrames().Select(f => f.GetMethod()).Where(m => m is not null).Select(m => Describe(m!)).ToArray();

    private static string Describe(MethodBase method)
    {
        var type = method.DeclaringType;
        var typeName = type is null ? "?" : Clean(type.IsNested && typeof(IAsyncStateMachine).IsAssignableFrom(type)
            ? $"{type.DeclaringType?.Name}.{type.Name}"
            : type.Name);
        return $"{type?.Namespace?.Split('.')[0]}:{typeName}.{method.Name}";
    }

    private static string Clean(string name) => name.Replace("`2", "").Replace("`1", "");

    /// <summary>Live and exception stack traces seen by a handler behind three async behaviors, with scope per dispatch on.</summary>
    public static async Task<object> Stack()
    {
        var services = new ServiceCollection()
            .AddSingleton<IRequestHandler<StackRequest, StackReport>, StackRequestHandler>()
            .AddSingleton(typeof(IPipelineBehavior<,>), typeof(Layer1<,>))
            .AddSingleton(typeof(IPipelineBehavior<,>), typeof(Layer2<,>))
            .AddSingleton(typeof(IPipelineBehavior<,>), typeof(Layer3<,>));
        services.AddSwitchboard(cfg => cfg.UseScopePerDispatch());
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<ISender>().Send(new StackRequest());
        return report;
    }

    /// <summary>
    /// JIT work and wall time of the first dispatches in a fresh process: building the container, then the first Send
    /// through three behaviors (suspending once), the first Publish and the first stream, then a second warm Send.
    /// </summary>
    public static async Task<object> Cold()
    {
        var sinceStart = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        var methods0 = JitInfo.GetCompiledMethodCount();
        var il0 = JitInfo.GetCompiledILBytes();
        var jit0 = JitInfo.GetCompilationTime();
        var sw = Stopwatch.StartNew();

        var gate = new Gate();
        await using var provider = StudyServices.Build(gate, behaviors: 3, scopePerDispatch: true);
        var mediator = provider.GetRequiredService<IMediator>();

        gate.Arm();
        var first = mediator.Send(new DeepRequest(1));
        gate.Release();
        await first;
        await mediator.Publish(new DeepNote(1));
        await foreach (var _ in mediator.CreateStream(new DeepStream(4)))
        {
        }

        var firstMs = sw.Elapsed.TotalMilliseconds;
        var methods1 = JitInfo.GetCompiledMethodCount();
        var il1 = JitInfo.GetCompiledILBytes();
        var jit1 = JitInfo.GetCompilationTime();

        sw.Restart();
        await mediator.Send(new DeepRequest(2));
        var warmUs = sw.Elapsed.TotalMicroseconds;

        return new
        {
            mainEnteredMs = sinceStart,
            firstDispatchesMs = firstMs,
            jitMethods = methods1 - methods0,
            jitIlBytes = il1 - il0,
            jitTimeMs = (jit1 - jit0).TotalMilliseconds,
            secondSendUs = warmUs,
        };
    }

    /// <summary>Size and state-machine type count of the Switchboard assembly this variant loaded.</summary>
    public static object Assembly()
    {
        var assembly = typeof(Mediator).Assembly;
        var stateMachines = assembly.GetTypes().Count(t => typeof(IAsyncStateMachine).IsAssignableFrom(t));
        return new
        {
            bytes = new System.IO.FileInfo(assembly.Location).Length,
            types = assembly.GetTypes().Length,
            asyncStateMachineTypes = stateMachines,
        };
    }

    public static void Emit(object value) => Console.WriteLine("RESULT " + JsonSerializer.Serialize(value));
}
