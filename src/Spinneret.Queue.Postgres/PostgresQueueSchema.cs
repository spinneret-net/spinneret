using Spinneret.Queue.Sql;

namespace Spinneret.Queue.Postgres;

/// <summary>
/// DDL for the queue tables. The startup initializer runs it when
/// <see cref="SqlStorageOptions.CreateSchema"/> is on; hosts that own their schema through
/// migrations run it themselves and turn that off.
/// </summary>
public static class PostgresQueueSchema
{
    /// <summary>
    /// Idempotent creation script for the queue and dead-letter tables. Safe to run concurrently:
    /// every host runs it at startup, and a fleet scaling from zero runs it from every replica at
    /// once. One <c>DO</c> block, so it applies atomically as a single statement.
    /// </summary>
    public static string CreateScript(PostgresStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return PostgresQueueDialect.Instance.CreateSchemaScript(SqlQueueRegistration.NamesOf(options));
    }
}
