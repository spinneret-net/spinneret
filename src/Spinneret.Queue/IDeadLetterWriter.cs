using System.Diagnostics;

namespace Spinneret.Queue;

/// <summary>Persists tasks the queue has given up on.</summary>
public interface IDeadLetterWriter
{
    Task WriteAsync(DeadLetterEntry entry, CancellationToken ct = default);
}

/// <summary>One dead-lettered task.</summary>
public sealed record DeadLetterEntry
{
    /// <summary>
    /// The store key (task id or scheduler job id), so a retried write does not duplicate the entry.
    /// </summary>
    public required string IdempotencyKey { get; init; }
    public required DeadLetterSource Source { get; init; }
    public required string CommandTypeName { get; init; }

    /// <summary>Optional human-readable description carried on the queue envelope.</summary>
    public string? Description { get; init; }

    public required string PayloadJson { get; init; }
    public required string Error { get; init; }
    public required int Attempts { get; init; }

    /// <summary>32-hex W3C trace id of the failed execution; defaults to the ambient trace.</summary>
    public string? TraceId { get; init; } = Activity.Current?.TraceId.ToHexString();
}

/// <summary>Where a dead letter came from. Member names are persisted: never rename them.</summary>
public enum DeadLetterSource
{
    Queue = 0,
    Scheduler = 1,
}
