using System.Data.Common;

namespace Spinneret.Queue.Sql;

/// <summary>
/// The resolved storage a dialect package registered: where to connect, how to speak to it, and
/// which tables to use. One singleton per host; every SQL queue service reads it rather than its
/// dialect package's options, which is what keeps this package free of any one provider.
/// </summary>
internal sealed class SqlQueueStorage : IAsyncDisposable
{
    private readonly bool _ownsDataSource;

    // ownsDataSource: whether the storage created the data source and so disposes it. A data
    // source the host supplied (and shares with its own data access) belongs to the host.
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

    /// <summary>Whether the startup initializer creates the tables; off when the host owns the schema.</summary>
    public bool CreateSchema { get; }

    public ValueTask DisposeAsync() => _ownsDataSource ? DataSource.DisposeAsync() : ValueTask.CompletedTask;
}
