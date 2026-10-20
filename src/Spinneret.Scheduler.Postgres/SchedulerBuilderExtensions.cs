using Microsoft.Extensions.Configuration;
using Spinneret.Scheduler;
using Spinneret.Scheduler.Postgres;
using Spinneret.Scheduler.Sql;

// ReSharper disable once CheckNamespace — DI namespace for discoverability.
namespace Microsoft.Extensions.DependencyInjection;

public static class PostgresSchedulerBuilderExtensions
{
    private static readonly SqlSchedulerDescriptor Descriptor = new(
        nameof(UsePostgresStorage), PostgresSchedulerOptions.SectionName, PostgresSchedulerDialect.Instance);

    /// <summary>
    /// Stores scheduled jobs in PostgreSQL, in the queue's database, configured from <c>Scheduler:Postgres</c>.
    /// </summary>
    /// <remarks>
    /// Requires <c>AddQueue(q => q.UsePostgresStorage(...))</c>: a due job's booking and enqueue commit in one transaction.
    /// The sweep runs only when driven, by <c>s.UseInHostSweeper()</c> or the <c>Spinneret.Scheduler.AspNetCore</c> endpoint.
    /// </remarks>
    public static SchedulerBuilder UsePostgresStorage(this SchedulerBuilder scheduler, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return scheduler.UseSqlStorage<PostgresSchedulerOptions>(Descriptor, configuration, configure: null);
    }

    /// <summary>As <see cref="UsePostgresStorage(SchedulerBuilder, IConfiguration)"/>, configured in code.</summary>
    public static SchedulerBuilder UsePostgresStorage(this SchedulerBuilder scheduler, Action<PostgresSchedulerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return scheduler.UseSqlStorage(Descriptor, configuration: null, configure);
    }

    /// <summary>As <see cref="UsePostgresStorage(SchedulerBuilder, IConfiguration)"/>, with the defaults.</summary>
    public static SchedulerBuilder UsePostgresStorage(this SchedulerBuilder scheduler) =>
        scheduler.UseSqlStorage<PostgresSchedulerOptions>(Descriptor, configuration: null, configure: null);
}
