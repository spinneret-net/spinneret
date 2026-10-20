using Spinneret.Queue.Sql;

namespace Spinneret.Queue.Postgres;

/// <summary>PostgreSQL queue storage settings, bound from <c>Queue:Postgres</c>.</summary>
public sealed class PostgresStorageOptions : SqlStorageOptions
{
    public static readonly string SectionName = "Queue:Postgres";

    public PostgresStorageOptions()
        : base(schemaName: "public", queueTableName: "spinneret_queue", deadLetterTableName: "spinneret_dead_letters")
    {
    }
}
