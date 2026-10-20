using Spinneret.Queue.Postgres;
using Spinneret.Scheduler.Sql;

namespace Spinneret.Scheduler.Postgres;

/// <summary>
/// DDL for the scheduled-jobs table. The startup initializer runs it when the queue storage's
/// <see cref="Spinneret.Queue.Sql.SqlStorageOptions.CreateSchema"/> is on; hosts that own their
/// schema through migrations run it themselves and turn that off.
/// </summary>
/// <remarks>
/// The table holds only outstanding work — a job is deleted once it has run, been cancelled, or had
/// its failure recorded as a dead letter — so it stays proportional to what is scheduled rather than
/// to everything ever scheduled, and needs no retention policy.
/// </remarks>
public static class PostgresSchedulerSchema
{
    /// <summary>
    /// Idempotent creation script for the scheduled-jobs table. Safe to run concurrently, on the
    /// same terms as <see cref="PostgresQueueSchema.CreateScript"/>.
    /// </summary>
    public static string CreateScript(PostgresStorageOptions storageOptions, PostgresSchedulerOptions schedulerOptions)
    {
        ArgumentNullException.ThrowIfNull(storageOptions);
        ArgumentNullException.ThrowIfNull(schedulerOptions);

        return PostgresSchedulerDialect.Instance.CreateSchemaScript(
            new SqlSchedulerNames(storageOptions.SchemaName, schedulerOptions.TableName));
    }
}
