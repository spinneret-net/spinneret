using Spinneret.Queue.Sql;

namespace Spinneret.Queue.Mssql;

/// <summary>
/// Where the queue's SQL Server storage lives: the connection, and the names of its tables. Bound
/// from the <c>Queue:Mssql</c> section.
/// </summary>
public sealed class MssqlStorageOptions : SqlStorageOptions
{
    public static readonly string SectionName = "Queue:Mssql";

    public MssqlStorageOptions()
        : base(schemaName: "dbo", queueTableName: "SpinneretQueue", deadLetterTableName: "SpinneretDeadLetters")
    {
    }
}
