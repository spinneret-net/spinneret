using Spinneret.Queue.Postgres;
using Spinneret.Scheduler.Sql;

namespace Spinneret.Scheduler.Postgres;

/// <summary>
/// DDL for the scheduled-jobs table, for hosts that turn off <see cref="Spinneret.Queue.Sql.SqlStorageOptions.CreateSchema"/> and run migrations.
/// </summary>
public static class PostgresSchedulerSchema
{
    /// <summary>
    /// Idempotent creation script for the scheduled-jobs table; safe to run concurrently.
    /// </summary>
    public static string CreateScript(PostgresStorageOptions storageOptions, PostgresSchedulerOptions schedulerOptions)
    {
        ArgumentNullException.ThrowIfNull(storageOptions);
        ArgumentNullException.ThrowIfNull(schedulerOptions);

        return PostgresSchedulerDialect.Instance.CreateSchemaScript(
            new SqlSchedulerNames(storageOptions.SchemaName, schedulerOptions.TableName));
    }
}
