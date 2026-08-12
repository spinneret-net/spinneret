using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Spinneret.Queue.Mssql;

/// <summary>
/// Creates the queue tables idempotently at startup, before the worker and producers touch them.
/// Retries briefly because at boot the database is often still warming up next to the host (fresh
/// container, failover); a database that stays unreachable fails startup, which is the right
/// outcome for a host whose queue cannot work.
/// </summary>
internal sealed class MssqlQueueSchemaInitializer(
    IOptions<MssqlQueueOptions> options,
    ILogger<MssqlQueueSchemaInitializer> logger)
    : IHostedService
{
    private const int MaxAttempts = 5;

    public async Task StartAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (!o.CreateSchema)
            return;

        var script = MssqlQueueSchema.CreateScript(o);

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
                    "Queue schema creation attempt {Attempt}/{MaxAttempts} failed; retrying",
                    attempt, MaxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
