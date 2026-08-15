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
/// Creates the configured channels' queues on the Cloud Tasks emulator, the way the MSSQL transport
/// creates its tables. Production queues are owned by infrastructure-as-code and are never touched:
/// this runs only when <see cref="GcpQueueOptions.EmulatorEndpoint"/> is set.
/// </summary>
/// <remarks>
/// <para>
/// Without it, every emulator queue has to be declared twice — once in
/// <see cref="GcpQueueOptions.Channels"/> and again as a <c>-queue</c> flag on the emulator — and the
/// two drift silently: enqueueing to a queue the emulator never declared fails at the first task,
/// not at boot.
/// </para>
/// <para>
/// The work runs in the background and retries with capped, jittered backoff, so an emulator still
/// coming up alongside the host — or started minutes later — still gets its queues. It never fails
/// the host, deliberately: what it creates exists only on a developer's machine, which makes it the
/// last thing in this library worth stopping an application over, and the hosts that boot with an
/// emulator configured include test hosts that have none and no natural place to say so. A queue
/// that never gets created is loud twice anyway — here at Error, and at the first enqueue, which
/// names it.
/// </para>
/// <para>
/// Takes an <see cref="IServiceProvider"/> rather than a <see cref="CloudTasksClient"/>: the host
/// constructs every hosted service at start, so a client injected here would be built on production
/// hosts too — and building one resolves Application Default Credentials, which a host that only
/// receives dispatches has no reason to hold. Resolving after the emulator check keeps that lazy.
/// </para>
/// </remarks>
internal sealed class EmulatorQueueInitializer : BackgroundService
{
    /// <summary>
    /// Delay before the second attempt; doubles per attempt up to <see cref="MaxRetryDelay"/>. Both
    /// are far shorter than the scheduler's job installer uses for the same shape of loop, because
    /// what this waits for is a container on the same machine, watched by whoever just started it.
    /// </summary>
    internal static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Ceiling on the backoff. Retrying is indefinite — giving up would leave the queues missing
    /// until the next restart, when the emulator being waited for is often seconds away — so the cap
    /// is what bounds a permanently absent emulator to about one log line a minute.
    /// </summary>
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Attempt at which a failure stops looking like a container still starting and starts being
    /// logged as an error.
    /// </summary>
    internal const int EscalateAfterAttempts = 5;

    private readonly IServiceProvider _services;
    private readonly IOptions<GcpQueueOptions> _options;
    private readonly ILogger<EmulatorQueueInitializer> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public EmulatorQueueInitializer(
        IServiceProvider services,
        IOptions<GcpQueueOptions> options,
        ILogger<EmulatorQueueInitializer> logger)
        : this(services, options, logger, (delay, ct) => Task.Delay(delay, ct))
    {
    }

    /// <summary>Test seam: lets a test drive the retry loop without waiting out the backoff.</summary>
    internal EmulatorQueueInitializer(
        IServiceProvider services,
        IOptions<GcpQueueOptions> options,
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
            // An unhandled exception out of a BackgroundService stops the host by default, which is
            // the one outcome this must never cause. The retry loop already absorbs everything the
            // emulator can answer with, so reaching here means the client itself could not be built.
            _logger.LogError(ex,
                "Emulator queue creation stopped. Declare the queues with -queue flags on the "
                + "emulator, or restart the host once it can be reached.");
        }
    }

    private async Task CreateQueues(GcpQueueOptions options, CancellationToken ct)
    {
        var client = _services.GetRequiredService<CloudTasksClient>();
        // CreateQueue's parent resource path. Spelled out because Google.Cloud.Tasks.V2 ships a
        // QueueName helper but no LocationName.
        var parent = $"projects/{options.ProjectId}/locations/{options.LocationId}";
        var pending = options.Channels.Values.Distinct(StringComparer.Ordinal).ToList();

        for (var attempt = 1; !ct.IsCancellationRequested; attempt++)
        {
            // Every queue still pending gets one attempt, then they share a single backoff. Per-queue
            // budgets would multiply the wait by the number of channels while an emulator starts.
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
                return; // The host is shutting down; the next startup asserts what is left.
            }
        }
    }

    private async Task<bool> TryCreateQueue(
        CloudTasksClient client,
        GcpQueueOptions options,
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
            // Some emulator builds only serve queues declared up front via -queue flags. Retrying
            // cannot change that answer, so this counts as settled rather than as a failure.
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
        // A failure that outlives a few attempts is no longer plausibly a container still starting:
        // the emulator is not running, or is not where EmulatorEndpoint says it is.
        if (attempt < EscalateAfterAttempts)
            _logger.LogWarning(ex,
                "Failed to create emulator queue '{QueueId}' (attempt {Attempt}); retrying.", queueId, attempt);
        else
            _logger.LogError(ex,
                "Failed to create emulator queue '{QueueId}' on {Attempt} consecutive attempts; still "
                + "retrying. Is a Cloud Tasks emulator listening on {EmulatorEndpoint}?",
                queueId, attempt, _options.Value.EmulatorEndpoint);
    }

    /// <summary>
    /// Exponential backoff, capped, with equal jitter — half the computed delay plus a random share
    /// of the other half, as the scheduler's job installer does. The halving keeps a retry from ever
    /// landing arbitrarily close to the previous one.
    /// </summary>
    private static TimeSpan RetryDelay(int attempt)
    {
        var full = BaseDelayForAttempt(attempt);
        return full / 2 + Random.Shared.NextDouble() * (full / 2);
    }

    /// <summary>The un-jittered ceiling for <paramref name="attempt"/>; jitter is applied on top.</summary>
    internal static TimeSpan BaseDelayForAttempt(int attempt)
    {
        // Doubling in ticks would overflow long before the cap matters, so cap the exponent first.
        var doublings = Math.Min(attempt - 1, 20);
        var scaled = BaseRetryDelay * Math.Pow(2, doublings);
        return scaled > MaxRetryDelay ? MaxRetryDelay : scaled;
    }
}
