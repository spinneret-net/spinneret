using Spinneret.Queue.Sql;

namespace Spinneret.Queue.Mssql;

/// <summary>
/// DDL for the queue tables. The startup initializer runs it when
/// <see cref="MssqlStorageOptions.CreateSchema"/> is on; hosts that own their schema through
/// migrations run it themselves and turn that off.
/// </summary>
public static class MssqlQueueSchema
{
    /// <summary>
    /// Idempotent creation script for the queue and dead-letter tables. Safe to run concurrently:
    /// every host runs it at startup, and a fleet scaling from zero runs it from every replica at
    /// once.
    /// </summary>
    public static string CreateScript(MssqlStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return MssqlQueueDialect.Instance.CreateSchemaScript(NamesOf(options));
    }

    internal static SqlQueueNames NamesOf(MssqlStorageOptions options) =>
        new(options.SchemaName, options.QueueTableName, options.DeadLetterTableName);
}
