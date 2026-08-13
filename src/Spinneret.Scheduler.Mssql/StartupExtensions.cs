using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Spinneret.Queue.Mssql;

namespace Spinneret.Scheduler.Mssql;

public static class StartupExtensions
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
        if (services.All(d => d.ServiceType != typeof(MssqlQueue)))
            throw new InvalidOperationException(
                "AddMssqlScheduler requires AddMssqlQueue to be called first: the scheduler stores its "
                + "jobs in the queue's database and dispatches onto the queue transactionally.");

        var section = configuration.GetSection(MssqlSchedulerOptions.SectionName);
        services.Configure<MssqlSchedulerOptions>(section);

        var bound = new MssqlSchedulerOptions();
        section.Bind(bound);
        Validate(bound);

        services.TryAddSingleton(sp => new ScheduledJobSql(
            sp.GetRequiredService<IOptions<MssqlQueueOptions>>().Value,
            sp.GetRequiredService<IOptions<MssqlSchedulerOptions>>().Value));
        services.TryAddSingleton<IRecurringJobScheduler, MssqlScheduler>();
        services.TryAddSingleton<IMssqlTransactionalScheduler, MssqlTransactionalScheduler>();
        services.AddHostedService<MssqlSchedulerSchemaInitializer>();
        // After the schema initializer: hosted services start in registration order, and the
        // installer writes job rows into the table that initializer creates.
        services.AddRecurringJobInstaller();

        return services;
    }

    /// <summary>
    /// Registers the sweep that dispatches due jobs. Separate from <see cref="AddMssqlScheduler"/>
    /// so hosts that only declare or one-shot-schedule jobs don't also dispatch them.
    /// </summary>
    public static IServiceCollection AddMssqlSchedulerSweeper(this IServiceCollection services)
    {
        services.AddHostedService<MssqlSchedulerSweeper>();
        return services;
    }

    private static void Validate(MssqlSchedulerOptions o)
    {
        if (!Identifier.IsValid(o.TableName))
            throw new InvalidOperationException(
                $"Scheduler:Mssql:TableName must be a plain SQL identifier (letters, digits, underscore); got '{o.TableName}'.");

        if (o.SweepInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("Scheduler:Mssql:SweepInterval must be positive.");
    }
}
