using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Spinneret.Queue;
using Spinneret.Queue.Sql;
using Spinneret.Scheduler;
using Spinneret.Scheduler.Sql;

namespace Spinneret.Sql.Conformance;

/// <summary>
/// Everything a conformance suite needs to know about the database under test: how to register its
/// storage, how to reach it, and the few spellings — quoting, the clock, catalog lookups, the names
/// the DDL derives — that differ between dialects. The suites themselves are written once against
/// this, so a scenario proven on one database is proven on every database.
/// </summary>
/// <remarks>
/// Raw SQL in the suites quotes table names through <see cref="Quote"/> and leaves column names
/// unquoted. That reads the same on every dialect: SQL Server ignores case, and Postgres folds the
/// unquoted column names of both the DDL and the query to lower case.
/// </remarks>
public abstract class SqlTestDialect(ISqlDatabaseFixture? database)
{
    /// <summary>The live database's connection string; only valid in suites that have a fixture.</summary>
    public string ConnectionString =>
        database?.ConnectionString
        ?? throw new InvalidOperationException("This suite runs without a database.");

    /// <summary>The queue dialect's name, as the storage reports it.</summary>
    public abstract string Name { get; }

    /// <summary>The storage options section, e.g. <c>Queue:Mssql</c>.</summary>
    public abstract string StorageSection { get; }

    /// <summary>The scheduler options section, e.g. <c>Scheduler:Mssql</c>.</summary>
    public abstract string SchedulerSection { get; }

    /// <summary>The <c>Use*Storage</c> method's name, as error messages mention it.</summary>
    public abstract string UseStorageName { get; }

    /// <summary>A syntactically valid connection string for suites that never connect.</summary>
    public abstract string SampleConnectionString { get; }

    public abstract (string Schema, string QueueTable, string DeadLetterTable) DefaultStorageNames { get; }

    public abstract string DefaultSchedulerTable { get; }

    /// <summary>The longest identifier the database accepts.</summary>
    public abstract int MaxIdentifierLength { get; }

    // ----------------------------------------------------------------------- registration ---

    public abstract QueueBuilder UseStorage(QueueBuilder queue, IConfiguration configuration);

    public abstract QueueBuilder UseStorage(
        QueueBuilder queue, Func<IServiceProvider, DbDataSource> dataSource, IConfiguration configuration);

    public abstract SchedulerBuilder UseScheduler(SchedulerBuilder scheduler, IConfiguration configuration);

    public abstract SqlStorageOptions StorageOptions(IServiceProvider services);

    public abstract SqlSchedulerOptions SchedulerOptions(IServiceProvider services);

    /// <summary>Adjusts the storage options after registration, as a host's own Configure call would.</summary>
    public abstract void ConfigureStorage(IServiceCollection services, Action<SqlStorageOptions> configure);

    /// <summary>A data source over <see cref="ConnectionString"/>, owned by the caller.</summary>
    public DbDataSource CreateDataSource() => CreateDataSourceFor(ConnectionString);

    /// <summary>A data source over <paramref name="connectionString"/>, owned by the caller.</summary>
    public abstract DbDataSource CreateDataSourceFor(string connectionString);

    // ------------------------------------------------------------------------- spellings ---

    public abstract string Quote(string identifier);

    /// <summary>The current UTC instant, as a SQL expression.</summary>
    public abstract string Now { get; }

    /// <summary>The current UTC instant plus <paramref name="minutes"/>, as a SQL expression.</summary>
    public abstract string MinutesFromNow(int minutes);

    /// <summary>A short text column type for side-effect tables.</summary>
    public abstract string ShortText { get; }

    /// <summary>Binds an exact UTC instant to a timestamp column.</summary>
    public abstract void AddTimestamp(DbCommand command, string name, DateTimeOffset value);

    // --------------------------------------------------------------- derived and catalog ---

    public abstract string DedupeIndex(string queueTable);

    public abstract string ChannelIndex(string queueTable);

    public abstract string DeadLetterOrderIndex(string deadLetterTable);

    public abstract string SchedulerDueIndex(string jobsTable);

    /// <summary>A query returning 1 when the table exists in the default schema, else 0.</summary>
    public abstract string TableExistsSql(string table);

    public abstract string ColumnExistsSql(string table, string column);

    public abstract string IndexExistsSql(string table, string index);

    public abstract string DropIndexSql(string table, string index);

    /// <summary>
    /// A queue table as an old version of the script would have left it: the original columns only,
    /// and none of its indexes — the state the repair tests start from.
    /// </summary>
    public abstract string LegacyQueueTableSql(string table);

    /// <summary>A scheduled-jobs table without its due index.</summary>
    public abstract string LegacyJobsTableSql(string table);

    // ------------------------------------------------------------------------ execution ---

    public async Task<DbConnection> OpenConnectionAsync()
    {
        var connection = CreateConnection();
        await connection.OpenAsync();
        return connection;
    }

    protected abstract DbConnection CreateConnection();

    /// <summary>
    /// Runs a scalar query, converting the result: dialects disagree on, for example, whether
    /// <c>COUNT(*)</c> is an int or a bigint.
    /// </summary>
    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        if (value is null or DBNull)
            return default!;
        if (value is T typed)
            return typed;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<int> CountAsync(string table, string? where = null) =>
        await ScalarAsync<int>($"SELECT COUNT(*) FROM {Quote(table)}{(where is null ? "" : $" WHERE {where}")}");

    public async Task<bool> TableExists(string table) => await ScalarAsync<int>(TableExistsSql(table)) == 1;

    public async Task<bool> ColumnExists(string table, string column) =>
        await ScalarAsync<int>(ColumnExistsSql(table, column)) == 1;

    public async Task<bool> IndexExists(string table, string index) =>
        await ScalarAsync<int>(IndexExistsSql(table, index)) == 1;

    /// <summary>Creates a one-column side-effect table a handler can write to.</summary>
    public Task CreateSideTable(string table) =>
        ExecuteAsync($"CREATE TABLE {Quote(table)} (Value {ShortText} NOT NULL);");

    /// <summary>Inserts a side-effect row on <paramref name="transaction"/>.</summary>
    public async Task InsertSideEffect(DbTransaction transaction, string table, string? value, CancellationToken ct)
    {
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {Quote(table)} (Value) VALUES (@Value);";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@Value";
        parameter.Value = (object?)value ?? DBNull.Value;
        // Typed, so a null still binds on a provider that cannot infer a type from DBNull.
        parameter.DbType = System.Data.DbType.String;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Suite names carry the dialect, so a failure says which database it came from.</summary>
    public override string ToString() => Name;
}
