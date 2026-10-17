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

public sealed record FailingTelemetryNote : INotification;

public sealed class FailingTelemetryNoteHandler : INotificationHandler<FailingTelemetryNote>
{
    public async Task Handle(FailingTelemetryNote notification, CancellationToken cancellationToken)
    {
        await Task.Yield();
        throw new InvalidOperationException("telemetry note failed");
    }
}

public sealed record CancellableTelemetryProbe : IRequest<int>;

public sealed class CancellableTelemetryProbeHandler : IRequestHandler<CancellableTelemetryProbe, int>
{
    public async Task<int> Handle(CancellableTelemetryProbe request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }
}

public sealed record CancellableTelemetryStreamProbe : IStreamRequest<int>;

public sealed class CancellableTelemetryStreamProbeHandler : IStreamRequestHandler<CancellableTelemetryStreamProbe, int>
{
    public async IAsyncEnumerable<int> Handle(
        CancellableTelemetryStreamProbe request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; ; i++)
        {
            await Task.Delay(1, cancellationToken);
            yield return i;
        }
    }
}

/// <summary>See <see cref="TelemetryTests"/>: listeners are process-wide, so every assertion filters on the probes above.</summary>
[Collection(TelemetryTests.CollectionName)]
public sealed class TelemetryGapTests
{
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddSwitchboard(cfg => cfg.RegisterServicesFromAssemblyContaining<TelemetryGapTests>())
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
    public void Every_duration_histogram_is_measured_in_seconds()
    {
        RuntimeHelpers.RunClassConstructor(typeof(SwitchboardTelemetry).TypeHandle);
        var units = new ConcurrentDictionary<string, string?>();
        using var meterListener = new MeterListener
        {
            // Only looks: no instrument is enabled, so no dispatch elsewhere starts being observed.
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == SwitchboardTelemetry.MeterName)
                {
                    units[instrument.Name] = instrument.Unit;
                }
            },
            // See TelemetryTests: on .NET 8, Dispose() only releases the instruments when this callback is set.
            MeasurementsCompleted = (_, _) => { },
        };
        meterListener.Start();

        Assert.Equal("s", units[SwitchboardTelemetry.RequestDurationInstrument]);
        Assert.Equal("s", units[SwitchboardTelemetry.StreamDurationInstrument]);
        Assert.Equal("s", units[SwitchboardTelemetry.NotificationDurationInstrument]);
    }

    [Fact]
    public async Task A_failing_notification_handler_marks_its_own_span_and_the_publish_span_as_errors()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = ListenToActivities(stopped);
        await using var provider = BuildProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetRequiredService<IPublisher>().Publish(new FailingTelemetryNote()));

        var publish = Assert.Single(stopped, a => a.DisplayName == "Publish FailingTelemetryNote");
        var handler = Assert.Single(stopped, a => a.DisplayName == "Handle FailingTelemetryNote");
        Assert.Equal(publish.SpanId, handler.ParentSpanId);
        Assert.Equal("FailingTelemetryNote", handler.GetTagItem("switchboard.notification"));
        Assert.Equal("FailingTelemetryNoteHandler", handler.GetTagItem("switchboard.handler"));
        Assert.Equal(ActivityStatusCode.Error, handler.Status);
        Assert.Equal("telemetry note failed", handler.StatusDescription);
        Assert.Equal(typeof(InvalidOperationException).FullName, handler.GetTagItem("error.type"));
        Assert.Equal(ActivityStatusCode.Error, publish.Status);

        // The OpenTelemetry semantic conventions for a recorded exception.
        var exceptionEvent = Assert.Single(handler.Events, e => e.Name == "exception");
        var tags = exceptionEvent.Tags.ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal(typeof(InvalidOperationException).FullName, tags["exception.type"]);
        Assert.Equal("telemetry note failed", tags["exception.message"]);
        Assert.Contains("telemetry note failed", Assert.IsType<string>(tags["exception.stacktrace"]));
        Assert.Contains(nameof(FailingTelemetryNoteHandler), (string)tags["exception.stacktrace"]!);
    }

    [Fact]
    public async Task A_cancelled_request_tags_its_span_with_the_cancellation_but_does_not_mark_it_as_an_error()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = ListenToActivities(stopped);
        await using var provider = BuildProvider();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetRequiredService<ISender>().Send(new CancellableTelemetryProbe(), new CancellationToken(canceled: true)));

        var span = Assert.Single(stopped, a => a.DisplayName == "Send CancellableTelemetryProbe");
        Assert.Equal(typeof(TaskCanceledException).FullName, span.GetTagItem("error.type"));
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.DoesNotContain(span.Events, e => e.Name == "exception");
    }

    [Fact]
    public async Task A_cancelled_stream_tags_its_span_with_the_cancellation_but_does_not_mark_it_as_an_error()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = ListenToActivities(stopped);
        await using var provider = BuildProvider();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in provider.GetRequiredService<ISender>()
                               .CreateStream(new CancellableTelemetryStreamProbe(), cancellation.Token))
            {
                cancellation.Cancel();
            }
        });

        var span = Assert.Single(stopped, a => a.DisplayName == "CreateStream CancellableTelemetryStreamProbe");
        Assert.Equal(typeof(TaskCanceledException).FullName, span.GetTagItem("error.type"));
        Assert.Equal(1, span.GetTagItem("switchboard.stream.items"));
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.DoesNotContain(span.Events, e => e.Name == "exception");
    }

    [Fact]
    public async Task A_traced_stream_still_reaches_the_handler_with_the_WithCancellation_token()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = ListenToActivities(stopped);
        await using var provider = BuildProvider();
        using var cancellation = new CancellationTokenSource();
        var received = 0;

        // The handler never ends on its own; only the consumer's token, passed through the traced stream, stops it.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in provider.GetRequiredService<ISender>()
                               .CreateStream(new CancellableTelemetryStreamProbe())
                               .WithCancellation(cancellation.Token))
            {
                cancellation.Cancel();
                if (++received > 50)
                {
                    break;
                }
            }
        });

        Assert.Equal(1, received);
        Assert.Single(stopped, a => a.DisplayName == "CreateStream CancellableTelemetryStreamProbe");
    }
}
