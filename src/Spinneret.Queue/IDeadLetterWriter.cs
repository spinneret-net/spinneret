namespace Spinneret.Queue;

public interface IDeadLetterWriter
{
    Task WriteAsync(DeadLetterEntry entry, CancellationToken ct = default);
}

public sealed record DeadLetterEntry
{
    /// <summary>
    /// Used as the Firestore document ID — Cloud Tasks task ID for queue sources,
    /// scheduler job ID for scheduler sources. Prevents duplicate entries when the
    /// dead-letter write itself is retried.
    /// </summary>
    public required string IdempotencyKey { get; init; }
    public required DeadLetterSource Source { get; init; }
    public required string CommandTypeName { get; init; }

    /// <summary>
    /// Optional human-readable description carried on the queue envelope (e.g. which domain event and
    /// subscriber this delivery was for). Surfaced on the dead-letter page to identify the failed task
    /// beyond its command type. Null when the enqueuer supplied no description.
    /// </summary>
    public string? Description { get; init; }

    public required string PayloadJson { get; init; }
    public required string Error { get; init; }
    public required int Attempts { get; init; }
}

public enum DeadLetterSource { Queue, Scheduler }
