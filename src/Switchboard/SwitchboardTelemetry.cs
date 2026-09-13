using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
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
    /// <summary>Name of the <see cref="ActivitySource"/> emitting one span per send, publish and notification handler.</summary>
    public const string ActivitySourceName = "Switchboard";

    /// <summary>Name of the <see cref="Meter"/> emitting the duration histograms.</summary>
    public const string MeterName = "Switchboard";

    /// <summary>Histogram of <c>Send</c> durations in seconds, tagged with <c>switchboard.request</c> and, on failure, <c>error.type</c>.</summary>
    public const string RequestDurationInstrument = "switchboard.request.duration";

    /// <summary>Histogram of <c>Publish</c> durations in seconds, tagged with <c>switchboard.notification</c> and, on failure, <c>error.type</c>.</summary>
    public const string NotificationDurationInstrument = "switchboard.notification.duration";

    internal const string RequestTag = "switchboard.request";
    internal const string NotificationTag = "switchboard.notification";
    internal const string HandlerTag = "switchboard.handler";
    internal const string ErrorTypeTag = "error.type";

    private static readonly string? Version = typeof(SwitchboardTelemetry).Assembly.GetName().Version?.ToString();

    private static readonly ActivitySource Source = new(ActivitySourceName, Version);
    private static readonly Meter Meter = new(MeterName, Version);

    private static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>(
        RequestDurationInstrument, unit: "s", description: "Duration of requests sent through Switchboard, pipeline behaviors included.");

    private static readonly Histogram<double> NotificationDuration = Meter.CreateHistogram<double>(
        NotificationDurationInstrument, unit: "s", description: "Duration of notifications published through Switchboard, all handlers included.");

    internal static bool IsRequestObserved => Source.HasListeners() || RequestDuration.Enabled;

    internal static bool IsNotificationObserved => Source.HasListeners() || NotificationDuration.Enabled;

    internal static Task<T> ObserveRequest<T>(string requestName, Func<Task<T>> run)
        => Observe("Send", RequestTag, requestName, RequestDuration, run);

    internal static Task<T> ObserveNotification<T>(string notificationName, Func<Task<T>> run)
        => Observe("Publish", NotificationTag, notificationName, NotificationDuration, run);

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
