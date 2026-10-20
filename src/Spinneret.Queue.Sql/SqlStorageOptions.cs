namespace Spinneret.Queue.Sql;

/// <summary>
/// Where a SQL queue storage lives: the connection, and the names of its tables. Each dialect
/// package derives its own options type with its configuration section and its database's naming
/// defaults; the settings themselves mean the same thing on every database.
/// </summary>
public abstract class SqlStorageOptions
{
    /// <summary>Starts from a dialect's default names.</summary>
    protected SqlStorageOptions(string schemaName, string queueTableName, string deadLetterTableName)
    {
        SchemaName = schemaName;
        QueueTableName = queueTableName;
        DeadLetterTableName = deadLetterTableName;
    }

    /// <summary>
    /// Connection string for the queue tables. The queue must live in the same database as the
    /// application's own tables — that is what makes an enqueue atomic with the business write it
    /// belongs to, and a handler's writes atomic with the delivery. Not needed when the storage is
    /// given a data source of the host's.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Name of an entry in the standard ConnectionStrings section, read when
    /// <see cref="ConnectionString"/> is not set directly.
    /// </summary>
    public string? ConnectionStringName { get; set; }

    /// <summary>Schema the queue tables live in.</summary>
    public string SchemaName { get; set; }

    /// <summary>Table holding pending queue messages — the outbox.</summary>
    public string QueueTableName { get; set; }

    /// <summary>Table the storage's <see cref="IDeadLetterWriter"/> writes to.</summary>
    public string DeadLetterTableName { get; set; }

    /// <summary>
    /// Create the queue tables idempotently at startup. Disable when the host owns the schema
    /// through its own migrations; the dialect package's schema class yields the DDL.
    /// </summary>
    public bool CreateSchema { get; set; } = true;
}
