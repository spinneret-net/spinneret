using Spinneret.Scheduler.Sql;

namespace Spinneret.Scheduler.Mssql;

/// <summary>
/// The SQL Server scheduler's table, bound from the <c>Scheduler:Mssql</c> section. Connection,
/// schema name and schema creation follow the queue's <c>Queue:Mssql</c> options.
/// </summary>
public sealed class MssqlSchedulerOptions : SqlSchedulerOptions
{
    public static readonly string SectionName = "Scheduler:Mssql";

    public MssqlSchedulerOptions()
        : base(tableName: "SpinneretScheduledJobs")
    {
    }
}
