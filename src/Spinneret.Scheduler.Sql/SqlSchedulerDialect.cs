namespace Spinneret.Scheduler.Sql;

/// <summary>
/// Everything about the scheduled-jobs table that differs between databases. Timestamps are bound
/// and read through the queue storage's dialect, which the scheduler shares: the scheduler lives in
/// the queue's database so that a due job's booking and its enqueue commit together.
/// </summary>
/// <remarks>Internal and visible to the in-tree dialect packages only, like the queue's dialect.</remarks>
internal abstract class SqlSchedulerDialect
{
    /// <summary>
    /// The <c>SqlQueueDialect.Name</c> this dialect pairs with. A scheduler on one database feeding a
    /// queue on another could not share its transaction, so a mismatch fails the host at startup.
    /// </summary>
    public abstract string QueueDialectName { get; }

    /// <summary>The statements for the configured names, built once at startup.</summary>
    public abstract SqlSchedulerStatements CreateStatements(SqlSchedulerNames names);

    /// <summary>
    /// Idempotent creation script for the scheduled-jobs table, safe to run concurrently from every
    /// replica of a fleet at once.
    /// </summary>
    public abstract string CreateSchemaScript(SqlSchedulerNames names);
}

/// <summary>The configured names, already validated: the queue's schema and the jobs table.</summary>
internal sealed record SqlSchedulerNames(string Schema, string Table);

/// <summary>
/// The statements the SQL scheduler executes, described by parameters and result shape.
/// </summary>
/// <remarks>
/// There is no status column: a row's existence *is* its status. A job is deleted the moment it
/// stops being work to do — a one-shot that ran, one whose failure reached the dead-letter table, or
/// one that was cancelled — so every row is due or waiting to be. That keeps the table from growing
/// without bound and reduces every predicate to a key and a due time.
/// </remarks>
internal sealed record SqlSchedulerStatements
{
    /// <summary>
    /// Registers a recurring job, or refreshes the definition of the one under the same key — in
    /// one statement, safe against concurrent registrations of the same key. The next run is set to
    /// <c>@NextExecuteAt</c> for a new job and for one whose schedule text changed (compared
    /// ordinally); an unchanged schedule keeps its cadence. Parameters: <c>@JobKey</c>,
    /// <c>@RequestTypeName</c>, <c>@PayloadJson</c>, <c>@Schedule</c>, <c>@NextExecuteAt</c> and
    /// <c>@CreatedAt</c> (timestamps).
    /// </summary>
    public required string UpsertRecurring { get; init; }

    /// <summary>
    /// Inserts a one-shot job (no schedule). Parameters: <c>@JobKey</c>, <c>@RequestTypeName</c>,
    /// <c>@PayloadJson</c>, <c>@NextExecuteAt</c> and <c>@CreatedAt</c> (timestamps).
    /// </summary>
    public required string InsertOneShot { get; init; }

    /// <summary>
    /// Claims the next due job (<c>@Now</c>) under a lock held until the transaction ends, skipping
    /// rows other sweeps hold. Returns zero or one row of (JobKey, RequestTypeName, PayloadJson,
    /// Schedule nullable).
    /// </summary>
    public required string ClaimNextDue { get; init; }

    /// <summary>Books a claimed recurring job's next run. Parameters: <c>@JobKey</c>, <c>@Now</c>, <c>@NextExecuteAt</c>.</summary>
    public required string AdvanceRecurring { get; init; }

    /// <summary>Retires a claimed one-shot job. Parameters: <c>@JobKey</c>.</summary>
    public required string CompleteOneShot { get; init; }

    /// <summary>
    /// After a failed dispatch, on a fresh transaction: advances a recurring job only if it is still
    /// due (<c>@Now</c>) — a competing sweep may have handled it meanwhile. The affected row count
    /// tells whether it applied. Parameters: <c>@JobKey</c>, <c>@Now</c>, <c>@NextExecuteAt</c>.
    /// </summary>
    public required string CompensateRecurring { get; init; }

    /// <summary>As <see cref="CompensateRecurring"/>, deleting a still-due one-shot job. Parameters: <c>@JobKey</c>, <c>@Now</c>.</summary>
    public required string CompensateOneShot { get; init; }

    /// <summary>Removes a recurring job — never a one-shot under the same key. Parameters: <c>@JobKey</c>.</summary>
    public required string DeleteRecurring { get; init; }

    /// <summary>Cancels a one-shot job — never a recurring one under the same key. Parameters: <c>@JobKey</c>.</summary>
    public required string Cancel { get; init; }
}
