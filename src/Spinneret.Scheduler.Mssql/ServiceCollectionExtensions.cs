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
    /// <see cref="IMssqlTransactionalScheduler"/>, the recurring-job installer and the schema
    /// initializer. Requires <c>AddMssqlQueue</c> to be called first — the scheduler stores its
    /// jobs next to the queue and dispatches onto it in one transaction.
    /// </summary>
    /// <remarks>
    /// Call <c>AddMssqlSchedulerSweeper()</c> on the host(s) that should dispatch due jobs;
    /// sweeps race safely across hosts. Configuration is read from the <c>Scheduler:Mssql</c>
    /// section.
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
        if (services.All(d => d.ServiceType != typeof(MssqlQueue)))
            throw new InvalidOperationException(
                "AddMssqlScheduler requires AddMssqlQueue to be called first: the scheduler stores its "
                + "jobs in the queue's database and dispatches onto the queue transactionally.");

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
        services.AddHostedService<MssqlSchedulerSchemaInitializer>();
        // After the schema initializer: hosted services start in registration order, and the
        // installer writes job rows into the table that initializer creates. The installer retries,
        // so getting this order wrong would cost a backoff rather than the jobs.
        services.AddRecurringJobInstaller();

        return services;
    }

    /// <summary>
    /// Registers the sweep that dispatches due jobs. Separate from <c>AddMssqlScheduler</c>
    /// so hosts that only declare or one-shot-schedule jobs don't also dispatch them.
    /// </summary>
    public static IServiceCollection AddMssqlSchedulerSweeper(this IServiceCollection services)
    {
        services.AddHostedService<MssqlSchedulerSweeper>();
        return services;
    }
}
