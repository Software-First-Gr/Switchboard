using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Switchboard;

/// <summary>
/// OpenTelemetry-compatible tracing and metrics, built on <see cref="ActivitySource"/> and
/// <see cref="Meter"/> from the base class library — no extra dependency. Nothing is recorded
/// until a listener subscribes, e.g. <c>tracing.AddSource(SwitchboardTelemetry.ActivitySourceName)</c>
/// and <c>metrics.AddMeter(SwitchboardTelemetry.MeterName)</c>.
/// </summary>
public static class SwitchboardTelemetry
{
    /// <summary>Name of the <see cref="ActivitySource"/> emitting one span per send, stream, publish and notification handler.</summary>
    public const string ActivitySourceName = "Switchboard";

    /// <summary>Name of the <see cref="Meter"/> emitting the duration histograms.</summary>
    public const string MeterName = "Switchboard";

    /// <summary>Histogram of <c>Send</c> durations in seconds, tagged with <c>switchboard.request</c> and, on failure, <c>error.type</c>.</summary>
    public const string RequestDurationInstrument = "switchboard.request.duration";

    /// <summary>
    /// Histogram of <c>CreateStream</c> durations in seconds, from the first item requested until the stream ends, fails or
    /// is abandoned, the consumer's time between items included. Tagged with <c>switchboard.request</c> and, on failure, <c>error.type</c>.
    /// </summary>
    public const string StreamDurationInstrument = "switchboard.stream.duration";

    /// <summary>Histogram of <c>Publish</c> durations in seconds, tagged with <c>switchboard.notification</c> and, on failure, <c>error.type</c>.</summary>
    public const string NotificationDurationInstrument = "switchboard.notification.duration";

    internal const string RequestTag = "switchboard.request";
    internal const string NotificationTag = "switchboard.notification";
    internal const string HandlerTag = "switchboard.handler";
    internal const string ErrorTypeTag = "error.type";
    internal const string StreamItemsTag = "switchboard.stream.items";

    private static readonly string? Version = typeof(SwitchboardTelemetry).Assembly.GetName().Version?.ToString();

    private static readonly ActivitySource Source = new(ActivitySourceName, Version);
    private static readonly Meter Meter = new(MeterName, Version);

    private static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>(
        RequestDurationInstrument, unit: "s", description: "Duration of requests sent through Switchboard, pipeline behaviors included.");

    private static readonly Histogram<double> StreamDuration = Meter.CreateHistogram<double>(
        StreamDurationInstrument, unit: "s", description: "Duration of streams created through Switchboard, from the first item requested until the stream ends.");

    private static readonly Histogram<double> NotificationDuration = Meter.CreateHistogram<double>(
        NotificationDurationInstrument, unit: "s", description: "Duration of notifications published through Switchboard, all handlers included.");

    internal static bool IsRequestObserved => Source.HasListeners() || RequestDuration.Enabled;

    internal static bool IsStreamObserved => Source.HasListeners() || StreamDuration.Enabled;

    internal static bool IsNotificationObserved => Source.HasListeners() || NotificationDuration.Enabled;

    internal static Task<T> ObserveRequest<T>(string requestName, Func<Task<T>> run)
        => Observe("Send", RequestTag, requestName, RequestDuration, run);

    internal static Task<T> ObserveNotification<T>(string notificationName, Func<Task<T>> run)
        => Observe("Publish", NotificationTag, notificationName, NotificationDuration, run);

    /// <summary>
    /// Wraps <paramref name="items"/> in a <c>CreateStream</c> span and duration measurement that start with the first
    /// item requested and end when the stream completes, fails, or is disposed early.
    /// </summary>
    internal static async IAsyncEnumerable<T> ObserveStream<T>(
        string requestName, IAsyncEnumerable<T> items, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var activity = Source.StartActivity("CreateStream " + requestName);
        activity?.SetTag(RequestTag, requestName);

        var started = Stopwatch.GetTimestamp();
        string? errorType = null;
        var count = 0;

        try
        {
            await using var enumerator = items.GetAsyncEnumerator(cancellationToken);

            while (true)
            {
                // An iterator resumes on its consumer's execution context after every yield, so the span is made
                // current again before each step: whatever the handler does next is traced as its child.
                if (activity is not null)
                {
                    Activity.Current = activity;
                }

                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync();
                }
                catch (Exception exception)
                {
                    errorType = exception.GetType().FullName;
                    RecordException(activity, exception);
                    throw;
                }

                if (!moved)
                {
                    break;
                }

                count++;
                yield return enumerator.Current;
            }
        }
        finally
        {
            activity?.SetTag(StreamItemsTag, count);

            if (StreamDuration.Enabled)
            {
                var tags = new TagList { { RequestTag, requestName } };
                if (errorType is not null)
                {
                    tags.Add(ErrorTypeTag, errorType);
                }

                StreamDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
            }
        }
    }

    /// <summary>Starts a child span for one notification handler; <see langword="null"/> when nobody is listening.</summary>
    internal static Activity? StartHandler(string notificationName, Type handlerType)
    {
        var activity = Source.StartActivity("Handle " + notificationName);
        activity?.SetTag(NotificationTag, notificationName);
        activity?.SetTag(HandlerTag, DisplayName(handlerType));
        return activity;
    }

    internal static void RecordException(Activity? activity, Exception exception)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(ErrorTypeTag, exception.GetType().FullName);

        // A caller that gave up (navigated away, timed out) is not a failure of the handler.
        if (exception is OperationCanceledException)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            { "exception.type", exception.GetType().FullName },
            { "exception.message", exception.Message },
            { "exception.stacktrace", exception.ToString() },
        }));
    }

    /// <summary>A readable, low-cardinality type name: <c>GetOrder</c>, <c>Envelope&lt;Invoice&gt;</c>.</summary>
    internal static string DisplayName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var tick = type.Name.IndexOf('`');
        var name = tick < 0 ? type.Name : type.Name[..tick];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(DisplayName))}>";
    }

    private static async Task<T> Observe<T>(
        string operation, string tagName, string messageName, Histogram<double> duration, Func<Task<T>> run)
    {
        using var activity = Source.StartActivity(operation + " " + messageName);
        activity?.SetTag(tagName, messageName);

        var started = Stopwatch.GetTimestamp();
        string? errorType = null;

        try
        {
            return await run();
        }
        catch (Exception exception)
        {
            errorType = exception.GetType().FullName;
            RecordException(activity, exception);
            throw;
        }
        finally
        {
            if (duration.Enabled)
            {
                var tags = new TagList { { tagName, messageName } };
                if (errorType is not null)
                {
                    tags.Add(ErrorTypeTag, errorType);
                }

                duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
            }
        }
    }
}
