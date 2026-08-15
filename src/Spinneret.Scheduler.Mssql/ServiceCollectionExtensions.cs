using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Spinneret.Queue.Mssql;
using Spinneret.Scheduler;
using Spinneret.Scheduler.Mssql;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Add* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection;

public static class MssqlSchedulerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQL Server scheduler: <see cref="IRecurringJobScheduler"/>,
    /// <see cref="IMssqlTransactionalScheduler"/>, the sweep engine, the recurring-job installer and
    /// the schema initializer. Requires <c>AddMssqlQueue</c> — in either registration order — since
    /// the scheduler stores its jobs next to the queue and dispatches onto it in one transaction.
    /// </summary>
    /// <remarks>
    /// This registers no trigger. Add <c>AddSchedulerSweeper()</c> on the host(s) that should
    /// dispatch due jobs, or map the endpoint from <c>Spinneret.Scheduler.Http</c> to be driven by an
    /// external cron; sweeps race safely across hosts either way. Configuration is read from the
    /// <c>Scheduler:Mssql</c> section, and the sweep cadence from <c>Scheduler:Sweeper:SweepInterval</c>.
    /// </remarks>
    public static IServiceCollection AddMssqlScheduler(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(MssqlSchedulerOptions.SectionName);

        var bound = new MssqlSchedulerOptions();
        section.Bind(bound);

        return services.AddMssqlSchedulerCore(options => section.Bind(options), bound);
    }

    /// <summary>
    /// Overload for hosts that configure the scheduler in code instead of via
    /// <see cref="IConfiguration"/> (tests, embedded scenarios).
    /// </summary>
    public static IServiceCollection AddMssqlScheduler(this IServiceCollection services, Action<MssqlSchedulerOptions> configure)
    {
        var bound = new MssqlSchedulerOptions();
        configure(bound);

        return services.AddMssqlSchedulerCore(configure, bound);
    }

    private static IServiceCollection AddMssqlSchedulerCore(
        this IServiceCollection services,
        Action<MssqlSchedulerOptions> configure,
        MssqlSchedulerOptions eagerlyBound)
    {
        // No "was AddMssqlQueue called first?" guard: this method registers lazily and reads nothing
        // from the collection, so it composes in any order. A genuinely missing queue surfaces when
        // the container resolves the sweep and names the service it could not supply.

        // Fail as early as possible on broken configuration; the options-pipeline validation
        // below re-validates at host start to also cover later Configure/PostConfigure changes.
        MssqlSchedulerOptionsValidator.ValidateOrThrow(eagerlyBound);

        services.AddOptions<MssqlSchedulerOptions>()
            .Configure(configure)
            .ValidateOnStart();
        services.TryAddSingleton<IValidateOptions<MssqlSchedulerOptions>, MssqlSchedulerOptionsValidator>();

        services.TryAddSingleton(sp => new ScheduledJobSql(
            sp.GetRequiredService<IOptions<MssqlQueueOptions>>().Value,
            sp.GetRequiredService<IOptions<MssqlSchedulerOptions>>().Value));
        services.TryAddSingleton<IRecurringJobScheduler, MssqlScheduler>();
        services.TryAddSingleton<IMssqlTransactionalScheduler, MssqlTransactionalScheduler>();
        // The sweep engine, not the trigger: registering it costs nothing until something drives it,
        // and the host chooses that separately with AddSchedulerSweeper() or an HTTP trigger.
        services.TryAddSingleton<ISchedulerSweep, MssqlSchedulerSweeper>();
        services.AddHostedService<MssqlSchedulerSchemaInitializer>();
        // After the schema initializer: hosted services start in registration order, and the
        // installer writes job rows into the table that initializer creates. The installer retries,
        // so getting this order wrong would cost a backoff rather than the jobs.
        services.AddRecurringJobInstaller();

        return services;
    }
}
