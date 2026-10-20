using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Spinneret.Queue;
using Spinneret.Queue.Postgres;
using Spinneret.Queue.Sql;
using Spinneret.Scheduler;
using Spinneret.Scheduler.Postgres;
using Spinneret.Scheduler.Sql;

namespace Spinneret.Sql.Conformance;

public sealed class PostgresTestDialect(ISqlDatabaseFixture? database = null) : SqlTestDialect(database)
{
    public override string Name => "postgres";
    public override string StorageSection => PostgresStorageOptions.SectionName;
    public override string SchedulerSection => PostgresSchedulerOptions.SectionName;
    public override string UseStorageName => "UsePostgresStorage";
    public override string SampleConnectionString => "Host=localhost;Port=1;Database=q;Username=app;Password=secret;Timeout=2";
    public override (string, string, string) DefaultStorageNames => ("public", "spinneret_queue", "spinneret_dead_letters");
    public override string DefaultSchedulerTable => "spinneret_scheduled_jobs";
    public override int MaxIdentifierLength => 63;

    public override QueueBuilder UseStorage(QueueBuilder queue, IConfiguration configuration) =>
        queue.UsePostgresStorage(configuration);

    public override QueueBuilder UseStorage(
        QueueBuilder queue, Func<IServiceProvider, DbDataSource> dataSource, IConfiguration configuration) =>
        queue.UsePostgresStorage(sp => (NpgsqlDataSource)dataSource(sp), configuration);

    public override SchedulerBuilder UseScheduler(SchedulerBuilder scheduler, IConfiguration configuration) =>
        scheduler.UsePostgresStorage(configuration);

    public override SqlStorageOptions StorageOptions(IServiceProvider services) =>
        services.GetRequiredService<IOptions<PostgresStorageOptions>>().Value;

    public override SqlSchedulerOptions SchedulerOptions(IServiceProvider services) =>
        services.GetRequiredService<IOptions<PostgresSchedulerOptions>>().Value;

    public override void ConfigureStorage(IServiceCollection services, Action<SqlStorageOptions> configure) =>
        services.Configure<PostgresStorageOptions>(configure);

    public override DbDataSource CreateDataSourceFor(string connectionString) => NpgsqlDataSource.Create(connectionString);

    protected override DbConnection CreateConnection() => new NpgsqlConnection(ConnectionString);

    public override string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
    public override string Now => "now()";
    public override string MinutesFromNow(int minutes) => $"(now() + interval '{minutes} minutes')";
    public override string ShortText => "varchar(50)";

    public override void AddTimestamp(DbCommand command, string name, DateTimeOffset value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = value.ToUniversalTime() });

    public override string DedupeIndex(string queueTable) => $"{queueTable}_dedupekey_key";
    public override string ChannelIndex(string queueTable) => $"{queueTable}_channel_visibleat_idx";
    public override string DeadLetterOrderIndex(string deadLetterTable) => $"{deadLetterTable}_deadletteredat_idx";
    public override string SchedulerDueIndex(string jobsTable) => $"{jobsTable}_nextexecuteat_idx";

    public override string TableExistsSql(string table) =>
        $"SELECT COUNT(*) FROM pg_tables WHERE schemaname = current_schema() AND tablename = '{table}'";

    // Unquoted in the DDL, so stored folded to lower case.
    public override string ColumnExistsSql(string table, string column) =>
        $"SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = current_schema() "
        + $"AND table_name = '{table}' AND column_name = '{column.ToLowerInvariant()}'";

    public override string IndexExistsSql(string table, string index) =>
        $"SELECT COUNT(*) FROM pg_indexes WHERE schemaname = current_schema() AND tablename = '{table}' AND indexname = '{index}'";

    public override string DropIndexSql(string table, string index) => $"DROP INDEX {Quote(index)};";

    public override string LegacyQueueTableSql(string table) => $"""
        CREATE TABLE {Quote(table)} (
            Id bigint GENERATED ALWAYS AS IDENTITY NOT NULL CONSTRAINT {Quote($"{table}_pkey")} PRIMARY KEY,
            Channel varchar(100) NOT NULL,
            VisibleAt timestamptz NOT NULL,
            DedupeKey varchar(200) NULL,
            Envelope text NOT NULL);
        """;

    public override string LegacyJobsTableSql(string table) => $"""
        CREATE TABLE {Quote(table)} (
            JobKey varchar(200) NOT NULL CONSTRAINT {Quote($"{table}_pkey")} PRIMARY KEY,
            RequestTypeName varchar(500) NOT NULL,
            PayloadJson text NOT NULL,
            Schedule varchar(500) NULL,
            NextExecuteAt timestamptz NOT NULL,
            CreatedAt timestamptz NOT NULL,
            LastRunAt timestamptz NULL);
        """;
}
