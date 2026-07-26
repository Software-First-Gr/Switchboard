using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Switchboard.Tests;

public sealed class SendTests
{
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddSingleton<Recorder>()
            .AddSwitchboard(cfg => cfg.RegisterServicesFromAssemblyContaining<SendTests>())
            .BuildServiceProvider();

    [Fact]
    public async Task Send_typed_request_returns_handler_response()
    {
        await using var provider = BuildProvider();
        var sender = provider.GetRequiredService<ISender>();

        var response = await sender.Send(new Ping("ping"));

        Assert.Equal("ping pong", response);
    }

    [Fact]
    public async Task Send_void_request_invokes_handler()
    {
        await using var provider = BuildProvider();
        var sender = provider.GetRequiredService<ISender>();
        var recorder = provider.GetRequiredService<Recorder>();

        await sender.Send(new FireAndForget());

        Assert.Equal("fire-and-forget", recorder.Joined);
    }

    [Fact]
    public async Task Send_untyped_request_returns_boxed_response()
    {
        await using var provider = BuildProvider();
        var sender = provider.GetRequiredService<ISender>();
        object request = new Ping("hello");

        var result = await sender.Send(request);

        Assert.Equal("hello pong", Assert.IsType<string>(result));
    }

    [Fact]
    public async Task Send_untyped_void_request_returns_null()
    {
        await using var provider = BuildProvider();
        var sender = provider.GetRequiredService<ISender>();
        var recorder = provider.GetRequiredService<Recorder>();
        object request = new FireAndForget();

        var result = await sender.Send(request);

        Assert.Null(result);
        Assert.Equal("fire-and-forget", recorder.Joined);
    }

    [Fact]
    public async Task Send_untyped_non_request_throws()
    {
        await using var provider = BuildProvider();
        var sender = provider.GetRequiredService<ISender>();

        await Assert.ThrowsAsync<ArgumentException>(() => sender.Send(new object()));
    }

    [Fact]
    public async Task Send_null_request_throws()
    {
        await using var provider = BuildProvider();
        var sender = provider.GetRequiredService<ISender>();

        await Assert.ThrowsAsync<ArgumentNullException>(() => sender.Send((object)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => sender.Send((IRequest<string>)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => sender.Send((FireAndForget)null!));
    }

    [Fact]
    public async Task Send_without_registered_handler_throws()
    {
        await using var provider = BuildProvider();
        var sender = provider.GetRequiredService<ISender>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.Send(new Orphan()));
    }
}
