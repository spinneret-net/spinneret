using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Spinneret.Queue;
using Spinneret.Queue.Mssql;
using Spinneret.Queue.Sql;
using Spinneret.Scheduler;
using Spinneret.Scheduler.Mssql;
using Spinneret.Scheduler.Sql;

namespace Spinneret.Sql.Conformance;

public sealed class MssqlTestDialect(ISqlDatabaseFixture? database = null) : SqlTestDialect(database)
{
    public override string Name => "mssql";
    public override string StorageSection => MssqlStorageOptions.SectionName;
    public override string SchedulerSection => MssqlSchedulerOptions.SectionName;
    public override string UseStorageName => "UseMssqlStorage";
    public override string SampleConnectionString => "Server=localhost;Database=q;Integrated Security=true;";
    public override (string, string, string) DefaultStorageNames => ("dbo", "SpinneretQueue", "SpinneretDeadLetters");
    public override string DefaultSchedulerTable => "SpinneretScheduledJobs";
    public override int MaxIdentifierLength => 128;

    public override QueueBuilder UseStorage(QueueBuilder queue, IConfiguration configuration) =>
        queue.UseMssqlStorage(configuration);

    public override QueueBuilder UseStorage(
        QueueBuilder queue, Func<IServiceProvider, DbDataSource> dataSource, IConfiguration configuration) =>
        queue.UseMssqlStorage(dataSource, configuration);

    public override SchedulerBuilder UseScheduler(SchedulerBuilder scheduler, IConfiguration configuration) =>
        scheduler.UseMssql(configuration);

    public override SqlStorageOptions StorageOptions(IServiceProvider services) =>
        services.GetRequiredService<IOptions<MssqlStorageOptions>>().Value;

    public override SqlSchedulerOptions SchedulerOptions(IServiceProvider services) =>
        services.GetRequiredService<IOptions<MssqlSchedulerOptions>>().Value;

    public override void ConfigureStorage(IServiceCollection services, Action<SqlStorageOptions> configure) =>
        services.Configure<MssqlStorageOptions>(configure);

    public override DbDataSource CreateDataSourceFor(string connectionString) =>
        SqlClientFactory.Instance.CreateDataSource(connectionString);

    protected override DbConnection CreateConnection() => new SqlConnection(ConnectionString);

    public override string Quote(string identifier) => $"[{identifier.Replace("]", "]]")}]";
    public override string Now => "SYSUTCDATETIME()";
    public override string MinutesFromNow(int minutes) => $"DATEADD(minute, {minutes}, SYSUTCDATETIME())";
    public override string ShortText => "NVARCHAR(50)";

    public override void AddTimestamp(DbCommand command, string name, DateTimeOffset value) =>
        command.Parameters.Add(new SqlParameter(name, SqlDbType.DateTime2) { Value = value.UtcDateTime });

    public override string DedupeIndex(string queueTable) => $"UX_{queueTable}_DedupeKey";
    public override string ChannelIndex(string queueTable) => $"IX_{queueTable}_Channel_VisibleAt";
    public override string DeadLetterOrderIndex(string deadLetterTable) => $"IX_{deadLetterTable}_DeadLetteredAt";
    public override string SchedulerDueIndex(string jobsTable) => $"IX_{jobsTable}_NextExecuteAt";

    public override string TableExistsSql(string table) =>
        $"SELECT COUNT(*) FROM sys.tables WHERE name = N'{table}'";

    public override string ColumnExistsSql(string table, string column) =>
        $"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'[{table}]', N'U') AND name = N'{column}'";

    public override string IndexExistsSql(string table, string index) =>
        $"SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'[{table}]', N'U') AND name = N'{index}'";

    public override string DropIndexSql(string table, string index) => $"DROP INDEX [{index}] ON [{table}];";

    public override string LegacyQueueTableSql(string table) => $"""
        CREATE TABLE [{table}] (
            Id BIGINT IDENTITY NOT NULL CONSTRAINT [PK_{table}] PRIMARY KEY,
            Channel NVARCHAR(100) NOT NULL,
            VisibleAt DATETIME2(3) NOT NULL,
            DedupeKey NVARCHAR(200) NULL,
            Envelope NVARCHAR(MAX) NOT NULL);
        """;

    public override string LegacyJobsTableSql(string table) => $"""
        CREATE TABLE [{table}] (
            JobKey NVARCHAR(200) NOT NULL CONSTRAINT [PK_{table}] PRIMARY KEY,
            RequestTypeName NVARCHAR(500) NOT NULL,
            PayloadJson NVARCHAR(MAX) NOT NULL,
            Schedule NVARCHAR(500) NULL,
            NextExecuteAt DATETIME2(3) NOT NULL,
            CreatedAt DATETIME2(3) NOT NULL,
            LastRunAt DATETIME2(3) NULL);
        """;
}
