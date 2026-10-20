using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spinneret.Queue.Sql;

namespace Spinneret.Scheduler.Sql;

/// <summary>
/// A dialect package's scheduler storage, as the shared registration needs it: the <c>Use*</c>
/// method name (for error messages), the options section, and the dialect.
/// </summary>
internal sealed record SqlSchedulerDescriptor(string MethodName, string SectionName, SqlSchedulerDialect Dialect);

/// <summary>
/// The service registrations every SQL scheduler dialect shares. A dialect package describes itself
/// with a <see cref="SqlSchedulerDescriptor"/> and its options type; the queue storage the scheduler
/// shares a database with comes from the SQL queue registration, in either order — it is only
/// resolved when the scheduler is, which the schema initializer makes happen as the host starts.
/// </summary>
internal static class SqlSchedulerRegistration
{
    /// <summary>
    /// Sets the scheduler's storage to a SQL dialect, binding <typeparamref name="TOptions"/> from
    /// the descriptor's section of <paramref name="configuration"/> (when given), then
    /// <paramref name="configure"/>; validated eagerly and again at host start.
    /// </summary>
    public static SchedulerBuilder UseSqlStorage<TOptions>(
        this SchedulerBuilder scheduler,
        SqlSchedulerDescriptor descriptor,
        IConfiguration? configuration,
        Action<TOptions>? configure)
        where TOptions : SqlSchedulerOptions, new()
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(descriptor);

        Action<TOptions> apply = options =>
        {
            configuration?.GetSection(descriptor.SectionName).Bind(options);
            configure?.Invoke(options);
        };
        var validator = new SqlSchedulerOptionsValidator<TOptions>(descriptor.Dialect, descriptor.SectionName);

        return scheduler.UseStorage(new SchedulerStorageRole(descriptor.MethodName, services =>
        {
            // Fail as early as possible on broken configuration; the options-pipeline validation
            // re-validates at host start to also cover later Configure/PostConfigure changes.
            var eagerlyBound = new TOptions();
            apply(eagerlyBound);
            validator.ValidateOrThrow(eagerlyBound);

            services.AddOptions<TOptions>().Configure(apply).ValidateOnStart();
            services.AddSingleton<IValidateOptions<TOptions>>(validator);

            services.AddSqlScheduler(
                descriptor,
                sp => sp.GetRequiredService<IOptions<TOptions>>().Value.TableName);
        }));
    }

    internal static IServiceCollection AddSqlScheduler(
        this IServiceCollection services, SqlSchedulerDescriptor descriptor, Func<IServiceProvider, string> table)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(table);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new SqlSchedulerStorage(
            sp.GetService<SqlQueueStorage>()
                ?? throw new InvalidOperationException(MissingQueueStorage(descriptor)),
            descriptor.Dialect,
            table(sp)));
        services.TryAddSingleton<IRecurringJobScheduler, SqlScheduler>();
        services.TryAddSingleton<ISqlTransactionalScheduler, SqlTransactionalScheduler>();
        // The sweep engine, not the trigger: registering it costs nothing until something drives it,
        // and the host chooses that separately with UseSweeper() or an HTTP trigger.
        services.TryAddSingleton<ISchedulerSweep, SqlSchedulerSweeper>();
        // Before the recurring-job installer, which SchedulerBuilder adds after the storage: hosted
        // services start in registration order, and the installer writes into this table. It is
        // also what checks that the scheduler shares the queue's database: the scheduler and the
        // queue register independently, in either order, so that can only be known once both are
        // in — and the host resolves this initializer, and with it the storage, as it starts.
        services.AddHostedService<SqlSchedulerSchemaInitializer>();

        return services;
    }

    private static string MissingQueueStorage(SqlSchedulerDescriptor descriptor) =>
        $"s.{descriptor.MethodName}(...) requires the queue's storage in the same database: a due job's booking "
        + "and its enqueue commit in one transaction. Add the SQL queue storage of the same database, e.g. "
        + "AddQueue(q => q.UseMssqlStorage(...)) or AddQueue(q => q.UsePostgresStorage(...)).";
}

/// <summary>Validates a dialect's <see cref="SqlSchedulerOptions"/>: a table name safe to splice into SQL.</summary>
internal sealed class SqlSchedulerOptionsValidator<TOptions>(SqlSchedulerDialect dialect, string sectionName)
    : IValidateOptions<TOptions>
    where TOptions : SqlSchedulerOptions
{
    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        try
        {
            ValidateOrThrow(options);
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException ex)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }
    }

    public void ValidateOrThrow(TOptions o)
    {
        SqlIdentifiers.Validate(dialect.QueueDialect, o.TableName, $"{sectionName}:TableName");
        SqlIdentifiers.ValidateDerived(dialect.QueueDialect, dialect.DerivedIdentifiers(o.TableName), $"{sectionName}:TableName");
    }
}

/// <summary>
/// Creates the scheduled-jobs table idempotently at startup, gated by the queue storage's
/// CreateSchema switch — one schema-ownership decision for both.
/// </summary>
internal sealed class SqlSchedulerSchemaInitializer(
    SqlSchedulerStorage storage,
    ILogger<SqlSchedulerSchemaInitializer> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken ct) =>
        storage.Queue.CreateSchema
            ? SqlSchemaScript.RunAsync(
                storage.Queue.DataSource, storage.Dialect.CreateSchemaScript(storage.Names), "Scheduler", logger, ct)
            : Task.CompletedTask;

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
