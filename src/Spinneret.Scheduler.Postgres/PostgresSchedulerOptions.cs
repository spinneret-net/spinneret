using Spinneret.Scheduler.Sql;

namespace Spinneret.Scheduler.Postgres;

/// <summary>
/// The PostgreSQL scheduler's table, bound from the <c>Scheduler:Postgres</c> section. Connection,
/// schema name and schema creation follow the queue's <c>Queue:Postgres</c> options.
/// </summary>
public sealed class PostgresSchedulerOptions : SqlSchedulerOptions
{
    public static readonly string SectionName = "Scheduler:Postgres";

    public PostgresSchedulerOptions()
        : base(tableName: "spinneret_scheduled_jobs")
    {
    }
}
