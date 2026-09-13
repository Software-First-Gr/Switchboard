using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Switchboard.Tests;

public sealed record TelemetryProbe : IRequest<int>;

public sealed class TelemetryProbeHandler : IRequestHandler<TelemetryProbe, int>
{
    public Task<int> Handle(TelemetryProbe request, CancellationToken cancellationToken) => Task.FromResult(42);
}

public sealed record FailingTelemetryProbe : IRequest;

public sealed class FailingTelemetryProbeHandler : IRequestHandler<FailingTelemetryProbe>
{
    public Task Handle(FailingTelemetryProbe request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("boom");
}

public sealed record TelemetryNote : INotification;

public sealed class TelemetryNoteHandler : INotificationHandler<TelemetryNote>
{
    public Task Handle(TelemetryNote notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Listeners are process-wide, so other tests running in parallel may emit Switchboard telemetry too;
/// every assertion filters on the probe types declared above. Tests that must not run while a listener
/// is attached join <see cref="CollectionName"/>.
/// </summary>
[Collection(CollectionName)]
public sealed class TelemetryTests
{
    public const string CollectionName = "Switchboard telemetry listeners";

    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddSwitchboard(cfg => cfg.RegisterServicesFromAssemblyContaining<TelemetryTests>())
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
    public async Task Send_emits_a_span_named_after_the_request()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = ListenToActivities(stopped);
        await using var provider = BuildProvider();

        Assert.Equal(42, await provider.GetRequiredService<ISender>().Send(new TelemetryProbe()));

        var span = Assert.Single(stopped, a => a.DisplayName == "Send TelemetryProbe");
        Assert.Equal("TelemetryProbe", span.GetTagItem("switchboard.request"));
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
    }

    [Fact]
    public async Task A_failing_request_marks_its_span_as_an_error()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = ListenToActivities(stopped);
        await using var provider = BuildProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetRequiredService<ISender>().Send(new FailingTelemetryProbe()));

        var span = Assert.Single(stopped, a => a.DisplayName == "Send FailingTelemetryProbe");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName, span.GetTagItem("error.type"));
        Assert.Contains(span.Events, e => e.Name == "exception");
    }

    [Fact]
    public async Task Publish_emits_a_span_with_one_child_per_handler()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = ListenToActivities(stopped);
        await using var provider = BuildProvider();

        await provider.GetRequiredService<IPublisher>().Publish(new TelemetryNote());

        var publish = Assert.Single(stopped, a => a.DisplayName == "Publish TelemetryNote");
        var handler = Assert.Single(stopped, a => a.DisplayName == "Handle TelemetryNote");
        Assert.Equal(publish.SpanId, handler.ParentSpanId);
        Assert.Equal("TelemetryNoteHandler", handler.GetTagItem("switchboard.handler"));
    }

    [Fact]
    public async Task Send_records_duration_with_the_request_name_and_error_type()
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
        };
        meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Enqueue((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        meterListener.Start();
        await using var provider = BuildProvider();
        var sender = provider.GetRequiredService<ISender>();

        await sender.Send(new TelemetryProbe());
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.Send(new FailingTelemetryProbe()));

        var ok = Assert.Single(measurements, m => Equals(m.Tags.GetValueOrDefault("switchboard.request"), "TelemetryProbe"));
        Assert.Equal(SwitchboardTelemetry.RequestDurationInstrument, ok.Instrument);
        Assert.True(ok.Value >= 0);
        Assert.False(ok.Tags.ContainsKey("error.type"));

        var failed = Assert.Single(measurements, m => Equals(m.Tags.GetValueOrDefault("switchboard.request"), "FailingTelemetryProbe"));
        Assert.Equal(typeof(InvalidOperationException).FullName, failed.Tags["error.type"]);
    }
}
