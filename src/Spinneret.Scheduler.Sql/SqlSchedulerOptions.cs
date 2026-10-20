namespace Spinneret.Scheduler.Sql;

/// <summary>
/// The SQL scheduler's own setting: its table. Connection, schema name and schema creation follow
/// the queue storage's options — the scheduler dispatches onto the queue inside one SQL
/// transaction, which requires living in the same database. Each dialect package derives its own
/// options type with its configuration section and its database's naming default.
/// </summary>
public abstract class SqlSchedulerOptions
{
    /// <summary>Starts from a dialect's default table name.</summary>
    protected SqlSchedulerOptions(string tableName) => TableName = tableName;

    /// <summary>Table holding scheduled jobs.</summary>
    public string TableName { get; set; }

    // Sweep cadence lives on the trigger, not the store: see Scheduler:Sweeper:SweepInterval on
    // SchedulerOptions, which applies whichever scheduler provider a host registered.
}
