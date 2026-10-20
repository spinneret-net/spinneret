namespace Spinneret.Queue.Mssql;

/// <summary>
/// Where the queue's SQL Server storage lives: the connection, and the names of its tables.
/// </summary>
public sealed class MssqlStorageOptions
{
    public static readonly string SectionName = "Queue:Mssql";

    /// <summary>
    /// SQL Server connection string for the queue tables. The queue must live in the same database
    /// as the application's own tables — that is what makes an enqueue atomic with the business
    /// write it belongs to, and a handler's writes atomic with the delivery.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Name of an entry in the standard ConnectionStrings section, read when
    /// <see cref="ConnectionString"/> is not set directly.
    /// </summary>
    public string? ConnectionStringName { get; set; }

    /// <summary>Schema the queue tables live in.</summary>
    public string SchemaName { get; set; } = "dbo";

    /// <summary>Table holding pending queue messages — the outbox.</summary>
    public string QueueTableName { get; set; } = "SpinneretQueue";

    /// <summary>Table the storage's <see cref="Spinneret.Queue.IDeadLetterWriter"/> writes to.</summary>
    public string DeadLetterTableName { get; set; } = "SpinneretDeadLetters";

    /// <summary>
    /// Create the queue tables idempotently at startup. Disable when the host owns the schema
    /// through its own migrations; <see cref="MssqlQueueSchema.CreateScript"/> yields the DDL.
    /// </summary>
    public bool CreateSchema { get; set; } = true;
}
