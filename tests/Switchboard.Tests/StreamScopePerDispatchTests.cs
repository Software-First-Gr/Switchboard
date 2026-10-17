using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Switchboard.Tests;

/// <summary>Yields the handler's scoped marker <see cref="Count"/> times, then fails if asked to.</summary>
public sealed record ScopedStream(int Count = 3, bool FailAtEnd = false) : IStreamRequest<ScopeMarker>;

public sealed class ScopedStreamHandler : IStreamRequestHandler<ScopedStream, ScopeMarker>
{
    private readonly ScopeMarker _marker;

    public ScopedStreamHandler(ScopeMarker marker) => _marker = marker;

    public async IAsyncEnumerable<ScopeMarker> Handle(ScopedStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < request.Count; i++)
        {
            await Task.Yield();
            yield return _marker;
        }

        if (request.FailAtEnd)
        {
            throw new InvalidOperationException("boom");
        }
    }
}

/// <summary>Sends through its injected sender after it has already yielded, so the send runs on a resumed step.</summary>
public sealed record NestedScopeStream : IStreamRequest<ScopeMarker>;

public sealed class NestedScopeStreamHandler : IStreamRequestHandler<NestedScopeStream, ScopeMarker>
{
    private readonly ScopeMarker _marker;
    private readonly ISender _sender;

    public NestedScopeStreamHandler(ScopeMarker marker, ISender sender)
    {
        _marker = marker;
        _sender = sender;
    }

    public async IAsyncEnumerable<ScopeMarker> Handle(NestedScopeStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return _marker;
        yield return await _sender.Send(new ScopeProbe(), cancellationToken);

        await foreach (var marker in _sender.CreateStream(new ScopedStream(Count: 1), cancellationToken))
        {
            yield return marker;
        }
    }
}

public sealed class StreamScopePerDispatchTests
{
    private static ServiceProvider BuildProvider(Action<SwitchboardConfiguration>? configure = null) =>
        new ServiceCollection()
            .AddScoped<ScopeMarker>()
            .AddSwitchboard(cfg =>
            {
                cfg.RegisterServicesFromAssemblyContaining<StreamScopePerDispatchTests>();
                configure?.Invoke(cfg);
            })
            .BuildServiceProvider(validateScopes: true);

    [Fact]
    public async Task By_default_the_handler_shares_the_callers_scope()
    {
        await using var provider = BuildProvider();
        await using var callerScope = provider.CreateAsyncScope();
        var callerMarker = callerScope.ServiceProvider.GetRequiredService<ScopeMarker>();

        var markers = await callerScope.ServiceProvider.GetRequiredService<ISender>().CreateStream(new ScopedStream(Count: 1)).Collect();

        Assert.Same(callerMarker, Assert.Single(markers));
    }

    [Fact]
    public async Task The_scope_lives_for_the_whole_enumeration_and_is_disposed_when_it_ends()
    {
        await using var provider = BuildProvider(cfg => cfg.UseScopePerDispatch());
        await using var callerScope = provider.CreateAsyncScope();
        var callerMarker = callerScope.ServiceProvider.GetRequiredService<ScopeMarker>();
        var seen = new List<ScopeMarker>();

        await foreach (var marker in callerScope.ServiceProvider.GetRequiredService<ISender>().CreateStream(new ScopedStream()))
        {
            Assert.False(marker.Disposed, "the scope is still alive while items are being yielded");
            seen.Add(marker);
        }

        Assert.Equal(3, seen.Count);
        Assert.All(seen, marker => Assert.Same(seen[0], marker));
        Assert.NotSame(callerMarker, seen[0]);
        Assert.True(seen[0].Disposed);
        Assert.False(callerMarker.Disposed);
    }

    [Fact]
    public async Task The_scope_is_disposed_when_the_consumer_breaks_out_early()
    {
        await using var provider = BuildProvider(cfg => cfg.UseScopePerDispatch());
        ScopeMarker? first = null;

        await foreach (var marker in provider.GetRequiredService<ISender>().CreateStream(new ScopedStream()))
        {
            first = marker;
            break;
        }

        Assert.NotNull(first);
        Assert.True(first.Disposed);
    }

    [Fact]
    public async Task The_scope_is_disposed_when_the_handler_throws()
    {
        await using var provider = BuildProvider(cfg => cfg.UseScopePerDispatch());
        var seen = new List<ScopeMarker>();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var marker in provider.GetRequiredService<ISender>().CreateStream((object)new ScopedStream(Count: 1, FailAtEnd: true)))
            {
                seen.Add((ScopeMarker)marker!);
            }
        });

        Assert.True(Assert.Single(seen).Disposed);
    }

    [Fact]
    public async Task A_stream_that_is_never_enumerated_never_creates_a_scope()
    {
        var created = 0;
        await using var provider = BuildProvider(cfg => cfg.UseScopePerDispatch(_ => created++));

        _ = provider.GetRequiredService<ISender>().CreateStream(new ScopedStream());

        Assert.Equal(0, created);
    }

    [Fact]
    public async Task Each_enumeration_gets_its_own_scope()
    {
        await using var provider = BuildProvider(cfg => cfg.UseScopePerDispatch());
        var stream = provider.GetRequiredService<ISender>().CreateStream(new ScopedStream(Count: 1));

        var first = Assert.Single(await stream.Collect());
        var second = Assert.Single(await stream.Collect());

        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task Sends_and_streams_from_the_handler_join_its_scope_even_after_it_has_yielded()
    {
        await using var provider = BuildProvider(cfg => cfg.UseScopePerDispatch());
        await using var callerScope = provider.CreateAsyncScope();

        var markers = await callerScope.ServiceProvider.GetRequiredService<ISender>().CreateStream(new NestedScopeStream()).Collect();

        Assert.Equal(3, markers.Count);
        Assert.Same(markers[0], markers[1]); // a Send after the first yield
        Assert.Same(markers[0], markers[2]); // a nested stream
        Assert.True(markers[0].Disposed);
    }

    [Fact]
    public async Task The_callback_runs_before_the_handler_with_the_request_as_the_message()
    {
        await using var provider = BuildProvider(cfg => cfg.UseScopePerDispatch(async (scope, cancellationToken) =>
        {
            await Task.Yield();
            Assert.IsType<ScopedStream>(scope.Message);
            scope.ServiceProvider.GetRequiredService<ScopeMarker>().SeededUser = "seeded";
        }));

        var markers = await provider.GetRequiredService<ISender>().CreateStream(new ScopedStream(Count: 1)).Collect();

        Assert.Equal("seeded", Assert.Single(markers).SeededUser);
    }
}
