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

/// <summary>Yields the id of the span current inside the handler, before and after its first yield.</summary>
public sealed record TelemetryStreamProbe : IStreamRequest<string?>;

public sealed class TelemetryStreamProbeHandler : IStreamRequestHandler<TelemetryStreamProbe, string?>
{
    public async IAsyncEnumerable<string?> Handle(TelemetryStreamProbe request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return Activity.Current?.SpanId.ToString();
        await Task.Yield();
        yield return Activity.Current?.SpanId.ToString();
    }
}

public sealed record FailingTelemetryStreamProbe : IStreamRequest<int>;

public sealed class FailingTelemetryStreamProbeHandler : IStreamRequestHandler<FailingTelemetryStreamProbe, int>
{
    public async IAsyncEnumerable<int> Handle(FailingTelemetryStreamProbe request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return 1;
        throw new InvalidOperationException("boom");
    }
}

/// <summary>See <see cref="TelemetryTests"/>: listeners are process-wide, so every assertion filters on the probes above.</summary>
[Collection(TelemetryTests.CollectionName)]
public sealed class StreamTelemetryTests
{
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddSwitchboard(cfg => cfg.RegisterServicesFromAssemblyContaining<StreamTelemetryTests>())
            .BuildServiceProvider();

    private static ActivityListener ListenToActivities(ConcurrentQueue<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SwitchboardTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    [Fact]
    public async Task A_stream_emits_one_span_current_for_the_handler_at_every_step_but_never_for_the_consumer()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = ListenToActivities(stopped);
        await using var provider = BuildProvider();
        var seenByConsumer = new List<Activity?>();
        var seenByHandler = new List<string?>();

        await foreach (var spanId in provider.GetRequiredService<ISender>().CreateStream(new TelemetryStreamProbe()))
        {
            seenByHandler.Add(spanId);
            seenByConsumer.Add(Activity.Current);
        }

        var span = Assert.Single(stopped, a => a.DisplayName == "CreateStream TelemetryStreamProbe");
        Assert.Equal("TelemetryStreamProbe", span.GetTagItem("switchboard.request"));
        Assert.Equal(2, span.GetTagItem("switchboard.stream.items"));
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.Equal(new[] { span.SpanId.ToString(), span.SpanId.ToString() }, seenByHandler);
        Assert.All(seenByConsumer, Assert.Null);
    }

    [Fact]
    public async Task A_stream_abandoned_early_still_ends_its_span()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = ListenToActivities(stopped);
        await using var provider = BuildProvider();

        await foreach (var _ in provider.GetRequiredService<ISender>().CreateStream((object)new TelemetryStreamProbe()))
        {
            break;
        }

        var span = Assert.Single(stopped, a => a.DisplayName == "CreateStream TelemetryStreamProbe");
        Assert.Equal(1, span.GetTagItem("switchboard.stream.items"));
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
    }

    [Fact]
    public async Task A_failing_stream_marks_its_span_as_an_error()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = ListenToActivities(stopped);
        await using var provider = BuildProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetRequiredService<ISender>().CreateStream(new FailingTelemetryStreamProbe()).Collect());

        var span = Assert.Single(stopped, a => a.DisplayName == "CreateStream FailingTelemetryStreamProbe");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName, span.GetTagItem("error.type"));
        Assert.Equal(1, span.GetTagItem("switchboard.stream.items"));
        Assert.Contains(span.Events, e => e.Name == "exception");
    }

    [Fact]
    public async Task A_stream_records_its_duration_with_the_request_name_and_error_type()
    {
        var measurements = new ConcurrentQueue<(string Instrument, double Value, Dictionary<string, object?> Tags)>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == SwitchboardTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
            // See TelemetryTests: on .NET 8, Dispose() only releases the instruments when this callback is set.
            MeasurementsCompleted = (_, _) => { },
        };
        meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Enqueue((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        meterListener.Start();
        await using var provider = BuildProvider();
        var sender = provider.GetRequiredService<ISender>();

        await sender.CreateStream(new TelemetryStreamProbe()).Collect();
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.CreateStream(new FailingTelemetryStreamProbe()).Collect());

        var ok = Assert.Single(measurements, m => Equals(m.Tags.GetValueOrDefault("switchboard.request"), "TelemetryStreamProbe"));
        Assert.Equal(SwitchboardTelemetry.StreamDurationInstrument, ok.Instrument);
        Assert.True(ok.Value >= 0);
        Assert.False(ok.Tags.ContainsKey("error.type"));

        var failed = Assert.Single(measurements, m => Equals(m.Tags.GetValueOrDefault("switchboard.request"), "FailingTelemetryStreamProbe"));
        Assert.Equal(SwitchboardTelemetry.StreamDurationInstrument, failed.Instrument);
        Assert.Equal(typeof(InvalidOperationException).FullName, failed.Tags["error.type"]);
    }
}
