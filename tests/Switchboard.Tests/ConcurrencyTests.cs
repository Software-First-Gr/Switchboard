using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Switchboard.Tests;

// --- First-use races --------------------------------------------------------
//
// The wrapper caches are static and process-wide, so a race on their first use needs request types nothing else ever
// dispatches. Each race closes these generic requests over a RaceTag<TA, TB> built at run time: 64 types per request,
// used by one test only. Generic definitions are skipped by assembly scanning and validation, so the handlers are
// registered explicitly and the rest of the suite never sees them.

/// <summary>Only exists to be closed over: <c>RaceTag&lt;int, long&gt;</c> makes a request type of its own.</summary>
public sealed class RaceTag<TA, TB>;

/// <summary>A reference type, so <see cref="RaceProbe{TTag}"/> can also be sent covariantly as <c>IRequest&lt;object&gt;</c>.</summary>
public sealed record RaceReply<TTag>(int Id);

public sealed record RaceProbe<TTag>(int Id) : IRequest<RaceReply<TTag>>;

public sealed class RaceProbeHandler<TTag> : IRequestHandler<RaceProbe<TTag>, RaceReply<TTag>>
{
    public Task<RaceReply<TTag>> Handle(RaceProbe<TTag> request, CancellationToken cancellationToken)
        => Task.FromResult(new RaceReply<TTag>(request.Id));
}

public sealed record RaceCommand<TTag>(StrongBox<int> Handled) : IRequest;

public sealed class RaceCommandHandler<TTag> : IRequestHandler<RaceCommand<TTag>>
{
    public Task Handle(RaceCommand<TTag> request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref request.Handled.Value);
        return Task.CompletedTask;
    }
}

public sealed record RaceNote<TTag>(StrongBox<int> Handled) : INotification;

public sealed class RaceNoteHandler<TTag> : INotificationHandler<RaceNote<TTag>>
{
    public Task Handle(RaceNote<TTag> notification, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref notification.Handled.Value);
        return Task.CompletedTask;
    }
}

public sealed record RaceStream<TTag>(int Id) : IStreamRequest<RaceReply<TTag>>;

public sealed class RaceStreamHandler<TTag> : IStreamRequestHandler<RaceStream<TTag>, RaceReply<TTag>>
{
    public async IAsyncEnumerable<RaceReply<TTag>> Handle(RaceStream<TTag> request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < 3; i++)
        {
            await Task.Yield();
            yield return new RaceReply<TTag>(request.Id);
        }
    }
}

// --- Scope per dispatch under load -----------------------------------------

/// <summary>
/// Shared by every dispatch of one run. <see cref="Arrive"/> holds each one until all have arrived, so every scope of
/// the run is alive at the same time; <see cref="Abort"/> releases them all with the failure of any one.
/// </summary>
public sealed class ConcurrencyGate
{
    private readonly int _participants;
    private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;

    public ConcurrencyGate(int participants) => _participants = participants;

    public Task Arrive()
    {
        if (Interlocked.Increment(ref _arrived) == _participants)
        {
            _allArrived.TrySetResult();
        }

        return _allArrived.Task;
    }

    public void Abort(Exception exception) => _allArrived.TrySetException(exception);
}

/// <summary>What one top-level dispatch saw: the marker of its own scope and those of every dispatch it made itself.</summary>
public sealed record LoadRecord(string Message, ScopeMarker Own, IReadOnlyList<ScopeMarker> Nested);

public sealed class LoadRun
{
    public LoadRun(int participants) => Gate = new ConcurrencyGate(participants);

    public ConcurrencyGate Gate { get; }

    public ConcurrentQueue<LoadRecord> Records { get; } = new();
}

public interface ILoadMessage
{
    int Id { get; }

    LoadRun Run { get; }
}

public static class LoadMessages
{
    /// <summary>What the scope callback seeds into a top-level dispatch's scope: <c>LoadSend#3</c>.</summary>
    public static string Name(object message)
        => message is ILoadMessage load ? $"{message.GetType().Name}#{load.Id}" : message.GetType().Name;

    /// <summary>
    /// Sends, streams and publishes through the handler's own <see cref="ISender"/> and <see cref="IPublisher"/>, before and
    /// after every top-level dispatch of the run has arrived, and from tasks forked onto the thread pool; every one of
    /// them must land in the handler's scope.
    /// </summary>
    public static async Task<List<ScopeMarker>> NestedWork(ISender sender, IPublisher publisher, LoadRun run, CancellationToken cancellationToken)
    {
        var nested = new List<ScopeMarker> { await sender.Send(new ScopeProbe(), cancellationToken) };

        await run.Gate.Arrive();

        nested.AddRange(await Task.WhenAll(
            Task.Run(() => sender.Send(new ScopeProbe(), cancellationToken), cancellationToken),
            Task.Run(() => sender.Send(new ScopeProbe(), cancellationToken), cancellationToken)));

        nested.AddRange(await sender.CreateStream(new ScopedStream(Count: 2), cancellationToken).Collect(cancellationToken));

        var note = new ScopeNote(new ConcurrentQueue<ScopeMarker>());
        await publisher.Publish(note, cancellationToken);
        nested.AddRange(note.Seen);

        return nested;
    }
}

public sealed record LoadSend(int Id, LoadRun Run) : IRequest<ScopeMarker>, ILoadMessage;

public sealed class LoadSendHandler : IRequestHandler<LoadSend, ScopeMarker>
{
    private readonly ScopeMarker _marker;
    private readonly ISender _sender;
    private readonly IPublisher _publisher;

    public LoadSendHandler(ScopeMarker marker, ISender sender, IPublisher publisher)
    {
        _marker = marker;
        _sender = sender;
        _publisher = publisher;
    }

    public async Task<ScopeMarker> Handle(LoadSend request, CancellationToken cancellationToken)
    {
        await Task.Yield();
        var nested = await LoadMessages.NestedWork(_sender, _publisher, request.Run, cancellationToken);
        request.Run.Records.Enqueue(new LoadRecord(LoadMessages.Name(request), _marker, nested));
        return _marker;
    }
}

public sealed record LoadCommand(int Id, LoadRun Run) : IRequest, ILoadMessage;

public sealed class LoadCommandHandler : IRequestHandler<LoadCommand>
{
    private readonly ScopeMarker _marker;
    private readonly ISender _sender;
    private readonly IPublisher _publisher;

    public LoadCommandHandler(ScopeMarker marker, ISender sender, IPublisher publisher)
    {
        _marker = marker;
        _sender = sender;
        _publisher = publisher;
    }

    public async Task Handle(LoadCommand request, CancellationToken cancellationToken)
    {
        var nested = await LoadMessages.NestedWork(_sender, _publisher, request.Run, cancellationToken);
        request.Run.Records.Enqueue(new LoadRecord(LoadMessages.Name(request), _marker, nested));
    }
}

public sealed record LoadNote(int Id, LoadRun Run) : INotification, ILoadMessage;

public sealed class LoadNoteHandler : INotificationHandler<LoadNote>
{
    private readonly ScopeMarker _marker;
    private readonly ISender _sender;
    private readonly IPublisher _publisher;

    public LoadNoteHandler(ScopeMarker marker, ISender sender, IPublisher publisher)
    {
        _marker = marker;
        _sender = sender;
        _publisher = publisher;
    }

    public async Task Handle(LoadNote notification, CancellationToken cancellationToken)
    {
        var nested = await LoadMessages.NestedWork(_sender, _publisher, notification.Run, cancellationToken);
        notification.Run.Records.Enqueue(new LoadRecord(LoadMessages.Name(notification), _marker, nested));
    }
}

/// <summary>
/// Yields its marker four times and dispatches between the yields, so its nested work runs on steps resumed from the
/// consumer's execution context; it arrives at the run's gate only after it has yielded.
/// </summary>
public sealed record LoadStream(int Id, LoadRun Run) : IStreamRequest<ScopeMarker>, ILoadMessage;

public sealed class LoadStreamHandler : IStreamRequestHandler<LoadStream, ScopeMarker>
{
    private readonly ScopeMarker _marker;
    private readonly ISender _sender;
    private readonly IPublisher _publisher;

    public LoadStreamHandler(ScopeMarker marker, ISender sender, IPublisher publisher)
    {
        _marker = marker;
        _sender = sender;
        _publisher = publisher;
    }

    public async IAsyncEnumerable<ScopeMarker> Handle(LoadStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var nested = new List<ScopeMarker>();

        yield return _marker;
        nested.Add(await _sender.Send(new ScopeProbe(), cancellationToken));

        yield return _marker;
        await request.Run.Gate.Arrive();
        nested.AddRange(await Task.WhenAll(
            Task.Run(() => _sender.Send(new ScopeProbe(), cancellationToken), cancellationToken),
            Task.Run(() => _sender.Send(new ScopeProbe(), cancellationToken), cancellationToken)));

        yield return _marker;
        await foreach (var marker in _sender.CreateStream(new ScopedStream(Count: 2), cancellationToken))
        {
            nested.Add(marker);
        }

        var note = new ScopeNote(new ConcurrentQueue<ScopeMarker>());
        await _publisher.Publish(note, cancellationToken);
        nested.AddRange(note.Seen);

        request.Run.Records.Enqueue(new LoadRecord(LoadMessages.Name(request), _marker, nested));
        yield return _marker;
    }
}

// --- Streams abandoned, cancelled and completed -----------------------------

public enum StreamEnding
{
    Completes,
    BreaksEarly,
    CancelsThroughCreateStreamToken,
    CancelsThroughWithCancellation,
    CancelsWhileTheHandlerIsAwaiting,
}

public sealed class StreamLedger
{
    public StreamLedger(int participants) => Gate = new ConcurrencyGate(participants);

    public ConcurrencyGate Gate { get; }

    public ConcurrentDictionary<int, ScopeMarker> Scopes { get; } = new();

    public ConcurrentDictionary<int, bool> Finalized { get; } = new();
}

/// <summary>
/// Yields <see cref="Items"/> numbers, observing the token after every resumption. With <see cref="ParkAt"/> set, it
/// signals <see cref="Parked"/> before that item and waits for the token, so the consumer cancels a step in flight.
/// </summary>
public sealed record EndingStream(int Id, StreamLedger Ledger, int Items, int ParkAt = -1, TaskCompletionSource? Parked = null)
    : IStreamRequest<int>;

public sealed class EndingStreamHandler : IStreamRequestHandler<EndingStream, int>
{
    private readonly ScopeMarker _marker;

    public EndingStreamHandler(ScopeMarker marker) => _marker = marker;

    public async IAsyncEnumerable<int> Handle(EndingStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        request.Ledger.Scopes[request.Id] = _marker;

        try
        {
            await request.Ledger.Gate.Arrive();

            for (var i = 0; i < request.Items; i++)
            {
                if (i == request.ParkAt)
                {
                    request.Parked!.SetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return i;
            }
        }
        finally
        {
            request.Ledger.Finalized[request.Id] = true;
        }
    }
}

// --- The scope a handler's own stream leaves behind -------------------------

/// <summary>
/// Enumerates a stream through a scope the handler created itself — so the stream gets a dispatch scope of its own —
/// and sends through its injected sender between the items and after them.
/// </summary>
public sealed record IsolatedStreamConsumer : IRequest<IsolatedStreamConsumerResult>;

public sealed record IsolatedStreamConsumerResult(ScopeMarker Own, ScopeMarker Streamed, IReadOnlyList<ScopeMarker> SentAfterItems);

public sealed class IsolatedStreamConsumerHandler : IRequestHandler<IsolatedStreamConsumer, IsolatedStreamConsumerResult>
{
    private readonly ScopeMarker _marker;
    private readonly ISender _sender;
    private readonly IServiceScopeFactory _scopes;

    public IsolatedStreamConsumerHandler(ScopeMarker marker, ISender sender, IServiceScopeFactory scopes)
    {
        _marker = marker;
        _sender = sender;
        _scopes = scopes;
    }

    public async Task<IsolatedStreamConsumerResult> Handle(IsolatedStreamConsumer request, CancellationToken cancellationToken)
    {
        await using var ownScope = _scopes.CreateAsyncScope();
        var isolated = ownScope.ServiceProvider.GetRequiredService<ISender>();
        ScopeMarker? streamed = null;
        var sent = new List<ScopeMarker>();

        await foreach (var marker in isolated.CreateStream(new ScopedStream(Count: 2), cancellationToken))
        {
            streamed = marker;
            sent.Add(await _sender.Send(new ScopeProbe(), cancellationToken));
        }

        sent.Add(await _sender.Send(new ScopeProbe(), cancellationToken));

        return new IsolatedStreamConsumerResult(_marker, streamed!, sent);
    }
}

public sealed class ConcurrencyTests
{
    /// <summary>Never what a test asserts on: it only turns a hang into a failure.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private const int RaceThreads = 8;

    private static readonly Type[] TagArguments =
        { typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong) };

    private static ServiceProvider BuildProvider(Action<SwitchboardConfiguration>? configure = null) =>
        new ServiceCollection()
            .AddScoped<ScopeMarker>()
            .AddSwitchboard(cfg =>
            {
                cfg.RegisterServicesFromAssemblyContaining<ConcurrencyTests>();
                configure?.Invoke(cfg);
            })
            .BuildServiceProvider(validateScopes: true);

    // --- First-use races ----------------------------------------------------

    /// <summary>
    /// Builds one race per fresh request type: <paramref name="arm"/>, a generic method of this class closed over each
    /// tag, registers that type's handler and returns what each racing thread does with it.
    /// </summary>
    private static async Task RaceFirstUses(string arm)
    {
        var services = new ServiceCollection().AddSwitchboard(_ => { });
        var armMethod = typeof(ConcurrencyTests).GetMethod(arm, BindingFlags.NonPublic | BindingFlags.Static)!;

        var arms = (
            from a in TagArguments
            from b in TagArguments
            let tag = typeof(RaceTag<,>).MakeGenericType(a, b)
            select (Func<IMediator, Func<int, Task>>)armMethod.MakeGenericMethod(tag).Invoke(null, new object[] { services })!).ToList();

        await using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        await RaceRounds(arms.Select(armFor => armFor(mediator)).ToList());
    }

    /// <summary>
    /// Runs each round on <see cref="RaceThreads"/> dedicated threads that a barrier releases together, so the first
    /// dispatches of a round's request type really start at once. Every call is awaited only once all rounds are over.
    /// </summary>
    private static async Task RaceRounds(IReadOnlyList<Func<int, Task>> rounds)
    {
        var started = new Task[rounds.Count * RaceThreads];
        using var barrier = new Barrier(RaceThreads);

        var threads = Enumerable.Range(0, RaceThreads).Select(participant => new Thread(() =>
        {
            for (var round = 0; round < rounds.Count; round++)
            {
                if (!barrier.SignalAndWait(HangGuard))
                {
                    started[round * RaceThreads + participant] = Task.FromException(new TimeoutException("the race barrier never opened"));
                    return;
                }

                started[round * RaceThreads + participant] = StartSafely(rounds[round], participant);
            }
        })
        { IsBackground = true }).ToList();

        threads.ForEach(thread => thread.Start());
        foreach (var thread in threads)
        {
            Assert.True(thread.Join(HangGuard), "a racing thread never finished");
        }

        await Task.WhenAll(started.Select(task => task ?? Task.FromException(new InvalidOperationException("a race was never run"))))
            .WaitAsync(HangGuard);
    }

    // A dispatch that throws before it returns a task (building a wrapper, say) fails its own task, not the racing thread.
    private static Task StartSafely(Func<int, Task> operation, int participant)
    {
        try
        {
            return operation(participant);
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    private static Func<IMediator, Func<int, Task>> TypedSendArm<TTag>(IServiceCollection services)
    {
        services.AddTransient<IRequestHandler<RaceProbe<TTag>, RaceReply<TTag>>, RaceProbeHandler<TTag>>();

        return mediator => async participant =>
        {
            var request = new RaceProbe<TTag>(participant);
            switch (participant % 3)
            {
                case 0:
                    Assert.Equal(participant, (await mediator.Send(request)).Id);
                    break;
                case 1:
                    // Covariant: IRequest<RaceReply<TTag>> sent as IRequest<object>, racing the typed sends of the same new type.
                    Assert.Equal(participant, Assert.IsType<RaceReply<TTag>>(await mediator.Send((IRequest<object>)request)).Id);
                    break;
                default:
                    Assert.Equal(participant, Assert.IsType<RaceReply<TTag>>(await mediator.Send((object)request)).Id);
                    break;
            }
        };
    }

    [Fact]
    public Task Racing_first_sends_of_a_new_request_type_all_succeed_and_a_covariant_send_never_poisons_the_cache()
        => RaceFirstUses(nameof(TypedSendArm));

    private static Func<IMediator, Func<int, Task>> VoidSendArm<TTag>(IServiceCollection services)
    {
        services.AddTransient<IRequestHandler<RaceCommand<TTag>>, RaceCommandHandler<TTag>>();

        return mediator => async participant =>
        {
            var request = new RaceCommand<TTag>(new StrongBox<int>());

            if (participant % 2 == 0)
            {
                await mediator.Send(request);
            }
            else
            {
                // Send(object) asks the typed cache first, which caches that this type declares no response.
                Assert.Null(await mediator.Send((object)request));
            }

            Assert.Equal(1, request.Handled.Value);
        };
    }

    [Fact]
    public Task Racing_first_sends_of_a_new_void_request_type_all_succeed()
        => RaceFirstUses(nameof(VoidSendArm));

    private static Func<IMediator, Func<int, Task>> PublishArm<TTag>(IServiceCollection services)
    {
        services.AddTransient<INotificationHandler<RaceNote<TTag>>, RaceNoteHandler<TTag>>();

        return mediator => async participant =>
        {
            var notification = new RaceNote<TTag>(new StrongBox<int>());

            await (participant % 2 == 0 ? mediator.Publish(notification) : mediator.Publish((object)notification));

            Assert.Equal(1, notification.Handled.Value);
        };
    }

    [Fact]
    public Task Racing_first_publishes_of_a_new_notification_type_reach_the_handler_exactly_once_each()
        => RaceFirstUses(nameof(PublishArm));

    private static Func<IMediator, Func<int, Task>> StreamArm<TTag>(IServiceCollection services)
    {
        services.AddTransient<IStreamRequestHandler<RaceStream<TTag>, RaceReply<TTag>>, RaceStreamHandler<TTag>>();

        return mediator => participant =>
        {
            var request = new RaceStream<TTag>(participant);

            // CreateStream looks the wrapper up before it returns, so the race is in these calls; the items are checked after.
            Task<List<object?>> items = (participant % 3) switch
            {
                0 => CollectAsObjects(mediator.CreateStream(request)),
                1 => CollectAsObjects(mediator.CreateStream((IStreamRequest<object>)request)), // covariant
                _ => mediator.CreateStream((object)request).Collect(),
            };

            return Check(items);

            async Task Check(Task<List<object?>> collected)
            {
                var all = await collected;
                Assert.Equal(3, all.Count);
                Assert.All(all, item => Assert.Equal(participant, Assert.IsType<RaceReply<TTag>>(item).Id));
            }
        };

        static async Task<List<object?>> CollectAsObjects<T>(IAsyncEnumerable<T> stream)
            => (await stream.Collect()).Cast<object?>().ToList();
    }

    [Fact]
    public Task Racing_first_streams_of_a_new_stream_request_type_all_succeed_and_a_covariant_stream_never_poisons_the_cache()
        => RaceFirstUses(nameof(StreamArm));

    // --- Scope per dispatch under load ---------------------------------------

    [Fact]
    public async Task Hundreds_of_concurrent_dispatches_from_one_caller_each_get_a_scope_of_their_own_that_their_nested_dispatches_join()
    {
        const int perKind = 50;
        const int dispatches = perKind * 4;
        var callbacks = 0;

        await using var provider = BuildProvider(cfg => cfg.UseScopePerDispatch(async (scope, cancellationToken) =>
        {
            Interlocked.Increment(ref callbacks);
            await Task.Yield();
            scope.ServiceProvider.GetRequiredService<ScopeMarker>().SeededUser = LoadMessages.Name(scope.Message);
        }));
        await using var callerScope = provider.CreateAsyncScope();
        var callerMarker = callerScope.ServiceProvider.GetRequiredService<ScopeMarker>();
        var sender = callerScope.ServiceProvider.GetRequiredService<ISender>();
        var publisher = callerScope.ServiceProvider.GetRequiredService<IPublisher>();
        var run = new LoadRun(dispatches);
        var handedBack = new ConcurrentDictionary<string, List<ScopeMarker>>();

        var all = Enumerable.Range(0, dispatches).Select(id => Task.Run(async () =>
        {
            try
            {
                switch (id % 4)
                {
                    case 0:
                        var request = new LoadSend(id, run);
                        handedBack[LoadMessages.Name(request)] = new List<ScopeMarker>
                        {
                            id % 8 == 0 ? Assert.IsType<ScopeMarker>(await sender.Send((object)request)) : await sender.Send(request),
                        };
                        break;
                    case 1:
                        await sender.Send(new LoadCommand(id, run));
                        break;
                    case 2:
                        await publisher.Publish(new LoadNote(id, run));
                        break;
                    default:
                        var stream = new LoadStream(id, run);
                        var streamed = new List<ScopeMarker>();
                        await foreach (var marker in sender.CreateStream(stream))
                        {
                            Assert.False(marker.Disposed, "the scope is alive while the stream is enumerated");
                            streamed.Add(marker);
                            await Task.Yield();
                        }

                        handedBack[LoadMessages.Name(stream)] = streamed;
                        break;
                }
            }
            catch (Exception exception)
            {
                run.Gate.Abort(exception);
                throw;
            }
        })).ToList();

        await Task.WhenAll(all).WaitAsync(HangGuard);

        var records = run.Records.ToList();
        Assert.Equal(dispatches, records.Count);
        Assert.Equal(dispatches, records.Select(r => r.Message).Distinct().Count());

        // No two top-level dispatches ever shared a scope, and none ran in the caller's.
        var owns = records.Select(r => r.Own).ToList();
        Assert.Equal(dispatches, owns.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.DoesNotContain(callerMarker, owns);

        foreach (var record in records)
        {
            // Each callback seeded the scope of its own message, never a sibling's.
            Assert.Equal(record.Message, record.Own.SeededUser);

            // A send before the gate, two forked onto the pool after it, a stream of two and a publish.
            Assert.Equal(6, record.Nested.Count);
            Assert.All(record.Nested, nested => Assert.Same(record.Own, nested));

            Assert.True(record.Own.Disposed, $"the scope of {record.Message} was disposed");

            // Every send and stream handed its consumer the marker of its own scope, and only that one.
            if (handedBack.TryGetValue(record.Message, out var markers))
            {
                Assert.Equal(record.Message.StartsWith(nameof(LoadStream)) ? 4 : 1, markers.Count);
                Assert.All(markers, marker => Assert.Same(record.Own, marker));
            }
        }

        Assert.Equal(perKind * 2, handedBack.Count);
        Assert.Equal(dispatches, Volatile.Read(ref callbacks)); // nested dispatches joined a scope instead of creating one
        Assert.False(callerMarker.Disposed);

        // Nothing leaked into the caller's async context: its next send still gets a fresh scope.
        var after = await sender.Send(new ScopeProbe());
        Assert.DoesNotContain(after, owns);
        Assert.NotSame(callerMarker, after);
        Assert.True(after.Disposed);
        Assert.Equal(dispatches + 1, Volatile.Read(ref callbacks));
    }

    [Fact]
    public async Task Concurrent_streams_that_complete_break_early_or_are_cancelled_all_dispose_their_scopes()
    {
        const int streams = 200;
        const int items = 6;
        const int stopAfter = 2;
        var endings = Enum.GetValues<StreamEnding>();

        await using var provider = BuildProvider(cfg => cfg.UseScopePerDispatch());
        await using var callerScope = provider.CreateAsyncScope();
        var callerMarker = callerScope.ServiceProvider.GetRequiredService<ScopeMarker>();
        var sender = callerScope.ServiceProvider.GetRequiredService<ISender>();
        var ledger = new StreamLedger(streams);

        var all = Enumerable.Range(0, streams).Select(id => Task.Run(async () =>
        {
            var ending = endings[id % endings.Length];
            try
            {
                return (Ending: ending, Outcome: await Consume(id, ending));
            }
            catch (Exception exception)
            {
                ledger.Gate.Abort(exception);
                throw;
            }
        })).ToList();

        var outcomes = await Task.WhenAll(all).WaitAsync(HangGuard);

        foreach (var (ending, outcome) in outcomes)
        {
            Assert.Equal(
                ending switch
                {
                    StreamEnding.Completes => $"completed after {items}",
                    StreamEnding.BreaksEarly => $"broke after {stopAfter}",
                    _ => $"cancelled after {stopAfter}",
                },
                outcome);
        }

        Assert.Equal(streams, ledger.Scopes.Count);
        Assert.Equal(streams, ledger.Scopes.Values.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.All(ledger.Scopes, scope => Assert.True(scope.Value.Disposed, $"the scope of stream {scope.Key} was disposed"));
        Assert.Equal(streams, ledger.Finalized.Count); // the handler's own enumerator was disposed too, however the stream ended
        Assert.False(callerMarker.Disposed);

        async Task<string> Consume(int id, StreamEnding ending)
        {
            using var cancellation = new CancellationTokenSource();
            var received = 0;

            if (ending == StreamEnding.CancelsWhileTheHandlerIsAwaiting)
            {
                var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var enumerator = sender.CreateStream(new EndingStream(id, ledger, items, ParkAt: stopAfter, parked), cancellation.Token)
                    .GetAsyncEnumerator();

                while (received < stopAfter && await enumerator.MoveNextAsync())
                {
                    received++;
                }

                var pending = enumerator.MoveNextAsync().AsTask();
                await parked.Task;
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                return $"cancelled after {received}";
            }

            var (stream, enumeratorToken) = ending == StreamEnding.CancelsThroughCreateStreamToken
                ? (sender.CreateStream(new EndingStream(id, ledger, items), cancellation.Token), default)
                : (sender.CreateStream(new EndingStream(id, ledger, items)), cancellation.Token);

            try
            {
                await foreach (var item in stream.WithCancellation(enumeratorToken))
                {
                    Assert.Equal(received, item);
                    received++;

                    if (received == stopAfter && ending == StreamEnding.BreaksEarly)
                    {
                        return $"broke after {received}";
                    }

                    if (received == stopAfter && ending != StreamEnding.Completes)
                    {
                        cancellation.Cancel();
                    }

                    await Task.Yield();
                }
            }
            catch (OperationCanceledException) when (ending != StreamEnding.Completes && ending != StreamEnding.BreaksEarly)
            {
                return $"cancelled after {received}";
            }

            return $"completed after {received}";
        }
    }

    [Fact]
    public async Task A_stream_a_handler_consumes_in_a_scope_of_its_own_never_leaves_that_scope_behind_in_the_handlers_context()
    {
        const int dispatches = 100;

        await using var provider = BuildProvider(cfg => cfg.UseScopePerDispatch());
        await using var callerScope = provider.CreateAsyncScope();
        var sender = callerScope.ServiceProvider.GetRequiredService<ISender>();

        var results = await Task.WhenAll(Enumerable.Range(0, dispatches)
            .Select(_ => Task.Run(() => sender.Send(new IsolatedStreamConsumer())))).WaitAsync(HangGuard);

        Assert.All(results, result =>
        {
            Assert.NotSame(result.Own, result.Streamed); // the stream got a scope of its own...
            Assert.True(result.Streamed.Disposed);

            // ...and every send the handler made between the items and after them still joined the handler's scope.
            Assert.Equal(3, result.SentAfterItems.Count);
            Assert.All(result.SentAfterItems, sent => Assert.Same(result.Own, sent));
        });
        Assert.Equal(dispatches, results.Select(r => r.Own).Distinct(ReferenceEqualityComparer.Instance).Count());
    }
}
