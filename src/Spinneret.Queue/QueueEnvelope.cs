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

    public string? TraceId { get; init; }

    /// <summary>
    /// Optional human-readable description of the task (from <see cref="QueueOptions.Description"/>),
    /// carried for observability and surfaced on the dead-letter page. Never affects dispatch.
    /// </summary>
    public string? Description { get; init; }
}
