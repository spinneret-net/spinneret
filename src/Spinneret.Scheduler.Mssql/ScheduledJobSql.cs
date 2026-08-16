using Spinneret.Queue.Mssql;

namespace Spinneret.Scheduler.Mssql;

/// <summary>
/// One-shot job handles, mirroring the GCP scheduler's document ids. One-shot handles and
/// caller-chosen recurring keys share the JobKey namespace, so the prefix is what tells them apart
/// without a read — it keeps a cancel from destroying a recurring job's schedule.
/// </summary>
internal static class ScheduledJobHandle
{
    public const string OneShotPrefix = "oneshot-";

    public static string New() => $"{OneShotPrefix}{Guid.NewGuid():N}";

    public static bool IsOneShot(string handle) => handle.StartsWith(OneShotPrefix, StringComparison.Ordinal);
}

/// <summary>
/// The single source of truth for the scheduler's SQL — every statement the scheduler executes is
/// built here from the configured names, so the schema and the statements can never drift apart.
/// </summary>
/// <remarks>
/// There is no status column: a row's existence *is* its status. A job is deleted the moment it
/// stops being work to do — a one-shot that ran, one whose failure reached the dead-letter table, or
/// one that was cancelled — so every row is due or waiting to be. That keeps the table from growing
/// without bound and reduces every predicate below to a key and a due time.
/// </remarks>
internal sealed class ScheduledJobSql(MssqlQueueOptions queueOptions, MssqlSchedulerOptions schedulerOptions)
{
    public string Table { get; } = Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName);

    /// <summary>Locks a job row for the register-or-refresh upsert; HOLDLOCK covers the not-yet-existing key.</summary>
    public string SelectForRegister { get; } = $"""
        SELECT Schedule
        FROM {Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName)} WITH (UPDLOCK, HOLDLOCK)
        WHERE JobKey = @JobKey;
        """;

    public string Insert { get; } = $"""
        INSERT INTO {Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName)}
            (JobKey, RequestTypeName, PayloadJson, Schedule, NextExecuteAt, CreatedAt)
        VALUES (@JobKey, @RequestTypeName, @PayloadJson, @Schedule, @NextExecuteAt, @CreatedAt);
        """;

    /// <summary>Refreshes the definition of an existing pending job without touching its cadence.</summary>
    public string UpdateDefinition { get; } = $"""
        UPDATE {Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName)}
        SET RequestTypeName = @RequestTypeName, PayloadJson = @PayloadJson, Schedule = @Schedule
        WHERE JobKey = @JobKey;
        """;

    /// <summary>Refreshes the definition and re-arms the job, for a changed schedule.</summary>
    public string UpdateDefinitionAndRearm { get; } = $"""
        UPDATE {Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName)}
        SET RequestTypeName = @RequestTypeName, PayloadJson = @PayloadJson, Schedule = @Schedule,
            NextExecuteAt = @NextExecuteAt
        WHERE JobKey = @JobKey;
        """;

    /// <summary>
    /// Claims the next due job. UPDLOCK holds the row until the sweep's transaction commits —
    /// that lock is the lease — and READPAST lets a competing host's sweep skip straight to the
    /// next due job instead of blocking, so hosts sweep in parallel without double-dispatching.
    /// </summary>
    public string ClaimNextDue { get; } = $"""
        SELECT TOP(1) JobKey, RequestTypeName, PayloadJson, Schedule
        FROM {Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName)} WITH (UPDLOCK, READPAST, ROWLOCK)
        WHERE NextExecuteAt <= @Now
        ORDER BY NextExecuteAt;
        """;

    /// <summary>Books a claimed recurring job's next run.</summary>
    public string AdvanceRecurring { get; } = $"""
        UPDATE {Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName)}
        SET NextExecuteAt = @NextExecuteAt, LastRunAt = @Now
        WHERE JobKey = @JobKey;
        """;

    /// <summary>
    /// Retires a claimed one-shot job. Runs on the claim's transaction, so the row disappears in the
    /// same commit that makes the queue message durable — the message is the record from then on.
    /// </summary>
    public string CompleteOneShot { get; } = $"""
        DELETE FROM {Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName)}
        WHERE JobKey = @JobKey;
        """;

    /// <summary>
    /// Compensation after a failed dispatch, on a fresh transaction: applies only if the job is
    /// still due — a competing sweep that claimed it meanwhile either advanced it past now or
    /// deleted it outright, so this becomes a no-op (checked via rows affected).
    /// </summary>
    public string CompensateRecurring { get; } = $"""
        UPDATE {Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName)}
        SET NextExecuteAt = @NextExecuteAt, LastRunAt = @Now
        WHERE JobKey = @JobKey AND NextExecuteAt <= @Now;
        """;

    /// <summary>
    /// Retires a one-shot job whose dispatch failed. The dead-letter write shares this transaction,
    /// so the row is only ever removed together with the record that replaces it.
    /// </summary>
    public string CompensateOneShot { get; } = $"""
        DELETE FROM {Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName)}
        WHERE JobKey = @JobKey AND NextExecuteAt <= @Now;
        """;

    /// <summary>
    /// Removes a retired recurring job. The Schedule NOT NULL guard is what keeps a one-shot job
    /// safe: one-shot handles live in the same JobKey namespace and are the rows with no schedule.
    /// </summary>
    public string DeleteRecurring { get; } = $"""
        DELETE FROM {Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName)}
        WHERE JobKey = @JobKey AND Schedule IS NOT NULL;
        """;

    /// <summary>
    /// Cancels a one-shot job by removing it. The Schedule IS NULL guard mirrors
    /// <see cref="DeleteRecurring"/>'s: it is a backstop behind the handle-prefix check, so a cancel
    /// can never delete a recurring job's schedule. A job that already ran is simply gone, which
    /// makes this a no-op — the same outcome the old pending-status guard produced.
    /// </summary>
    public string Cancel { get; } = $"""
        DELETE FROM {Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName)}
        WHERE JobKey = @JobKey AND Schedule IS NULL;
        """;
}
