using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Switchboard.Tests;

public sealed record OrderedNote : INotification;

public sealed class SlowFirstHandler : INotificationHandler<OrderedNote>
{
    private readonly Recorder _recorder;

    public SlowFirstHandler(Recorder recorder) => _recorder = recorder;

    public async Task Handle(OrderedNote notification, CancellationToken cancellationToken)
    {
        _recorder.Add("first:start");
        await Task.Delay(30, cancellationToken);
        _recorder.Add("first:end");
    }
}

public sealed class FastSecondHandler : INotificationHandler<OrderedNote>
{
    private readonly Recorder _recorder;

    public FastSecondHandler(Recorder recorder) => _recorder = recorder;

    public Task Handle(OrderedNote notification, CancellationToken cancellationToken)
    {
        _recorder.Add("second");
        return Task.CompletedTask;
    }
}

/// <summary>A notification with no handlers, on purpose. Not scanned anywhere.</summary>
public sealed record LonelyNote : INotification;

public sealed class PublishTests
{
    /// <summary>Handlers are registered manually (no scanning) so registration order is exact.</summary>
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddSingleton<Recorder>()
            .AddSwitchboard(cfg => { })
            .AddTransient<INotificationHandler<OrderedNote>, SlowFirstHandler>()
            .AddTransient<INotificationHandler<OrderedNote>, FastSecondHandler>()
            .BuildServiceProvider();

    [Fact]
    public async Task Publish_runs_handlers_sequentially_in_registration_order()
    {
        await using var provider = BuildProvider();
        var publisher = provider.GetRequiredService<IPublisher>();
        var recorder = provider.GetRequiredService<Recorder>();

        await publisher.Publish(new OrderedNote());

        Assert.Equal("first:start|first:end|second", recorder.Joined);
    }

    [Fact]
    public async Task Publish_untyped_object_dispatches_to_handlers()
    {
        await using var provider = BuildProvider();
        var publisher = provider.GetRequiredService<IPublisher>();
        var recorder = provider.GetRequiredService<Recorder>();
        object notification = new OrderedNote();

        await publisher.Publish(notification);

        Assert.Equal("first:start|first:end|second", recorder.Joined);
    }

    [Fact]
    public async Task Publish_with_no_handlers_completes()
    {
        await using var provider = BuildProvider();
        var publisher = provider.GetRequiredService<IPublisher>();

        await publisher.Publish(new LonelyNote());
    }

    [Fact]
    public async Task Publish_untyped_non_notification_throws()
    {
        await using var provider = BuildProvider();
        var publisher = provider.GetRequiredService<IPublisher>();

        await Assert.ThrowsAsync<ArgumentException>(() => publisher.Publish(new object()));
    }

    [Fact]
    public async Task Publish_null_throws()
    {
        await using var provider = BuildProvider();
        var publisher = provider.GetRequiredService<IPublisher>();

        await Assert.ThrowsAsync<ArgumentNullException>(() => publisher.Publish((object)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => publisher.Publish<OrderedNote>(null!));
    }
}
