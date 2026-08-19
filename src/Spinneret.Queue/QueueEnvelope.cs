namespace Spinneret.Queue;

/// <summary>
/// Wire format for an enqueued mediator request. The outer envelope is serialized with
/// default <see cref="System.Text.Json"/> options; <see cref="PayloadJson"/> is serialized
/// separately using the host's configured options so it round-trips NodaTime / Input /
/// ValueArray converters.
/// </summary>
public sealed record QueueEnvelope
{
    /// <summary>
    /// Envelope format version, for forward evolution of the wire shape. Envelopes serialized
    /// before this field existed deserialize as 0 and are treated as version 1.
    /// New envelope members must be optional (non-required, with defaults valid for in-flight
    /// messages) — a required member would break both out-of-tree transports at compile time
    /// and already-enqueued envelopes on deploy.
    /// </summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// The full CLR name of the request type. The dispatcher resolves this against the
    /// <see cref="QueueTypeRegistry"/> -not via <c>Type.GetType</c> -to prevent arbitrary
    /// type instantiation if the OIDC perimeter is ever bypassed.
    /// </summary>
    public required string RequestTypeName { get; init; }

    public required string PayloadJson { get; init; }

    public required DateTimeOffset EnqueuedAtUtc { get; init; }

    /// <summary>
    /// Failed executions accumulated in earlier task generations — the sole source of the
    /// <see cref="QueuePolicy.MaxAttempts"/> budget. Every app-level retry re-enqueues a fresh task
    /// (a failure increments this; a deferral carries it unchanged), so the transport's own retry
    /// counter — which also counts deliveries that never reached the app — never spends attempts.
    /// </summary>
    public int PriorFailures { get; init; }

    /// <summary>
    /// W3C traceparent of the operation that enqueued this request ("00-{trace}-{span}-{flags}"), or
    /// null when the enqueue had no W3C activity. Consumers restore it with
    /// <see cref="System.Diagnostics.ActivityContext.TryParse(string?, string?, out System.Diagnostics.ActivityContext)"/>.
    /// <para>
    /// Preserved verbatim across re-enqueues — a booked retry and a deferral both keep the original —
    /// so every attempt of a task, and the dead letter it may end as, share one trace id. That is what
    /// makes "here is the dead letter, show me the request that caused it" a single log query. Do not
    /// "improve" this to the current attempt's context: on a transport redelivery the ambient activity
    /// belongs to the transport, not the business operation, so rewriting can silently swap a good
    /// trace id for an unrelated one.
    /// </para>
    /// </summary>
    public string? TraceParent { get; init; }

    /// <summary>
    /// W3C tracestate at enqueue time, carried alongside <see cref="TraceParent"/> so vendor context
    /// survives the hop. Null unless something set one.
    /// </summary>
    public string? TraceState { get; init; }

    /// <summary>
    /// Optional human-readable description of the task (from <see cref="QueueOptions.Description"/>),
    /// carried for observability and surfaced on the dead-letter page. Never affects dispatch.
    /// </summary>
    public string? Description { get; init; }
}
