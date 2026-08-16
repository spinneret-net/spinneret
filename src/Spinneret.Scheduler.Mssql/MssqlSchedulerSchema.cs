using Spinneret.Queue.Mssql;

namespace Spinneret.Scheduler.Mssql;

/// <summary>
/// DDL for the scheduled-jobs table. The startup initializer runs it when the queue's
/// <see cref="MssqlQueueOptions.CreateSchema"/> is on; hosts that own their schema through
/// migrations run it themselves and turn that off.
/// </summary>
/// <remarks>
/// The table holds only outstanding work — a job is deleted once it has run, been cancelled, or had
/// its failure recorded as a dead letter — so it stays proportional to what is scheduled rather than
/// to everything ever scheduled, and needs no retention policy.
/// </remarks>
public static class MssqlSchedulerSchema
{
    /// <summary>
    /// Idempotent creation script for the scheduled-jobs table. Safe to run concurrently, on the
    /// same terms as <see cref="MssqlQueueSchema.CreateScript"/> — see there for why the lock and
    /// the separately guarded index are needed.
    /// </summary>
    public static string CreateScript(MssqlQueueOptions queueOptions, MssqlSchedulerOptions schedulerOptions)
    {
        ArgumentNullException.ThrowIfNull(queueOptions);
        ArgumentNullException.ThrowIfNull(schedulerOptions);

        var table = Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName);
        var dueIndex = $"IX_{schedulerOptions.TableName}_NextExecuteAt";

        return $"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;

            DECLARE @lockResult INT;
            EXEC @lockResult = sp_getapplock
                @Resource = N'Spinneret:SchedulerSchema:{table}',
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 30000;

            IF @lockResult < 0
            BEGIN
                ROLLBACK TRANSACTION;
                THROW 50000, 'Timed out acquiring the Spinneret scheduler schema lock.', 1;
            END;

            IF OBJECT_ID(N'{table}', N'U') IS NULL
            BEGIN
                CREATE TABLE {table} (
                    JobKey NVARCHAR(200) NOT NULL CONSTRAINT {Identifier.Quote($"PK_{schedulerOptions.TableName}")} PRIMARY KEY,
                    RequestTypeName NVARCHAR(500) NOT NULL,
                    PayloadJson NVARCHAR(MAX) NOT NULL,
                    Schedule NVARCHAR(500) NULL,
                    NextExecuteAt DATETIME2(3) NOT NULL,
                    CreatedAt DATETIME2(3) NOT NULL,
                    LastRunAt DATETIME2(3) NULL
                );
            END;

            -- Guarded on its own existence, not the table's: the sweep selects on NextExecuteAt, so
            -- a table left without this index scans every job on every tick, forever.
            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'{table}', N'U')
                  AND name = N'{dueIndex}')
            BEGIN
                CREATE INDEX {Identifier.Quote(dueIndex)}
                    ON {table} (NextExecuteAt);
            END;

            COMMIT TRANSACTION;
            """;
    }
}
