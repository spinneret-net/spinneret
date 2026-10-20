using Google.Cloud.Tasks.V2;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
// Google.Cloud.Tasks.V2 declares its own Task and Queue.
using Task = System.Threading.Tasks.Task;
using CloudTasksQueueResource = Google.Cloud.Tasks.V2.Queue;

namespace Spinneret.Queue.CloudTasks;

// Creates the configured queues on the emulator only. Retries in the background and never fails the host.
// Resolves the client lazily, so production hosts never build one (and need no ADC) just to start.
internal sealed class EmulatorQueueInitializer : BackgroundService
{
    internal static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(2);

    // Retries indefinitely; the cap bounds an absent emulator to about one log line a minute.
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(1);

    internal const int EscalateAfterAttempts = 5;

    private readonly IServiceProvider _services;
    private readonly IOptions<CloudTasksOptions> _options;
    private readonly ILogger<EmulatorQueueInitializer> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public EmulatorQueueInitializer(
        IServiceProvider services,
        IOptions<CloudTasksOptions> options,
        ILogger<EmulatorQueueInitializer> logger)
        : this(services, options, logger, (delay, ct) => Task.Delay(delay, ct))
    {
    }

    internal EmulatorQueueInitializer(
        IServiceProvider services,
        IOptions<CloudTasksOptions> options,
        ILogger<EmulatorQueueInitializer> logger,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _services = services;
        _options = options;
        _logger = logger;
        _delay = delay;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var options = _options.Value;
        if (!options.UsesEmulator)
            return;

        try
        {
            await CreateQueues(options, ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // An unhandled BackgroundService exception would stop the host.
            _logger.LogError(ex,
                "Emulator queue creation stopped. Declare the queues with -queue flags on the "
                + "emulator, or restart the host once it can be reached.");
        }
    }

    private async Task CreateQueues(CloudTasksOptions options, CancellationToken ct)
    {
        var client = _services.GetRequiredService<CloudTasksClient>();
        // Spelled out: Google.Cloud.Tasks.V2 has no LocationName helper.
        var parent = $"projects/{options.ProjectId}/locations/{options.LocationId}";
        var pending = options.Channels.Values.Distinct(StringComparer.Ordinal).ToList();

        for (var attempt = 1; !ct.IsCancellationRequested; attempt++)
        {
            // One shared backoff per round, so the wait doesn't multiply with the number of channels.
            var failed = new List<string>();
            foreach (var queueId in pending)
                if (!await TryCreateQueue(client, options, parent, queueId, attempt, ct))
                    failed.Add(queueId);
            pending = failed;

            if (pending.Count == 0)
                return;

            try
            {
                await _delay(RetryDelay(attempt), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<bool> TryCreateQueue(
        CloudTasksClient client,
        CloudTasksOptions options,
        string parent,
        string queueId,
        int attempt,
        CancellationToken ct)
    {
        var queue = new CloudTasksQueueResource
        {
            Name = new QueueName(options.ProjectId, options.LocationId, queueId).ToString(),
        };

        try
        {
            await client.CreateQueueAsync(parent, queue, ct);
            _logger.LogInformation("Created emulator queue {QueueId}.", queueId);
            return true;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
            return true;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            // Some emulator builds only serve -queue declared queues; retrying can't change that.
            _logger.LogWarning(
                "The Cloud Tasks emulator does not support creating queues, so {QueueId} must be "
                + "declared with a -queue flag on the emulator itself.", queueId);
            return true;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            LogAttemptFailed(ex, queueId, attempt);
            return false;
        }
    }

    private void LogAttemptFailed(Exception ex, string queueId, int attempt)
    {
        if (attempt < EscalateAfterAttempts)
            _logger.LogWarning(ex,
                "Failed to create emulator queue '{QueueId}' (attempt {Attempt}); retrying.", queueId, attempt);
        else
            _logger.LogError(ex,
                "Failed to create emulator queue '{QueueId}' on {Attempt} consecutive attempts; still "
                + "retrying. Is a Cloud Tasks emulator listening on {EmulatorEndpoint}?",
                queueId, attempt, _options.Value.EmulatorEndpoint);
    }

    // Equal jitter: half the delay plus a random share of the other half, so retries never bunch up.
    private static TimeSpan RetryDelay(int attempt)
    {
        var full = BaseDelayForAttempt(attempt);
        return full / 2 + Random.Shared.NextDouble() * (full / 2);
    }

    internal static TimeSpan BaseDelayForAttempt(int attempt)
    {
        // Cap the exponent first, or doubling overflows the ticks.
        var doublings = Math.Min(attempt - 1, 20);
        var scaled = BaseRetryDelay * Math.Pow(2, doublings);
        return scaled > MaxRetryDelay ? MaxRetryDelay : scaled;
    }
}
