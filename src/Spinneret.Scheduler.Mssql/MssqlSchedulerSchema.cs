using Spinneret.Queue.Mssql;
using Spinneret.Scheduler.Sql;

namespace Spinneret.Scheduler.Mssql;

/// <summary>
/// DDL for the scheduled-jobs table, for hosts that turn off <see cref="Spinneret.Queue.Sql.SqlStorageOptions.CreateSchema"/> and run migrations.
/// </summary>
public static class MssqlSchedulerSchema
{
    /// <summary>
    /// Idempotent creation script for the scheduled-jobs table; safe to run concurrently.
    /// </summary>
    public static string CreateScript(MssqlStorageOptions storageOptions, MssqlSchedulerOptions schedulerOptions)
    {
        ArgumentNullException.ThrowIfNull(storageOptions);
        ArgumentNullException.ThrowIfNull(schedulerOptions);

        return MssqlSchedulerDialect.Instance.CreateSchemaScript(
            new SqlSchedulerNames(storageOptions.SchemaName, schedulerOptions.TableName));
    }
}
