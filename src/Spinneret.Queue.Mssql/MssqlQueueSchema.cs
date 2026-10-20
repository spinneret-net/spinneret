using Spinneret.Queue.Sql;

namespace Spinneret.Queue.Mssql;

/// <summary>DDL for the queue tables, for hosts that turn off <see cref="SqlStorageOptions.CreateSchema"/>.</summary>
public static class MssqlQueueSchema
{
    /// <summary>Idempotent, concurrency-safe creation script for the queue and dead-letter tables.</summary>
    public static string CreateScript(MssqlStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return MssqlQueueDialect.Instance.CreateSchemaScript(SqlQueueRegistration.NamesOf(options));
    }
}
