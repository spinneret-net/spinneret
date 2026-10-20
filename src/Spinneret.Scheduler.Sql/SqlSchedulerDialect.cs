using Spinneret.Queue.Sql;

namespace Spinneret.Scheduler.Sql;

// Timestamps go through the queue's dialect: the scheduler shares the queue's database so a
// due job's booking and its enqueue commit together.
internal abstract class SqlSchedulerDialect
{
    // A mismatch with the queue's dialect fails startup: they could not share a transaction.
    public abstract SqlQueueDialect QueueDialect { get; }

    // Index and constraint names derived from the table, checked against the identifier limit.
    public abstract IEnumerable<string> DerivedIdentifiers(string table);

    public abstract SqlSchedulerStatements CreateStatements(SqlSchedulerNames names);

    // Must be idempotent and safe to run concurrently from every replica.
    public abstract string CreateSchemaScript(SqlSchedulerNames names);
}

internal sealed record SqlSchedulerNames(string Schema, string Table);

// No status column: a row is deleted once it stops being work, so every row is due or waiting.
internal sealed record SqlSchedulerStatements
{
    // Concurrency-safe upsert; resets the next run only for a new job or a changed schedule.
    public required string UpsertRecurring { get; init; }

    public required string InsertOneShot { get; init; }

    // Locks the next due row until the transaction ends, skipping rows other sweeps hold.
    public required string ClaimNextDue { get; init; }

    public required string AdvanceRecurring { get; init; }

    public required string CompleteOneShot { get; init; }

    // After a failed dispatch: applies only if still due; the affected row count says whether it did.
    public required string CompensateRecurring { get; init; }

    public required string CompensateOneShot { get; init; }

    // Never touches a one-shot under the same key.
    public required string DeleteRecurring { get; init; }

    // Never touches a recurring job under the same key.
    public required string Cancel { get; init; }
}
