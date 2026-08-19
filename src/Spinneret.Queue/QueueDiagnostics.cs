using System.Diagnostics;

namespace Spinneret.Queue;

/// <summary>
/// Identifiers for the queue's tracing instrumentation.
/// </summary>
public static class QueueDiagnostics
{
    /// <summary>
    /// Name of the <see cref="ActivitySource"/> carrying the queue's publish and process spans. Pass it
    /// to an <see cref="ActivityListener"/>, or to OpenTelemetry's <c>AddSource</c>, to record them.
    /// </summary>
    public const string ActivitySourceName = "Spinneret.Queue";
}

/// <summary>Tag keys the queue's spans carry. Declared once: they are what a host's dashboards query.</summary>
internal static class QueueTags
{
    internal const string System = "messaging.system";
    internal const string SystemName = "spinneret";
    internal const string Operation = "messaging.operation";
    internal const string Destination = "messaging.destination.name";

    /// <summary>The transport's own id for the message. Never the dedupe key — see <see cref="DedupeKey"/>.</summary>
    internal const string MessageId = "messaging.message.id";

    /// <summary>
    /// The caller's idempotency key, when one was supplied. Kept out of
    /// <see cref="MessageId"/> because that one means the id the transport assigned: Cloud Tasks
    /// happens to build its task name from the dedupe key, so the two coincide there, while the
    /// MSSQL transport's id is an identity column and does not.
    /// </summary>
    internal const string DedupeKey = "spinneret.queue.dedupe_key";

    internal const string RequestType = "spinneret.request.type";
    internal const string Attempt = "spinneret.queue.attempt";
    internal const string MaxAttempts = "spinneret.queue.max_attempts";
    internal const string Outcome = "spinneret.queue.outcome";
}

/// <summary>
/// Values of the <see cref="QueueTags.Outcome"/> tag — the vocabulary documented in docs/queue.md,
/// so a rename here is a breaking change to somebody's dashboard.
/// </summary>
internal static class QueueOutcome
{
    internal const string Ack = "ack";
    internal const string Retry = "retry";
    internal const string Defer = "defer";
    internal const string DeadLetter = "deadletter";
    internal const string Discard = "discard";
    internal const string TransportRetry = "transport-retry";
}

/// <summary>
/// Shared W3C trace-context plumbing for the queue and its transports.
/// </summary>
/// <remarks>
/// Capture reads <see cref="Activity.Current"/> — the publish span when something is listening, the
/// host's ambient span when nothing is. ASP.NET Core starts a request activity whether or not anything
/// listens, so propagation keeps working in a host that registers no <see cref="ActivityListener"/>,
/// which is precisely the host where every <c>StartActivity</c> call here returns null.
/// </remarks>
internal static class QueueTracing
{
    private static readonly string? Version = typeof(QueueTracing).Assembly.GetName().Version?.ToString();

    private static readonly ActivitySource Source = new(QueueDiagnostics.ActivitySourceName, Version);

    /// <summary>Starts the producer span for one enqueue. Every transport publishes through here.</summary>
    internal static Activity? StartProducer(string channel, QueueEnvelope envelope, string? dedupeKey)
    {
        // Tags go in at creation rather than after: a sampler only sees what the creation options carry.
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

    /// <summary>
    /// Stamps the ambient trace context onto an envelope about to go on the wire. Call it after
    /// <see cref="StartProducer"/> so the consumer's parent is the publish span.
    /// </summary>
    /// <remarks>
    /// Null-coalesces rather than assigns: a re-enqueued retry or deferral already carries the original
    /// producer's context, and every attempt of a task must stay in that one trace.
    /// </remarks>
    internal static QueueEnvelope StampTraceContext(QueueEnvelope envelope)
    {
        // Anything but W3C means a host took a legacy Request-Id header without forcing the W3C
        // format; the hierarchical id that produces is one ActivityContext.TryParse rejects, so it
        // must not go on the wire.
        var current = Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity ? activity : null;

        return envelope with
        {
            TraceParent = envelope.TraceParent ?? current?.Id,
            TraceState = envelope.TraceState ?? current?.TraceStateString,
        };
    }

    /// <summary>Starts the consumer span for one delivery, continuing the trace its producer recorded.</summary>
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

    /// <summary>
    /// The type's own name, without namespace or declaring type. Span names are read in a list,
    /// where the distinguishing end of a qualified name is the first thing a UI truncates; the
    /// qualified name stays on the <see cref="QueueTags.RequestType"/> tag.
    /// </summary>
    private static string ShortName(string requestTypeName)
    {
        // A closed generic's FullName carries assembly-qualified arguments in brackets, and those
        // contain dots of their own.
        var name = requestTypeName.IndexOf('[') is var generic && generic >= 0
            ? requestTypeName[..generic]
            : requestTypeName;

        var cut = name.LastIndexOfAny(['.', '+']);
        return cut >= 0 && cut < name.Length - 1 ? name[(cut + 1)..] : name;
    }

    /// <summary>
    /// The producer's context, or <c>default</c> to inherit whatever is ambient.
    /// </summary>
    /// <remarks>
    /// Ambient wins when the transport already carried the context in-band (a traceparent header on the
    /// delivery request): the server span is then the truer parent, and re-rooting on the producer would
    /// hide the hop that actually delivered the message.
    /// </remarks>
    private static ActivityContext ParentFor(QueueEnvelope envelope)
    {
        if (!ActivityContext.TryParse(envelope.TraceParent, envelope.TraceState, isRemote: true, out var producer))
            return default;

        return Activity.Current is { } current && current.TraceId == producer.TraceId ? default : producer;
    }

    /// <summary>Records how a delivery ended on its consumer span.</summary>
    internal static void SetOutcome(this Activity? activity, string outcome, string? error = null)
    {
        if (activity is null)
            return;

        activity.SetTag(QueueTags.Outcome, outcome);
        if (error is not null)
            activity.SetStatus(ActivityStatusCode.Error, error);
    }

    /// <summary>The 32-hex trace id inside a traceparent. Null when it is absent or malformed.</summary>
    internal static string? TraceIdOf(string? traceParent)
        => ActivityContext.TryParse(traceParent, null, out var context)
            ? context.TraceId.ToHexString()
            : null;
}
