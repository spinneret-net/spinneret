using System.Data.Common;
using Microsoft.Extensions.Logging;

namespace Spinneret.Queue.Sql;

/// <summary>
/// Runs an idempotent schema script at startup, retrying briefly: at boot the database is often
/// still warming up next to the host (fresh container, failover). A database that stays unreachable
/// fails startup, which is the right outcome for a host whose queue cannot work. The script supplies
/// its own transaction and locking; it runs as one command outside any transaction.
/// </summary>
internal static class SqlSchemaScript
{
    private const int MaxAttempts = 5;

    public static async Task RunAsync(
        DbDataSource dataSource, string script, string what, ILogger logger, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(ct);
                await using var command = connection.CreateCommand(transaction: null, script);
                await command.ExecuteNonQueryAsync(ct);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex,
                    "{What} schema creation attempt {Attempt}/{MaxAttempts} failed; retrying",
                    what, attempt, MaxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
        }
    }
}
