using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Spinneret.Queue;
using Spinneret.Queue.Sql;
using Spinneret.Scheduler;
using Spinneret.Scheduler.Sql;

namespace Spinneret.Sql.Conformance;

/// <summary>What the suites need to know about a database. Raw SQL quotes table names through
/// <see cref="Quote"/> and leaves column names unquoted.</summary>
public abstract class SqlTestDialect(ISqlDatabaseFixture? database)
{
    /// <summary>Only valid in suites that have a fixture.</summary>
    public string ConnectionString =>
        database?.ConnectionString
        ?? throw new InvalidOperationException("This suite runs without a database.");

    public abstract string Name { get; }

    public abstract string StorageSection { get; }

    public abstract string SchedulerSection { get; }

    public abstract string UseStorageName { get; }

    public abstract string SampleConnectionString { get; }

    public abstract (string Schema, string QueueTable, string DeadLetterTable) DefaultStorageNames { get; }

    public abstract string DefaultSchedulerTable { get; }

    public abstract int MaxIdentifierLength { get; }

    public abstract QueueBuilder UseStorage(QueueBuilder queue, IConfiguration configuration);

    public abstract QueueBuilder UseStorage(
        QueueBuilder queue, Func<IServiceProvider, DbDataSource> dataSource, IConfiguration configuration);

    public abstract SchedulerBuilder UseScheduler(SchedulerBuilder scheduler, IConfiguration configuration);

    public abstract SqlStorageOptions StorageOptions(IServiceProvider services);

    public abstract SqlSchedulerOptions SchedulerOptions(IServiceProvider services);

    public abstract void ConfigureStorage(IServiceCollection services, Action<SqlStorageOptions> configure);

    public DbDataSource CreateDataSource() => CreateDataSourceFor(ConnectionString);

    public abstract DbDataSource CreateDataSourceFor(string connectionString);

    public abstract string Quote(string identifier);

    public abstract string Now { get; }

    public abstract string MinutesFromNow(int minutes);

    public abstract string ShortText { get; }

    public abstract void AddTimestamp(DbCommand command, string name, DateTimeOffset value);

    public abstract string DedupeIndex(string queueTable);

    public abstract string ChannelIndex(string queueTable);

    public abstract string DeadLetterOrderIndex(string deadLetterTable);

    public abstract string SchedulerDueIndex(string jobsTable);

    /// <summary>Returns 1 when the table exists in the default schema, else 0.</summary>
    public abstract string TableExistsSql(string table);

    public abstract string ColumnExistsSql(string table, string column);

    public abstract string IndexExistsSql(string table, string index);

    public abstract string DropIndexSql(string table, string index);

    /// <summary>A queue table as an old script left it: original columns, no indexes.</summary>
    public abstract string LegacyQueueTableSql(string table);

    public abstract string LegacyJobsTableSql(string table);

    public async Task<DbConnection> OpenConnectionAsync()
    {
        var connection = CreateConnection();
        await connection.OpenAsync();
        return connection;
    }

    protected abstract DbConnection CreateConnection();

    /// <summary>Converts the result, since dialects disagree on types such as <c>COUNT(*)</c>.</summary>
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

    public Task CreateSideTable(string table) =>
        ExecuteAsync($"CREATE TABLE {Quote(table)} (Value {ShortText} NOT NULL);");

    public async Task InsertSideEffect(DbTransaction transaction, string table, string? value, CancellationToken ct)
    {
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {Quote(table)} (Value) VALUES (@Value);";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@Value";
        parameter.Value = (object?)value ?? DBNull.Value;
        // Typed, so a null still binds on a provider that can't infer a type from DBNull.
        parameter.DbType = System.Data.DbType.String;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Suite names carry the dialect, so a failure says which database it came from.</summary>
    public override string ToString() => Name;
}
