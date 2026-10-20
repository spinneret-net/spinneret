using Microsoft.Extensions.Configuration;
using Spinneret.Scheduler;
using Spinneret.Scheduler.Mssql;
using Spinneret.Scheduler.Sql;

// ReSharper disable once CheckNamespace — DI namespace for discoverability.
namespace Microsoft.Extensions.DependencyInjection;

public static class MssqlSchedulerBuilderExtensions
{
    private static readonly SqlSchedulerDescriptor Descriptor = new(
        nameof(UseMssqlStorage), MssqlSchedulerOptions.SectionName, MssqlSchedulerDialect.Instance);

    /// <summary>
    /// Stores scheduled jobs in SQL Server, in the queue's database, configured from <c>Scheduler:Mssql</c>.
    /// </summary>
    /// <remarks>
    /// Requires <c>AddQueue(q => q.UseMssqlStorage(...))</c>: a due job's booking and enqueue commit in one transaction.
    /// The sweep runs only when driven, by <c>s.UseInHostSweeper()</c> or the <c>Spinneret.Scheduler.AspNetCore</c> endpoint.
    /// </remarks>
    public static SchedulerBuilder UseMssqlStorage(this SchedulerBuilder scheduler, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return scheduler.UseSqlStorage<MssqlSchedulerOptions>(Descriptor, configuration, configure: null);
    }

    /// <summary>As <see cref="UseMssqlStorage(SchedulerBuilder, IConfiguration)"/>, configured in code.</summary>
    public static SchedulerBuilder UseMssqlStorage(this SchedulerBuilder scheduler, Action<MssqlSchedulerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return scheduler.UseSqlStorage(Descriptor, configuration: null, configure);
    }

    /// <summary>As <see cref="UseMssqlStorage(SchedulerBuilder, IConfiguration)"/>, with the defaults.</summary>
    public static SchedulerBuilder UseMssqlStorage(this SchedulerBuilder scheduler) =>
        scheduler.UseSqlStorage<MssqlSchedulerOptions>(Descriptor, configuration: null, configure: null);
}
