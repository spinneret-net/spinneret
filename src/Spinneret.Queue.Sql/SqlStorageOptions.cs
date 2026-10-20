namespace Spinneret.Queue.Sql;

/// <summary>The connection and table names of a SQL queue storage.</summary>
public abstract class SqlStorageOptions
{
    /// <summary>Starts from a dialect's default names.</summary>
    protected SqlStorageOptions(string schemaName, string queueTableName, string deadLetterTableName)
    {
        SchemaName = schemaName;
        QueueTableName = queueTableName;
        DeadLetterTableName = deadLetterTableName;
    }

    /// <summary>Connection string for the queue tables; not needed when the host supplies a data source.</summary>
    /// <remarks>Use the application's own database, so enqueues are atomic with its writes.</remarks>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>A ConnectionStrings entry, used when <see cref="ConnectionString"/> is not set.</summary>
    public string? ConnectionStringName { get; set; }

    /// <summary>Schema the queue tables live in.</summary>
    public string SchemaName { get; set; }

    /// <summary>Table holding pending messages (the outbox).</summary>
    public string QueueTableName { get; set; }

    /// <summary>Table holding dead letters.</summary>
    public string DeadLetterTableName { get; set; }

    /// <summary>Creates the tables at startup; disable when your migrations own the schema.</summary>
    public bool CreateSchema { get; set; } = true;
}
