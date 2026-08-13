namespace Spinneret.Queue;

/// <summary>
/// Persists tasks the queue has given up on. Implemented by transports or hosts (the MSSQL
/// transport ships one; GCP hosts must register their own). Additions to this interface ship
/// as default interface members so existing implementations keep compiling.
/// </summary>
public interface IDeadLetterWriter
{
    Task WriteAsync(DeadLetterEntry entry, CancellationToken ct = default);
}

/// <summary>
/// One dead-lettered task. Constructed by transports; new members must be optional
/// (non-required) so out-of-tree writers keep compiling.
/// </summary>
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

/// <summary>
/// Where a dead letter came from. Member names are persisted (e.g. as strings in the MSSQL
/// dead-letter table), so they are a data contract: never rename or renumber.
/// </summary>
public enum DeadLetterSource
{
    Queue = 0,
    Scheduler = 1,
}
