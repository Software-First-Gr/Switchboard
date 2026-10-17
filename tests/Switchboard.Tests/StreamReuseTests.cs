using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Switchboard.Tests;

/// <summary>Counts its enumerations; every one after the first waits for <see cref="Later"/> before it ends.</summary>
public sealed class CountedStream : IStreamRequest<int>
{
    private int _enumerations;

    public int Enumerations => Volatile.Read(ref _enumerations);

    public TaskCompletionSource Later { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Next() => Interlocked.Increment(ref _enumerations);
}

public sealed class CountedStreamHandler : IStreamRequestHandler<CountedStream, int>
{
    public async IAsyncEnumerable<int> Handle(CountedStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumeration = request.Next();
        yield return enumeration;

        if (enumeration > 1)
        {
            await request.Later.Task;
        }
    }
}

/// <summary>
/// A C# async iterator hands its own instance out again from <c>GetAsyncEnumerator</c> once it has finished, on the
/// thread that created it, even before it is disposed. A stream returned straight from an iterator would therefore
/// give a second, overlapping enumeration the first one's state machine instead of a dispatch of its own.
/// </summary>
public sealed class StreamReuseTests
{
    private static ServiceProvider BuildProvider(bool scopePerDispatch) =>
        new ServiceCollection()
            .AddSingleton<Recorder>()
            .AddScoped<ScopeMarker>()
            .AddSwitchboard(cfg =>
            {
                cfg.RegisterServicesFromAssemblyContaining<StreamReuseTests>();
                if (scopePerDispatch)
                {
                    cfg.UseScopePerDispatch();
                }
            })
            .BuildServiceProvider();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_enumeration_that_starts_before_a_finished_one_is_disposed_dispatches_the_request_again(bool scopePerDispatch)
    {
        await using var provider = BuildProvider(scopePerDispatch);
        var request = new CountedStream();
        request.Later.SetResult();
        var stream = provider.GetRequiredService<ISender>().CreateStream(request);

        var first = stream.GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());
        Assert.False(await first.MoveNextAsync()); // finished, not yet disposed

        var second = stream.GetAsyncEnumerator();
        Assert.NotSame(first, second);
        await first.DisposeAsync();

        Assert.True(await second.MoveNextAsync());
        Assert.Equal(2, second.Current);
        Assert.False(await second.MoveNextAsync());
        await second.DisposeAsync();
        Assert.Equal(2, request.Enumerations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposing_a_finished_enumeration_leaves_an_overlapping_one_running(bool scopePerDispatch)
    {
        await using var provider = BuildProvider(scopePerDispatch);
        var request = new CountedStream();
        var stream = provider.GetRequiredService<ISender>().CreateStream((object)request);

        var first = stream.GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());
        Assert.False(await first.MoveNextAsync());

        var second = stream.GetAsyncEnumerator();
        Assert.True(await second.MoveNextAsync());
        var pending = second.MoveNextAsync(); // suspended in the handler, waiting for Later

        await first.DisposeAsync();
        request.Later.SetResult();

        Assert.False(await pending);
        await second.DisposeAsync();
        Assert.Equal(2, request.Enumerations);
    }
}
