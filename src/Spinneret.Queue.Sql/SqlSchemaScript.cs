using System.Data.Common;
using Microsoft.Extensions.Logging;

namespace Spinneret.Queue.Sql;

// Retries briefly because the database is often still starting next to the host.
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
