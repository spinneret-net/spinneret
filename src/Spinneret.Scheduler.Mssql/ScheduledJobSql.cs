using Spinneret.Queue.Mssql;

namespace Spinneret.Scheduler.Mssql;

/// <summary>Status values for scheduled-job rows, mirroring the GCP scheduler's document states.</summary>
internal static class ScheduledJobStatus
{
    public const string Pending = "pending";
    public const string Cancelled = "cancelled";
    public const string Enqueued = "enqueued";
    public const string Failed = "failed";
}

/// <summary>
/// The single source of truth for the scheduler's SQL — every statement the scheduler executes is
/// built here from the configured names, so the schema and the statements can never drift apart.
/// </summary>
internal sealed class ScheduledJobSql(MssqlQueueOptions queueOptions, MssqlSchedulerOptions schedulerOptions)
{
    public string Table { get; } = Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName);

    /// <summary>Locks a job row for the register-or-refresh upsert; HOLDLOCK covers the not-yet-existing key.</summary>
    public string SelectForRegister { get; } = $"""
        SELECT Status, Schedule
        FROM {Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName)} WITH (UPDLOCK, HOLDLOCK)
        WHERE JobKey = @JobKey;
        """;

    public string Insert { get; } = $"""
        INSERT INTO {Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName)}
            (JobKey, RequestTypeName, PayloadJson, Status, Schedule, ExecuteAt, NextExecuteAt, CreatedAt)
        VALUES (@JobKey, @RequestTypeName, @PayloadJson, @Status, @Schedule, @ExecuteAt, @NextExecuteAt, @CreatedAt);
        """;

    /// <summary>Refreshes the definition of an existing pending job without touching its cadence.</summary>
    public string UpdateDefinition { get; } = $"""
        UPDATE {Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName)}
        SET RequestTypeName = @RequestTypeName, PayloadJson = @PayloadJson, Schedule = @Schedule
        WHERE JobKey = @JobKey;
        """;

    /// <summary>Refreshes the definition and re-arms the job (terminal status or changed schedule).</summary>
    public string UpdateDefinitionAndRearm { get; } = $"""
        UPDATE {Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName)}
        SET RequestTypeName = @RequestTypeName, PayloadJson = @PayloadJson, Schedule = @Schedule,
            Status = @Status, ExecuteAt = @NextExecuteAt, NextExecuteAt = @NextExecuteAt
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
        WHERE Status = @Status AND NextExecuteAt <= @Now
        ORDER BY NextExecuteAt;
        """;

    /// <summary>Books a claimed recurring job's next run.</summary>
    public string AdvanceRecurring { get; } = $"""
        UPDATE {Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName)}
        SET NextExecuteAt = @NextExecuteAt, LastRunAt = @Now
        WHERE JobKey = @JobKey;
        """;

    /// <summary>Marks a claimed one-shot job terminal.</summary>
    public string CompleteOneShot { get; } = $"""
        UPDATE {Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName)}
        SET Status = @Status, LastRunAt = @Now
        WHERE JobKey = @JobKey;
        """;

    /// <summary>
    /// Compensation after a failed dispatch, on a fresh transaction: applies only if the job is
    /// still pending and due — a competing sweep that claimed it meanwhile wins, and this becomes
    /// a no-op (checked via rows affected).
    /// </summary>
    public string CompensateRecurring { get; } = $"""
        UPDATE {Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName)}
        SET NextExecuteAt = @NextExecuteAt, LastRunAt = @Now
        WHERE JobKey = @JobKey AND Status = @Status AND NextExecuteAt <= @Now;
        """;

    public string CompensateOneShot { get; } = $"""
        UPDATE {Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName)}
        SET Status = @FailedStatus, LastRunAt = @Now
        WHERE JobKey = @JobKey AND Status = @PendingStatus AND NextExecuteAt <= @Now;
        """;

    public string Cancel { get; } = $"""
        UPDATE {Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName)}
        SET Status = @CancelledStatus
        WHERE JobKey = @JobKey AND Status = @PendingStatus;
        """;
}
