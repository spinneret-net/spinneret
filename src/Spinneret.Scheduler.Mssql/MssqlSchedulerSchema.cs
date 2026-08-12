using Spinneret.Queue.Mssql;

namespace Spinneret.Scheduler.Mssql;

/// <summary>
/// DDL for the scheduled-jobs table. The startup initializer runs it when the queue's
/// <see cref="MssqlQueueOptions.CreateSchema"/> is on; hosts that own their schema through
/// migrations run it themselves and turn that off.
/// </summary>
public static class MssqlSchedulerSchema
{
    /// <summary>Idempotent creation script for the scheduled-jobs table.</summary>
    public static string CreateScript(MssqlQueueOptions queueOptions, MssqlSchedulerOptions schedulerOptions)
    {
        ArgumentNullException.ThrowIfNull(queueOptions);
        ArgumentNullException.ThrowIfNull(schedulerOptions);

        var table = Identifier.Qualify(queueOptions.SchemaName, schedulerOptions.TableName);

        return $"""
            IF OBJECT_ID(N'{table}', N'U') IS NULL
            BEGIN
                CREATE TABLE {table} (
                    JobKey NVARCHAR(200) NOT NULL CONSTRAINT {Identifier.Quote($"PK_{schedulerOptions.TableName}")} PRIMARY KEY,
                    RequestTypeName NVARCHAR(500) NOT NULL,
                    PayloadJson NVARCHAR(MAX) NOT NULL,
                    Status NVARCHAR(20) NOT NULL,
                    Schedule NVARCHAR(500) NULL,
                    ExecuteAt DATETIME2(3) NOT NULL,
                    NextExecuteAt DATETIME2(3) NOT NULL,
                    CreatedAt DATETIME2(3) NOT NULL,
                    LastRunAt DATETIME2(3) NULL
                );

                CREATE INDEX {Identifier.Quote($"IX_{schedulerOptions.TableName}_Status_NextExecuteAt")}
                    ON {table} (Status, NextExecuteAt);
            END;
            """;
    }
}
