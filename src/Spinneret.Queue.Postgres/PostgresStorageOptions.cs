using Spinneret.Queue.Sql;

namespace Spinneret.Queue.Postgres;

/// <summary>
/// Where the queue's PostgreSQL storage lives: the connection, and the names of its tables. Bound
/// from the <c>Queue:Postgres</c> section. The default names are lower-case snake_case, the
/// spelling Postgres folds unquoted identifiers to, so the tables can be queried without quoting.
/// </summary>
public sealed class PostgresStorageOptions : SqlStorageOptions
{
    public static readonly string SectionName = "Queue:Postgres";

    public PostgresStorageOptions()
        : base(schemaName: "public", queueTableName: "spinneret_queue", deadLetterTableName: "spinneret_dead_letters")
    {
    }
}
