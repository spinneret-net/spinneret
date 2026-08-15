namespace Spinneret.Scheduler.Mssql;

public sealed class MssqlSchedulerOptions
{
    public static readonly string SectionName = "Scheduler:Mssql";

    /// <summary>
    /// Table holding scheduled jobs. Connection, schema name and schema creation follow the queue's
    /// <c>Queue:Mssql</c> options: the scheduler dispatches onto the queue inside one SQL
    /// transaction, which requires living in the same database.
    /// </summary>
    public string TableName { get; set; } = "SpinneretScheduledJobs";

    // Sweep cadence lives on the trigger, not the store: see Scheduler:Sweeper:SweepInterval on
    // SchedulerOptions, which applies whichever scheduler provider a host registered.
}
