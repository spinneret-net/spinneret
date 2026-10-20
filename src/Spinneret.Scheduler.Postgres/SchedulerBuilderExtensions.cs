using Microsoft.Extensions.Configuration;
using Spinneret.Scheduler;
using Spinneret.Scheduler.Postgres;
using Spinneret.Scheduler.Sql;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Use* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection;

public static class PostgresSchedulerBuilderExtensions
{
    private static readonly SqlSchedulerDescriptor Descriptor = new(
        nameof(UsePostgres), PostgresSchedulerOptions.SectionName, PostgresSchedulerDialect.Instance);

    /// <summary>
    /// Stores scheduled jobs in PostgreSQL, next to the queue: provides
    /// <see cref="IRecurringJobScheduler"/>, <see cref="ISqlTransactionalScheduler"/>, the sweep engine
    /// and the schema initializer. Requires <c>AddQueue(q => q.UsePostgresStorage(...))</c> — in
    /// either registration order — since a due job's booking and its enqueue commit in one
    /// transaction, which needs the same database. The connection, schema name and schema creation
    /// follow the queue storage's.
    /// </summary>
    /// <remarks>
    /// The sweep runs only when something drives it: <c>s.UseSweeper()</c> for a timer, or the
    /// endpoint from <c>Spinneret.Scheduler.AspNetCore</c> for an external cron; sweeps race safely across
    /// hosts either way. Configuration is read from the <c>Scheduler:Postgres</c> section.
    /// </remarks>
    public static SchedulerBuilder UsePostgres(this SchedulerBuilder scheduler, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return scheduler.UseSqlStorage<PostgresSchedulerOptions>(Descriptor, configuration, configure: null);
    }

    /// <summary>As <see cref="UsePostgres(SchedulerBuilder, IConfiguration)"/>, configured in code.</summary>
    public static SchedulerBuilder UsePostgres(this SchedulerBuilder scheduler, Action<PostgresSchedulerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return scheduler.UseSqlStorage(Descriptor, configuration: null, configure);
    }

    /// <summary>As <see cref="UsePostgres(SchedulerBuilder, IConfiguration)"/>, with the defaults.</summary>
    public static SchedulerBuilder UsePostgres(this SchedulerBuilder scheduler) =>
        scheduler.UseSqlStorage<PostgresSchedulerOptions>(Descriptor, configuration: null, configure: null);
}
