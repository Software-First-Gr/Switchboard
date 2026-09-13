using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Switchboard.Tests;

public sealed class DeduplicationTests
{
    private static ServiceCollection ScanOnce() => (ServiceCollection)new ServiceCollection()
        .AddSingleton<Recorder>()
        .AddSwitchboard(cfg => cfg
            .RegisterServicesFromAssemblyContaining<DeduplicationTests>()
            .AddOpenBehavior(typeof(FirstBehavior<,>))
            .AddBehavior<ClosedTrackedBehavior>());

    [Fact]
    public void Calling_AddSwitchboard_twice_on_the_same_assembly_registers_handlers_once()
    {
        using var once = ScanOnce().BuildServiceProvider();
        using var twice = ScanOnce()
            .AddSwitchboard(cfg => cfg.RegisterServicesFromAssemblyContaining<DeduplicationTests>())
            .BuildServiceProvider();

        Assert.Single(twice.GetServices<IRequestHandler<Ping, string>>());
        Assert.Single(twice.GetServices<IRequestHandler<FireAndForget>>());
        Assert.Equal(
            once.GetServices<INotificationHandler<OrderedNote>>().Count(),
            twice.GetServices<INotificationHandler<OrderedNote>>().Count());
    }

    [Fact]
    public void Adding_the_same_behaviors_from_two_AddSwitchboard_calls_registers_them_once()
    {
        using var provider = ScanOnce()
            .AddSwitchboard(cfg => cfg
                .AddOpenBehavior(typeof(FirstBehavior<,>))
                .AddBehavior<ClosedTrackedBehavior>())
            .BuildServiceProvider();

        var behaviors = provider.GetServices<IPipelineBehavior<Tracked, string>>().ToList();

        Assert.Equal(2, behaviors.Count);
        Assert.IsType<FirstBehavior<Tracked, string>>(behaviors[0]);
        Assert.IsType<ClosedTrackedBehavior>(behaviors[1]);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_handler_runs_once_when_two_modules_scan_the_same_assembly()
    {
        await using var provider = ScanOnce()
            .AddSwitchboard(cfg => cfg.RegisterServicesFromAssemblyContaining<DeduplicationTests>())
            .BuildServiceProvider();
        var recorder = provider.GetRequiredService<Recorder>();

        await provider.GetRequiredService<ISender>().Send(new FireAndForget());

        Assert.Equal("first:before|fire-and-forget|first:after", recorder.Joined);
    }
}
