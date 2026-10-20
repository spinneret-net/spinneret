using Spinneret.Queue.Mssql;
using Spinneret.Scheduler.Sql;

namespace Spinneret.Scheduler.Mssql;

/// <summary>
/// DDL for the scheduled-jobs table. The startup initializer runs it when the queue storage's
/// <see cref="Spinneret.Queue.Sql.SqlStorageOptions.CreateSchema"/> is on; hosts that own their schema through
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
    public static string CreateScript(MssqlStorageOptions storageOptions, MssqlSchedulerOptions schedulerOptions)
    {
        ArgumentNullException.ThrowIfNull(storageOptions);
        ArgumentNullException.ThrowIfNull(schedulerOptions);

        return MssqlSchedulerDialect.Instance.CreateSchemaScript(
            new SqlSchedulerNames(storageOptions.SchemaName, schedulerOptions.TableName));
    }
}
