using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spinneret.Queue.Mssql;

namespace Spinneret.Scheduler.Mssql;

/// <summary>
/// Creates the scheduled-jobs table idempotently at startup, gated by the queue's CreateSchema
/// switch (one schema-ownership decision for both packages). Retries briefly for the same
/// database-warming-up reason as the queue's initializer.
/// </summary>
internal sealed class MssqlSchedulerSchemaInitializer(
    IOptions<MssqlQueueOptions> queueOptions,
    IOptions<MssqlSchedulerOptions> schedulerOptions,
    ILogger<MssqlSchedulerSchemaInitializer> logger)
    : IHostedService
{
    private const int MaxAttempts = 5;

    public async Task StartAsync(CancellationToken ct)
    {
        var o = queueOptions.Value;
        if (!o.CreateSchema)
            return;

        var script = MssqlSchedulerSchema.CreateScript(o, schedulerOptions.Value);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(o.ConnectionString);
                await connection.OpenAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = script;
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
