using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Switchboard.Tests;

/// <summary>What one concurrent stream's handler saw as <see cref="Activity.Current"/> at every step, and its nested send's span.</summary>
public sealed class SpanLedger
{
    public SpanLedger(int participants) => Gate = new ConcurrencyGate(participants);

    public ConcurrencyGate Gate { get; }

    public ConcurrentDictionary<int, ConcurrentQueue<Activity?>> SeenByHandler { get; } = new();

    public ConcurrentDictionary<int, Activity?> SeenByNestedSend { get; } = new();

    public void Saw(int id) => SeenByHandler.GetOrAdd(id, _ => new ConcurrentQueue<Activity?>()).Enqueue(Activity.Current);
}

public sealed record SpanProbeStream(int Id, SpanLedger Ledger) : IStreamRequest<int>;

/// <summary>
/// Records <see cref="Activity.Current"/> before every item — on its first step, after resuming on the thread pool, after
/// every resumption from the consumer's execution context — and sends once between items.
/// </summary>
public sealed class SpanProbeStreamHandler : IStreamRequestHandler<SpanProbeStream, int>
{
    private readonly ISender _sender;

    public SpanProbeStreamHandler(ISender sender) => _sender = sender;

    public async IAsyncEnumerable<int> Handle(SpanProbeStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        request.Ledger.Saw(request.Id);
        await Task.Yield();
        request.Ledger.Saw(request.Id);
        yield return 0;

        // Every stream's span is alive from here on.
        request.Ledger.Saw(request.Id);
        await request.Ledger.Gate.Arrive();
        request.Ledger.Saw(request.Id);
        yield return 1;

        request.Ledger.Saw(request.Id);
        await _sender.Send(new SpanChildProbe(request.Id, request.Ledger), cancellationToken);
        request.Ledger.Saw(request.Id);
        yield return 2;

        await Task.Yield();
        request.Ledger.Saw(request.Id);
        yield return 3;
    }
}

public sealed record SpanChildProbe(int Id, SpanLedger Ledger) : IRequest;

public sealed class SpanChildProbeHandler : IRequestHandler<SpanChildProbe>
{
    public Task Handle(SpanChildProbe request, CancellationToken cancellationToken)
    {
        request.Ledger.SeenByNestedSend[request.Id] = Activity.Current;
        return Task.CompletedTask;
    }
}

/// <summary>Attaches process-wide listeners, so it joins <see cref="TelemetryTests.CollectionName"/> and filters on its own probe types.</summary>
[Collection(TelemetryTests.CollectionName)]
public sealed class ConcurrencyTelemetryTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private const string ConsumerSourceName = "Switchboard.Tests.Consumers";

    private static readonly ActivitySource Consumers = new(ConsumerSourceName);

    private const string StreamSpan = "CreateStream SpanProbeStream";

    [Fact]
    public async Task Concurrent_streams_each_get_one_span_that_their_handler_alone_sees_as_current_at_every_step()
    {
        const int streams = 100;

        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is SwitchboardTelemetry.ActivitySourceName or ConsumerSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        var durations = new ConcurrentQueue<string?>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == SwitchboardTelemetry.MeterName && instrument.Name == SwitchboardTelemetry.StreamDurationInstrument)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
            // See TelemetryTests: on .NET 8 the instruments are only released on Dispose when this callback is set.
            MeasurementsCompleted = (_, _) => { },
        };
        meterListener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "switchboard.request" && Equals(tag.Value, nameof(SpanProbeStream)))
                {
                    durations.Enqueue(tag.Value as string);
                }
            }
        });
        meterListener.Start();

        await using var provider = new ServiceCollection()
            .AddSwitchboard(cfg => cfg.RegisterServicesFromAssemblyContaining<ConcurrencyTelemetryTests>())
            .BuildServiceProvider();
        var sender = provider.GetRequiredService<ISender>();
        var ledger = new SpanLedger(streams);
        var consumerSpans = new ConcurrentDictionary<int, Activity>();

        var all = Enumerable.Range(0, streams).Select(id => Task.Run(async () =>
        {
            try
            {
                using var consumer = Consumers.StartActivity("consume");
                Assert.NotNull(consumer);
                consumerSpans[id] = consumer;
                var received = new List<int>();

                await foreach (var item in sender.CreateStream(new SpanProbeStream(id, ledger)))
                {
                    // The consumer's own code between items is not traced inside the stream's span.
                    Assert.Same(consumer, Activity.Current);
                    received.Add(item);
                    await Task.Yield();
                }

                Assert.Same(consumer, Activity.Current);
                Assert.Equal(new[] { 0, 1, 2, 3 }, received);
            }
            catch (Exception exception)
            {
                ledger.Gate.Abort(exception);
                throw;
            }
        })).ToList();

        await Task.WhenAll(all).WaitAsync(HangGuard);

        var streamSpans = stopped.Where(a => a.DisplayName == StreamSpan).ToList();
        Assert.Equal(streams, streamSpans.Count);
        Assert.Equal(streams, streamSpans.Distinct().Count());

        var owners = new HashSet<Activity>();
        for (var id = 0; id < streams; id++)
        {
            var seen = ledger.SeenByHandler[id].ToList();
            Assert.Equal(7, seen.Count);

            // At every step the handler saw one span as current: a CreateStream span of this run, never a sibling's.
            var own = Assert.IsType<Activity>(seen[0]);
            Assert.All(seen, current => Assert.Same(own, current));
            Assert.Contains(own, streamSpans);
            Assert.True(owners.Add(own), $"stream {id} saw a span another stream saw too");

            // The span was started in the consumer's context, and the nested send was traced as its child.
            Assert.Equal(consumerSpans[id].SpanId, own.ParentSpanId);
            var nested = Assert.IsType<Activity>(ledger.SeenByNestedSend[id]);
            Assert.Equal("Send SpanChildProbe", nested.DisplayName);
            Assert.Same(own, nested.Parent);

            Assert.Equal(4, own.GetTagItem("switchboard.stream.items"));
        }

        Assert.Equal(streams, durations.Count);
    }
}
