using System.Diagnostics;

namespace Spinneret.Queue;

/// <summary>Identifiers for the queue's tracing instrumentation.</summary>
public static class QueueDiagnostics
{
    /// <summary>
    /// Name of the <see cref="ActivitySource"/> for the queue's publish and process spans, e.g. for OpenTelemetry's <c>AddSource</c>.
    /// </summary>
    public const string ActivitySourceName = "Spinneret.Queue";
}

// Hosts' dashboards query these: renaming one is a breaking change.
internal static class QueueTags
{
    internal const string System = "messaging.system";
    internal const string SystemName = "spinneret";
    internal const string Operation = "messaging.operation";
    internal const string Destination = "messaging.destination.name";

    // The transport's own id; never the dedupe key (they coincide only on Cloud Tasks).
    internal const string MessageId = "messaging.message.id";

    internal const string DedupeKey = "spinneret.queue.dedupe_key";

    internal const string RequestType = "spinneret.request.type";
    internal const string Attempt = "spinneret.queue.attempt";
    internal const string MaxAttempts = "spinneret.queue.max_attempts";
    internal const string Outcome = "spinneret.queue.outcome";
}

// Documented in docs/queue.md: renaming one breaks dashboards.
internal static class QueueOutcome
{
    internal const string Ack = "ack";
    internal const string Retry = "retry";
    internal const string Defer = "defer";
    internal const string DeadLetter = "deadletter";
    internal const string Discard = "discard";
    internal const string TransportRetry = "transport-retry";
}

// Propagation reads Activity.Current, so it works even with no listener (ASP.NET Core always starts a request activity).
internal static class QueueTracing
{
    private static readonly string? Version = typeof(QueueTracing).Assembly.GetName().Version?.ToString();

    private static readonly ActivitySource Source = new(QueueDiagnostics.ActivitySourceName, Version);

    internal static Activity? StartProducer(string channel, QueueEnvelope envelope, string? dedupeKey)
    {
        // Tags at creation: a sampler only sees what the creation options carry.
        var tags = new ActivityTagsCollection
        {
            [QueueTags.System] = QueueTags.SystemName,
            [QueueTags.Operation] = "publish",
            [QueueTags.Destination] = channel,
            [QueueTags.RequestType] = envelope.RequestTypeName,
        };

        if (dedupeKey is not null)
            tags[QueueTags.DedupeKey] = dedupeKey;

        return Source.StartActivity(
            $"{ShortName(envelope.RequestTypeName)} publish", ActivityKind.Producer, default(ActivityContext), tags);
    }

    // Call after StartProducer. Coalesces so retries and deferrals stay in the original producer's trace.
    internal static QueueEnvelope StampTraceContext(QueueEnvelope envelope)
    {
        // Non-W3C (legacy Request-Id) ids are rejected by ActivityContext.TryParse, so keep them off the wire.
        var current = Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity ? activity : null;

        return envelope with
        {
            TraceParent = envelope.TraceParent ?? current?.Id,
            TraceState = envelope.TraceState ?? current?.TraceStateString,
        };
    }

    internal static Activity? StartConsumer(QueueEnvelope envelope, string taskId)
    {
        var tags = new ActivityTagsCollection
        {
            [QueueTags.System] = QueueTags.SystemName,
            [QueueTags.Operation] = "process",
            [QueueTags.MessageId] = taskId,
            [QueueTags.RequestType] = envelope.RequestTypeName,
            [QueueTags.Attempt] = envelope.PriorFailures + 1,
        };

        return Source.StartActivity(
            $"{ShortName(envelope.RequestTypeName)} process", ActivityKind.Consumer, ParentFor(envelope), tags);
    }

    // UIs truncate the end of long span names; the full name stays on the RequestType tag.
    private static string ShortName(string requestTypeName)
    {
        // A closed generic's FullName has bracketed, dotted assembly-qualified arguments.
        var name = requestTypeName.IndexOf('[') is var generic && generic >= 0
            ? requestTypeName[..generic]
            : requestTypeName;

        var cut = name.LastIndexOfAny(['.', '+']);
        return cut >= 0 && cut < name.Length - 1 ? name[(cut + 1)..] : name;
    }

    // Ambient wins when it is already in the producer's trace (in-band traceparent), so the delivery hop stays visible.
    private static ActivityContext ParentFor(QueueEnvelope envelope)
    {
        if (!ActivityContext.TryParse(envelope.TraceParent, envelope.TraceState, isRemote: true, out var producer))
            return default;

        return Activity.Current is { } current && current.TraceId == producer.TraceId ? default : producer;
    }

    internal static void SetOutcome(this Activity? activity, string outcome, string? error = null)
    {
        if (activity is null)
            return;

        activity.SetTag(QueueTags.Outcome, outcome);
        if (error is not null)
            activity.SetStatus(ActivityStatusCode.Error, error);
    }

    internal static string? TraceIdOf(string? traceParent)
        => ActivityContext.TryParse(traceParent, null, out var context)
            ? context.TraceId.ToHexString()
            : null;
}
