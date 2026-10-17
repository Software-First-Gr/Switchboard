using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Switchboard.Tests;

public sealed record CountTo(int Count) : IStreamRequest<int>;

public sealed class CountToHandler : IStreamRequestHandler<CountTo, int>
{
    private readonly Recorder _recorder;

    public CountToHandler(Recorder recorder)
    {
        _recorder = recorder;
        _recorder.Add("handler:ctor");
    }

    public async IAsyncEnumerable<int> Handle(CountTo request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _recorder.Add("handler");

        for (var i = 1; i <= request.Count; i++)
        {
            await Task.Yield();
            yield return i;
        }
    }
}

public sealed record StreamNames(params string[] Names) : IStreamRequest<string>;

public sealed class StreamNamesHandler : IStreamRequestHandler<StreamNames, string>
{
    public async IAsyncEnumerable<string> Handle(StreamNames request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var name in request.Names)
        {
            await Task.Yield();
            yield return name;
        }
    }
}

/// <summary>Yields forever until cancelled, observing the token it was handed.</summary>
public sealed record Ticks : IStreamRequest<int>;

public sealed class TicksHandler : IStreamRequestHandler<Ticks, int>
{
    public async IAsyncEnumerable<int> Handle(Ticks request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; ; i++)
        {
            await Task.Delay(1, cancellationToken);
            yield return i;
        }
    }
}

/// <summary>Yields the token the handler received, so a test can see which cancellation reaches it.</summary>
public sealed record StreamTokenProbe : IStreamRequest<CancellationToken>;

public sealed class StreamTokenProbeHandler : IStreamRequestHandler<StreamTokenProbe, CancellationToken>
{
    public async IAsyncEnumerable<CancellationToken> Handle(StreamTokenProbe request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return cancellationToken;
    }
}

/// <summary>Yields the token it received, then enumerates <c>next()</c> without passing any token on.</summary>
public sealed class StreamTokenDroppingBehavior : IStreamPipelineBehavior<StreamTokenProbe, CancellationToken>
{
    public async IAsyncEnumerable<CancellationToken> Handle(
        StreamTokenProbe request, StreamHandlerDelegate<CancellationToken> next, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return cancellationToken;

        await foreach (var token in next())
        {
            yield return token;
        }
    }
}

// CS8425: these two ignore the enumerator's token on purpose, the way a careless handler or behavior would.
#pragma warning disable CS8425

/// <summary>
/// Answered with a stream that is not an iterator and only sees the token passed to <c>GetAsyncEnumerator</c>,
/// the way an EF Core query returned through <c>AsAsyncEnumerable()</c> does.
/// </summary>
public sealed record EnumeratorTokenProbe : IStreamRequest<CancellationToken>;

public sealed class EnumeratorTokenProbeHandler : IStreamRequestHandler<EnumeratorTokenProbe, CancellationToken>
{
    public IAsyncEnumerable<CancellationToken> Handle(EnumeratorTokenProbe request, CancellationToken cancellationToken)
        => new OnlyEnumeratorToken();

    private sealed class OnlyEnumeratorToken : IAsyncEnumerable<CancellationToken>
    {
        public IAsyncEnumerator<CancellationToken> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => Echo(cancellationToken).GetAsyncEnumerator();

        private static async IAsyncEnumerable<CancellationToken> Echo(CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return cancellationToken;
        }
    }
}

/// <summary>Enumerates <c>next()</c> without passing any token on.</summary>
public sealed class EnumeratorTokenDroppingBehavior : IStreamPipelineBehavior<EnumeratorTokenProbe, CancellationToken>
{
    public async IAsyncEnumerable<CancellationToken> Handle(
        EnumeratorTokenProbe request, StreamHandlerDelegate<CancellationToken> next, CancellationToken cancellationToken)
    {
        await foreach (var token in next())
        {
            yield return token;
        }
    }
}

#pragma warning restore CS8425

public sealed record FailingStream : IStreamRequest<int>;

public sealed class FailingStreamHandler : IStreamRequestHandler<FailingStream, int>
{
    public async IAsyncEnumerable<int> Handle(FailingStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return 1;
        throw new InvalidOperationException("boom");
    }
}

/// <summary>A stream request with no registered handler, on purpose.</summary>
public sealed record OrphanStream : IStreamRequest<int>;

public sealed class FirstStreamBehavior<TRequest, TResponse> : IStreamPipelineBehavior<TRequest, TResponse>
{
    private readonly Recorder _recorder;

    public FirstStreamBehavior(Recorder recorder) => _recorder = recorder;

    public async IAsyncEnumerable<TResponse> Handle(
        TRequest request, StreamHandlerDelegate<TResponse> next, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _recorder.Add("first:before");
        await foreach (var item in next().WithCancellation(cancellationToken))
        {
            yield return item;
        }
        _recorder.Add("first:after");
    }
}

public sealed class SecondStreamBehavior<TRequest, TResponse> : IStreamPipelineBehavior<TRequest, TResponse>
{
    private readonly Recorder _recorder;

    public SecondStreamBehavior(Recorder recorder) => _recorder = recorder;

    public async IAsyncEnumerable<TResponse> Handle(
        TRequest request, StreamHandlerDelegate<TResponse> next, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _recorder.Add("second:before");
        await foreach (var item in next().WithCancellation(cancellationToken))
        {
            yield return item;
        }
        _recorder.Add("second:after");
    }
}

/// <summary>Filters: drops every odd item.</summary>
public sealed class EvenOnlyBehavior : IStreamPipelineBehavior<CountTo, int>
{
    public async IAsyncEnumerable<int> Handle(
        CountTo request, StreamHandlerDelegate<int> next, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in next().WithCancellation(cancellationToken))
        {
            if (item % 2 == 0)
            {
                yield return item;
            }
        }
    }
}

/// <summary>Transforms: multiplies every item by ten.</summary>
public sealed class TimesTenBehavior : IStreamPipelineBehavior<CountTo, int>
{
    public async IAsyncEnumerable<int> Handle(
        CountTo request, StreamHandlerDelegate<int> next, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in next().WithCancellation(cancellationToken))
        {
            yield return item * 10;
        }
    }
}

/// <summary>Answers the stream itself, the way a cache would, without ever calling <c>next</c>.</summary>
public sealed class CachedCountBehavior : IStreamPipelineBehavior<CountTo, int>
{
    public async IAsyncEnumerable<int> Handle(
        CountTo request, StreamHandlerDelegate<int> next, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return -1;
    }
}

public static class AsyncEnumerableTestExtensions
{
    // Not ToListAsync: .NET 10 ships System.Linq.AsyncEnumerable.ToListAsync, which would make calls ambiguous there.
    public static async Task<List<T>> Collect<T>(this IAsyncEnumerable<T> items, CancellationToken cancellationToken = default)
    {
        var list = new List<T>();
        await foreach (var item in items.WithCancellation(cancellationToken))
        {
            list.Add(item);
        }

        return list;
    }
}

public sealed class StreamTests
{
    private static ServiceProvider BuildProvider(Action<SwitchboardConfiguration>? configure = null) =>
        new ServiceCollection()
            .AddSingleton<Recorder>()
            .AddSwitchboard(cfg =>
            {
                cfg.RegisterServicesFromAssemblyContaining<StreamTests>();
                configure?.Invoke(cfg);
            })
            .BuildServiceProvider();

    [Fact]
    public async Task CreateStream_yields_every_item_from_the_handler()
    {
        await using var provider = BuildProvider();

        var items = await provider.GetRequiredService<ISender>().CreateStream(new CountTo(3)).Collect();

        Assert.Equal(new[] { 1, 2, 3 }, items);
    }

    [Fact]
    public async Task Untyped_CreateStream_yields_the_items_as_objects()
    {
        await using var provider = BuildProvider();

        var items = await provider.GetRequiredService<ISender>().CreateStream((object)new CountTo(2)).Collect();

        Assert.Equal(new object?[] { 1, 2 }, items);
    }

    [Fact]
    public async Task Untyped_CreateStream_works_through_IMediator_too()
    {
        await using var provider = BuildProvider();

        var items = await provider.GetRequiredService<IMediator>().CreateStream((object)new StreamNames("a", "b")).Collect();

        Assert.Equal(new object?[] { "a", "b" }, items);
    }

    [Fact]
    public async Task A_stream_can_be_created_through_a_covariant_base_type()
    {
        await using var provider = BuildProvider();
        IStreamRequest<object> request = new StreamNames("a", "b");

        var items = await provider.GetRequiredService<ISender>().CreateStream(request).Collect();

        Assert.Equal(new object[] { "a", "b" }, items);

        // The covariant call must not poison the wrapper cache for the declared item type.
        Assert.Equal(new[] { "c" }, await provider.GetRequiredService<ISender>().CreateStream(new StreamNames("c")).Collect());
    }

    [Fact]
    public async Task Nothing_is_resolved_until_the_stream_is_enumerated()
    {
        await using var provider = BuildProvider(cfg => cfg.AddOpenStreamBehavior(typeof(FirstStreamBehavior<,>)));
        var recorder = provider.GetRequiredService<Recorder>();

        var stream = provider.GetRequiredService<ISender>().CreateStream(new CountTo(1));
        Assert.Equal("", recorder.Joined);

        await stream.Collect();
        Assert.Equal("first:before|handler:ctor|handler|first:after", recorder.Joined);
    }

    [Fact]
    public async Task Each_enumeration_dispatches_the_request_again()
    {
        await using var provider = BuildProvider();
        var recorder = provider.GetRequiredService<Recorder>();
        var stream = provider.GetRequiredService<ISender>().CreateStream(new CountTo(2));

        Assert.Equal(new[] { 1, 2 }, await stream.Collect());
        Assert.Equal(new[] { 1, 2 }, await stream.Collect());

        Assert.Equal("handler:ctor|handler|handler:ctor|handler", recorder.Joined);
    }

    [Fact]
    public async Task Behaviors_run_in_registration_order_outermost_first()
    {
        await using var provider = BuildProvider(cfg => cfg
            .AddOpenStreamBehavior(typeof(FirstStreamBehavior<,>))
            .AddOpenStreamBehavior(typeof(SecondStreamBehavior<,>)));
        var recorder = provider.GetRequiredService<Recorder>();

        await provider.GetRequiredService<ISender>().CreateStream(new CountTo(2)).Collect();

        Assert.Equal("first:before|second:before|handler:ctor|handler|second:after|first:after", recorder.Joined);
    }

    [Fact]
    public async Task Open_and_closed_stream_behaviors_share_one_ordering()
    {
        // TimesTen is outermost, so it multiplies only what EvenOnly lets through.
        await using var provider = BuildProvider(cfg => cfg
            .AddStreamBehavior<TimesTenBehavior>()
            .AddOpenStreamBehavior(typeof(FirstStreamBehavior<,>))
            .AddStreamBehavior<EvenOnlyBehavior>());
        var recorder = provider.GetRequiredService<Recorder>();

        var items = await provider.GetRequiredService<ISender>().CreateStream(new CountTo(5)).Collect();

        Assert.Equal(new[] { 20, 40 }, items);
        Assert.Equal("first:before|handler:ctor|handler|first:after", recorder.Joined);
    }

    [Fact]
    public async Task Closed_stream_behaviors_only_apply_to_their_own_request()
    {
        await using var provider = BuildProvider(cfg => cfg.AddStreamBehavior<TimesTenBehavior>());

        Assert.Equal(new[] { "x" }, await provider.GetRequiredService<ISender>().CreateStream(new StreamNames("x")).Collect());
    }

    [Fact]
    public async Task A_behavior_that_answers_the_stream_itself_never_constructs_the_handler()
    {
        await using var provider = BuildProvider(cfg => cfg.AddStreamBehavior<CachedCountBehavior>());
        var recorder = provider.GetRequiredService<Recorder>();

        var items = await provider.GetRequiredService<ISender>().CreateStream(new CountTo(3)).Collect();

        Assert.Equal(new[] { -1 }, items);
        Assert.Equal("", recorder.Joined);
    }

    [Fact]
    public async Task Cancelling_the_CreateStream_token_stops_the_stream_mid_way()
    {
        await using var provider = BuildProvider(cfg => cfg.AddOpenStreamBehavior(typeof(FirstStreamBehavior<,>)));
        using var cts = new CancellationTokenSource();
        var received = new List<int>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var tick in provider.GetRequiredService<ISender>().CreateStream(new Ticks(), cts.Token))
            {
                received.Add(tick);
                if (received.Count == 3)
                {
                    cts.Cancel();
                }
            }
        });

        Assert.Equal(new[] { 0, 1, 2 }, received);
    }

    [Fact]
    public async Task Cancelling_the_WithCancellation_token_stops_the_stream_mid_way()
    {
        await using var provider = BuildProvider(cfg => cfg.AddOpenStreamBehavior(typeof(FirstStreamBehavior<,>)));
        using var cts = new CancellationTokenSource();
        var received = new List<int>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var tick in provider.GetRequiredService<ISender>().CreateStream((object)new Ticks()).WithCancellation(cts.Token))
            {
                received.Add((int)tick!);
                if (received.Count == 3)
                {
                    cts.Cancel();
                }
            }
        });

        Assert.Equal(new[] { 0, 1, 2 }, received);
    }

    [Fact]
    public async Task Both_tokens_reach_every_behavior_and_the_handler_even_when_a_behavior_passes_none_on()
    {
        await using var provider = BuildProvider(cfg => cfg
            .AddOpenStreamBehavior(typeof(FirstStreamBehavior<,>))
            .AddStreamBehavior<StreamTokenDroppingBehavior>());
        var sender = provider.GetRequiredService<ISender>();

        // Checked while the stream is live: the token the two are combined into is released when it ends.
        async Task<List<bool>> CancelledAsSeen(CancellationTokenSource toCancel, CancellationToken createStreamToken, CancellationToken enumeratorToken)
        {
            var seen = new List<bool>();
            await foreach (var token in sender.CreateStream(new StreamTokenProbe(), createStreamToken).WithCancellation(enumeratorToken))
            {
                toCancel.Cancel();
                seen.Add(token.IsCancellationRequested);
            }

            return seen;
        }

        using (var createStream = new CancellationTokenSource())
        using (var enumerator = new CancellationTokenSource())
        {
            // The behavior's token, then the handler's.
            Assert.Equal(new[] { true, true }, await CancelledAsSeen(createStream, createStream.Token, enumerator.Token));
        }

        using (var createStream = new CancellationTokenSource())
        using (var enumerator = new CancellationTokenSource())
        {
            Assert.Equal(new[] { true, true }, await CancelledAsSeen(enumerator, createStream.Token, enumerator.Token));
        }
    }

    [Fact]
    public async Task A_handler_that_only_reads_the_enumerator_token_gets_it_even_behind_a_behavior_that_passes_none_on()
    {
        await using var provider = BuildProvider(cfg => cfg.AddStreamBehavior<EnumeratorTokenDroppingBehavior>());
        using var cts = new CancellationTokenSource();
        var seen = new List<bool>();

        await foreach (var token in provider.GetRequiredService<ISender>().CreateStream(new EnumeratorTokenProbe(), cts.Token))
        {
            cts.Cancel();
            seen.Add(token.IsCancellationRequested);
        }

        Assert.Equal(new[] { true }, seen);
    }

    [Fact]
    public async Task An_exception_from_the_handler_surfaces_after_the_items_before_it()
    {
        await using var provider = BuildProvider(cfg => cfg.AddOpenStreamBehavior(typeof(FirstStreamBehavior<,>)));
        var received = new List<int>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var item in provider.GetRequiredService<ISender>().CreateStream(new FailingStream()))
            {
                received.Add(item);
            }
        });

        Assert.Equal("boom", exception.Message);
        Assert.Equal(new[] { 1 }, received);
    }

    [Fact]
    public async Task A_missing_handler_fails_when_the_stream_is_enumerated()
    {
        await using var provider = BuildProvider();

        var stream = provider.GetRequiredService<ISender>().CreateStream(new OrphanStream());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => stream.Collect());
        Assert.Contains("IStreamRequestHandler", exception.Message);
    }

    [Fact]
    public void Untyped_CreateStream_rejects_objects_that_are_not_stream_requests_at_the_call()
    {
        using var provider = BuildProvider();
        var sender = provider.GetRequiredService<ISender>();

        Assert.Throws<ArgumentException>(() => sender.CreateStream(new Ping("not a stream")));
        Assert.Throws<ArgumentException>(() => sender.CreateStream(new object()));
    }

    [Fact]
    public void CreateStream_rejects_null_at_the_call()
    {
        using var provider = BuildProvider();
        var sender = provider.GetRequiredService<ISender>();

        Assert.Throws<ArgumentNullException>(() => sender.CreateStream<int>(null!));
        Assert.Throws<ArgumentNullException>(() => sender.CreateStream((object)null!));
    }
}
