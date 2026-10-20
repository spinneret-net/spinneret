using Spinneret.Scheduler.Sql;

namespace Spinneret.Scheduler.Postgres;

/// <summary>
/// The PostgreSQL scheduler's table, bound from <c>Scheduler:Postgres</c>; connection and schema follow <c>Queue:Postgres</c>.
/// </summary>
public sealed class PostgresSchedulerOptions : SqlSchedulerOptions
{
    public static readonly string SectionName = "Scheduler:Postgres";

    public PostgresSchedulerOptions()
        : base(tableName: "spinneret_scheduled_jobs")
    {
    }
}
