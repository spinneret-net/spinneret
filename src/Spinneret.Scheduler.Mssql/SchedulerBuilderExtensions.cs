using Microsoft.Extensions.Configuration;
using Spinneret.Scheduler;
using Spinneret.Scheduler.Mssql;
using Spinneret.Scheduler.Sql;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Use* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection;

public static class MssqlSchedulerBuilderExtensions
{
    private static readonly SqlSchedulerDescriptor Descriptor = new(
        nameof(UseMssql), MssqlSchedulerOptions.SectionName, MssqlSchedulerDialect.Instance);

    /// <summary>
    /// Stores scheduled jobs in SQL Server, next to the queue: provides
    /// <see cref="IRecurringJobScheduler"/>, <see cref="ISqlTransactionalScheduler"/>, the sweep engine
    /// and the schema initializer. Requires <c>AddQueue(q => q.UseMssqlStorage(...))</c> — in either
    /// registration order — since a due job's booking and its enqueue commit in one transaction,
    /// which needs the same database. The connection, schema name and schema creation follow the
    /// queue storage's.
    /// </summary>
    /// <remarks>
    /// The sweep runs only when something drives it: <c>s.UseSweeper()</c> for a timer, or the
    /// endpoint from <c>Spinneret.Scheduler.Http</c> for an external cron; sweeps race safely across
    /// hosts either way. Configuration is read from the <c>Scheduler:Mssql</c> section.
    /// </remarks>
    public static SchedulerBuilder UseMssql(this SchedulerBuilder scheduler, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return scheduler.UseSqlStorage<MssqlSchedulerOptions>(Descriptor, configuration, configure: null);
    }

    /// <summary>As <see cref="UseMssql(SchedulerBuilder, IConfiguration)"/>, configured in code.</summary>
    public static SchedulerBuilder UseMssql(this SchedulerBuilder scheduler, Action<MssqlSchedulerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return scheduler.UseSqlStorage(Descriptor, configuration: null, configure);
    }

    /// <summary>As <see cref="UseMssql(SchedulerBuilder, IConfiguration)"/>, with the defaults.</summary>
    public static SchedulerBuilder UseMssql(this SchedulerBuilder scheduler) =>
        scheduler.UseSqlStorage<MssqlSchedulerOptions>(Descriptor, configuration: null, configure: null);
}
