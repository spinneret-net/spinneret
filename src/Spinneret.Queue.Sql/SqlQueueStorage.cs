using System.Data.Common;

namespace Spinneret.Queue.Sql;

internal sealed class SqlQueueStorage : IAsyncDisposable
{
    private readonly bool _ownsDataSource;

    // A host-supplied data source (ownsDataSource: false) is the host's to dispose.
    public SqlQueueStorage(
        DbDataSource dataSource, SqlQueueDialect dialect, SqlQueueNames names, bool createSchema, bool ownsDataSource = true)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(names);

        DataSource = dataSource;
        Dialect = dialect;
        Names = names;
        CreateSchema = createSchema;
        _ownsDataSource = ownsDataSource;
        Statements = dialect.CreateStatements(names);
    }

    public DbDataSource DataSource { get; }
    public SqlQueueDialect Dialect { get; }
    public SqlQueueNames Names { get; }
    public SqlQueueStatements Statements { get; }

    public bool CreateSchema { get; }

    public ValueTask DisposeAsync() => _ownsDataSource ? DataSource.DisposeAsync() : ValueTask.CompletedTask;
}
