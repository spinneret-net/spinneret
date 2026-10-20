using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spinneret.Queue.Sql;

namespace Spinneret.Scheduler.Sql;

/// <summary>
/// The service registrations every SQL scheduler dialect shares. A dialect package names its dialect
/// and how to find its table name; the queue storage the scheduler shares a database with comes
/// from the SQL queue registration, in either order — it is only resolved when the scheduler is.
/// </summary>
internal static class SqlSchedulerRegistration
{
    public static IServiceCollection AddSqlScheduler(
        this IServiceCollection services, SqlSchedulerDialect dialect, Func<IServiceProvider, string> table)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(table);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new SqlSchedulerStorage(
            sp.GetService<SqlQueueStorage>()
                ?? throw new InvalidOperationException(
                    "The SQL scheduler requires the SQL queue storage in the same database "
                    + "(AddQueue with q.UseMssqlStorage, or another SQL storage)."),
            dialect,
            table(sp)));
        services.TryAddSingleton<IRecurringJobScheduler, SqlScheduler>();
        services.TryAddSingleton<ISqlTransactionalScheduler, SqlTransactionalScheduler>();
        // The sweep engine, not the trigger: registering it costs nothing until something drives it,
        // and the host chooses that separately with UseSweeper() or an HTTP trigger.
        services.TryAddSingleton<ISchedulerSweep, SqlSchedulerSweeper>();
        // Before the recurring-job installer, which SchedulerBuilder adds after the storage: hosted
        // services start in registration order, and the installer writes into this table.
        services.AddHostedService<SqlSchedulerSchemaInitializer>();

        return services;
    }
}

/// <summary>
/// Creates the scheduled-jobs table idempotently at startup, gated by the queue storage's
/// CreateSchema switch — one schema-ownership decision for both. Retries briefly for the same
/// database-warming-up reason as the queue's initializer.
/// </summary>
internal sealed class SqlSchedulerSchemaInitializer(
    SqlSchedulerStorage storage,
    ILogger<SqlSchedulerSchemaInitializer> logger)
    : IHostedService
{
    private const int MaxAttempts = 5;

    public async Task StartAsync(CancellationToken ct)
    {
        if (!storage.Queue.CreateSchema)
            return;

        var script = storage.Dialect.CreateSchemaScript(storage.Names);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = await storage.Queue.DataSource.OpenConnectionAsync(ct);
                await using var command = connection.CreateCommand(transaction: null, script);
                await command.ExecuteNonQueryAsync(ct);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex,
                    "Scheduler schema creation attempt {Attempt}/{MaxAttempts} failed; retrying",
                    attempt, MaxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
