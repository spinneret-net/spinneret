namespace Spinneret.Queue;

/// <summary>Wire format for an enqueued mediator request.</summary>
/// <remarks>
/// <see cref="PayloadJson"/> uses the host's serializer; the envelope itself uses default options.
/// New members must be optional, or in-flight envelopes break on deploy.
/// </remarks>
public sealed record QueueEnvelope
{
    /// <summary>Envelope format version; envelopes without it deserialize as 0 and are treated as 1.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// The request type's full CLR name, resolved only against <see cref="QueueTypeRegistry"/> to prevent arbitrary type loading.
    /// </summary>
    public required string RequestTypeName { get; init; }

    public required string PayloadJson { get; init; }

    public required DateTimeOffset EnqueuedAtUtc { get; init; }

    /// <summary>
    /// Failures booked in earlier task generations; the sole count against <see cref="QueuePolicy.MaxAttempts"/>.
    /// </summary>
    public int PriorFailures { get; init; }

    /// <summary>W3C traceparent of the enqueuing operation, or null when there was none.</summary>
    /// <remarks>
    /// Kept verbatim across re-enqueues so every attempt and the dead letter share one trace id;
    /// don't replace it with the current attempt's context.
    /// </remarks>
    public string? TraceParent { get; init; }

    /// <summary>W3C tracestate at enqueue time, carried alongside <see cref="TraceParent"/>.</summary>
    public string? TraceState { get; init; }

    /// <summary>Optional description from <see cref="QueueOptions.Description"/>; never affects dispatch.</summary>
    public string? Description { get; init; }
}
