using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Switchboard.Tests;

/// <summary>
/// A decorator written against 1.3, before <see cref="ISender"/> had <c>CreateStream</c>: it implements only the
/// <c>Send</c> overloads. It must keep compiling and working, since streaming arrived in a minor release.
/// </summary>
public sealed class PreStreamingSender : ISender
{
    private readonly ISender _inner;

    public PreStreamingSender(ISender inner) => _inner = inner;

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        => _inner.Send(request, cancellationToken);

    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest
        => _inner.Send(request, cancellationToken);

    public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        => _inner.Send(request, cancellationToken);
}

public sealed class SenderCompatibilityTests
{
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddSingleton<Recorder>()
            .AddSwitchboard(cfg => cfg.RegisterServicesFromAssemblyContaining<SenderCompatibilityTests>())
            .BuildServiceProvider();

    [Fact]
    public async Task A_sender_written_before_streaming_still_sends()
    {
        await using var provider = BuildProvider();
        ISender sender = new PreStreamingSender(provider.GetRequiredService<ISender>());

        Assert.Equal("hi pong", await sender.Send(new Ping("hi")));
    }

    [Fact]
    public async Task A_sender_written_before_streaming_says_what_to_implement_when_asked_for_a_stream()
    {
        await using var provider = BuildProvider();
        ISender sender = new PreStreamingSender(provider.GetRequiredService<ISender>());

        var typed = Assert.Throws<NotSupportedException>(() => sender.CreateStream(new CountTo(1)));
        var untyped = Assert.Throws<NotSupportedException>(() => sender.CreateStream((object)new CountTo(1)));

        Assert.Contains(nameof(PreStreamingSender), typed.Message);
        Assert.Contains("CreateStream", typed.Message);
        Assert.Equal(typed.Message, untyped.Message);
    }

    [Fact]
    public async Task The_mediator_implements_streaming_itself_rather_than_falling_back_to_the_default()
    {
        await using var provider = BuildProvider();

        Assert.Equal(new[] { 1 }, await provider.GetRequiredService<ISender>().CreateStream(new CountTo(1)).Collect());
        Assert.Equal(new object?[] { 1 }, await provider.GetRequiredService<IMediator>().CreateStream((object)new CountTo(1)).Collect());
    }
}
