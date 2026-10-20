using Spinneret.Scheduler.Sql;

namespace Spinneret.Scheduler.Mssql;

/// <summary>
/// The SQL Server scheduler's table, bound from <c>Scheduler:Mssql</c>; connection and schema follow <c>Queue:Mssql</c>.
/// </summary>
public sealed class MssqlSchedulerOptions : SqlSchedulerOptions
{
    public static readonly string SectionName = "Scheduler:Mssql";

    public MssqlSchedulerOptions()
        : base(tableName: "SpinneretScheduledJobs")
    {
    }
}
