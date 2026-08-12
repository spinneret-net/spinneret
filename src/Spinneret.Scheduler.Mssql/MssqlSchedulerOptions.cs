namespace Spinneret.Scheduler.Mssql;

public sealed class MssqlSchedulerOptions
{
    public const string SectionName = "Scheduler:Mssql";

    /// <summary>
    /// Table holding scheduled jobs. Connection, schema name and schema creation follow the queue's
    /// <c>Queue:Mssql</c> options: the scheduler dispatches onto the queue inside one SQL
    /// transaction, which requires living in the same database.
    /// </summary>
    public string TableName { get; set; } = "SpinneretScheduledJobs";

    /// <summary>How often each host sweeps for due jobs. Sweeps race safely across hosts.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(15);
}
