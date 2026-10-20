using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Spinneret.Scheduler;
using Spinneret.Scheduler.Mssql;
using Spinneret.Scheduler.Sql;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Use* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection;

public static class MssqlSchedulerBuilderExtensions
{
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
        var section = configuration.GetSection(MssqlSchedulerOptions.SectionName);
        return scheduler.UseMssqlCore(options => section.Bind(options));
    }

    /// <summary>As <see cref="UseMssql(SchedulerBuilder, IConfiguration)"/>, configured in code.</summary>
    public static SchedulerBuilder UseMssql(this SchedulerBuilder scheduler, Action<MssqlSchedulerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return scheduler.UseMssqlCore(configure);
    }

    /// <summary>As <see cref="UseMssql(SchedulerBuilder, IConfiguration)"/>, with the defaults.</summary>
    public static SchedulerBuilder UseMssql(this SchedulerBuilder scheduler) =>
        scheduler.UseMssqlCore(_ => { });

    private static SchedulerBuilder UseMssqlCore(this SchedulerBuilder scheduler, Action<MssqlSchedulerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(scheduler);

        return scheduler.UseStorage(new SchedulerStorageRole(nameof(UseMssql), services =>
        {
            // Fail as early as possible on broken configuration; the options-pipeline validation
            // re-validates at host start to also cover later Configure/PostConfigure changes.
            var eagerlyBound = new MssqlSchedulerOptions();
            configure(eagerlyBound);
            MssqlSchedulerOptionsValidator.ValidateOrThrow(eagerlyBound);

            services.AddOptions<MssqlSchedulerOptions>().Configure(configure).ValidateOnStart();
            services.AddSingleton<IValidateOptions<MssqlSchedulerOptions>, MssqlSchedulerOptionsValidator>();

            services.AddSqlScheduler(
                MssqlSchedulerDialect.Instance,
                sp => sp.GetRequiredService<IOptions<MssqlSchedulerOptions>>().Value.TableName);
        }));
    }
}
