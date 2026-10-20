namespace Spinneret.Scheduler.Sql;

/// <summary>
/// The SQL scheduler's table; connection and schema follow the queue storage, which shares its database.
/// </summary>
public abstract class SqlSchedulerOptions
{
    /// <summary>Starts from a dialect's default table name.</summary>
    protected SqlSchedulerOptions(string tableName) => TableName = tableName;

    /// <summary>Table holding scheduled jobs.</summary>
    public string TableName { get; set; }
}
