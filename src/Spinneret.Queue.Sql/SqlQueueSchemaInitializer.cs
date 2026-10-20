using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Spinneret.Queue.Sql;

internal sealed class SqlQueueSchemaInitializer(
    SqlQueueStorage storage,
    ILogger<SqlQueueSchemaInitializer> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken ct) =>
        storage.CreateSchema
            ? SqlSchemaScript.RunAsync(
                storage.DataSource, storage.Dialect.CreateSchemaScript(storage.Names), "Queue", logger, ct)
            : Task.CompletedTask;

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
