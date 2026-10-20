using Spinneret.Queue.Sql;

namespace Spinneret.Queue.Postgres;

/// <summary>DDL for the queue tables, for hosts that turn off <see cref="SqlStorageOptions.CreateSchema"/>.</summary>
public static class PostgresQueueSchema
{
    /// <summary>Idempotent, concurrency-safe creation script for the queue and dead-letter tables, applied atomically.</summary>
    public static string CreateScript(PostgresStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return PostgresQueueDialect.Instance.CreateSchemaScript(SqlQueueRegistration.NamesOf(options));
    }
}
