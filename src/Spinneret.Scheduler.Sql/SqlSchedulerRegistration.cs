using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spinneret.Queue.Sql;

namespace Spinneret.Scheduler.Sql;

internal sealed record SqlSchedulerDescriptor(string MethodName, string SectionName, SqlSchedulerDialect Dialect);

// The queue storage may be registered in either order; it is resolved only as the host starts.
internal static class SqlSchedulerRegistration
{
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
            // Fail early; ValidateOnStart re-checks later Configure/PostConfigure changes.
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
        services.TryAddSingleton<ISchedulerSweep, SqlSchedulerSweeper>();
        // Must start before the installer, which writes into this table. Resolving it at startup
        // also checks that the scheduler shares the queue's database.
        services.AddHostedService<SqlSchedulerSchemaInitializer>();

        return services;
    }

    private static string MissingQueueStorage(SqlSchedulerDescriptor descriptor) =>
        $"s.{descriptor.MethodName}(...) requires the queue's storage in the same database: a due job's booking "
        + "and its enqueue commit in one transaction. Add the SQL queue storage of the same database, e.g. "
        + "AddQueue(q => q.UseMssqlStorage(...)) or AddQueue(q => q.UsePostgresStorage(...)).";
}

// The table name is spliced into SQL, so it must be a safe identifier.
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

// Gated by the queue's CreateSchema: one schema-ownership decision for both.
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
