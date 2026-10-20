using Spinneret.Queue.Sql;

namespace Spinneret.Queue.Mssql;

/// <summary>SQL Server queue storage settings, bound from <c>Queue:Mssql</c>.</summary>
public sealed class MssqlStorageOptions : SqlStorageOptions
{
    public static readonly string SectionName = "Queue:Mssql";

    public MssqlStorageOptions()
        : base(schemaName: "dbo", queueTableName: "SpinneretQueue", deadLetterTableName: "SpinneretDeadLetters")
    {
    }
}
