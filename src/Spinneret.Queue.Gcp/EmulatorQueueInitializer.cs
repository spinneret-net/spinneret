using Google.Cloud.Tasks.V2;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
// Google.Cloud.Tasks.V2 declares both a Task and a Queue; alias so the framework Task wins and the
// transport's queue message type is named unambiguously.
using Task = System.Threading.Tasks.Task;
using CloudTasksQueueResource = Google.Cloud.Tasks.V2.Queue;

namespace Spinneret.Queue.Gcp;

/// <summary>
/// Creates the configured channels' queues on the Cloud Tasks emulator at startup, the way the
/// MSSQL transport creates its tables. Production queues are owned by infrastructure-as-code and are
/// never touched: this runs only when <see cref="GcpQueueOptions.EmulatorEndpoint"/> is set.
/// </summary>
/// <remarks>
/// Without it, every emulator queue has to be declared twice — once in
/// <see cref="GcpQueueOptions.Channels"/> and again as a <c>-queue</c> flag on the emulator — and the
/// two drift silently: enqueueing to a queue the emulator never declared fails at the first task,
/// not at boot.
/// <para>
/// Takes an <see cref="IServiceProvider"/> rather than a <see cref="CloudTasksClient"/>: the host
/// constructs every hosted service at start, so a client injected here would be built on production
/// hosts too — and building one resolves Application Default Credentials, which a host that only
/// receives dispatches has no reason to hold. Resolving after the emulator check keeps that lazy.
/// </para>
/// </remarks>
internal sealed class EmulatorQueueInitializer(
    IServiceProvider services,
    IOptions<GcpQueueOptions> options,
    ILogger<EmulatorQueueInitializer> logger)
    : IHostedService
{
    private const int MaxAttempts = 5;

    public async Task StartAsync(CancellationToken ct)
    {
        var value = options.Value;
        if (!value.UsesEmulator)
            return;

        var client = services.GetRequiredService<CloudTasksClient>();
        foreach (var queueId in value.Channels.Values.Distinct(StringComparer.Ordinal))
            await EnsureQueue(client, value, queueId, ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task EnsureQueue(
        CloudTasksClient client, GcpQueueOptions value, string queueId, CancellationToken ct)
    {
        var queue = new CloudTasksQueueResource
        {
            Name = new QueueName(value.ProjectId, value.LocationId, queueId).ToString(),
        };
        // CreateQueue's parent resource path. Spelled out because Google.Cloud.Tasks.V2 ships a
        // QueueName helper but no LocationName.
        var parent = $"projects/{value.ProjectId}/locations/{value.LocationId}";

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await client.CreateQueueAsync(parent, queue, ct);
                logger.LogInformation("Created emulator queue {QueueId}", queueId);
                return;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
            {
                return;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
            {
                // Some emulator builds only serve queues declared up front via -queue flags. Nothing
                // to do but say so clearly, rather than failing a local run over it.
                logger.LogWarning(
                    "The Cloud Tasks emulator does not support creating queues, so {QueueId} must be "
                    + "declared with a -queue flag on the emulator itself", queueId);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && !ct.IsCancellationRequested)
            {
                // At boot the emulator container is often still coming up alongside the host.
                logger.LogWarning(ex,
                    "Emulator queue creation attempt {Attempt}/{MaxAttempts} for {QueueId} failed; retrying",
                    attempt, MaxAttempts, queueId);
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
        }
    }
}
