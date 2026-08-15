using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Spinneret.Scheduler;

/// <summary>
/// Drives <see cref="ISchedulerSweep"/> on a timer. The clock, and nothing else: where jobs are
/// stored and how a pass claims them belongs to the provider behind the interface.
/// </summary>
/// <remarks>
/// Suits a host that is always running. A host that scales to zero has no thread to tick, so it
/// wants the HTTP trigger in <c>Spinneret.Scheduler.Http</c> and an external cron instead — same
/// sweep, different clock.
/// </remarks>
internal sealed class SchedulerSweeperService(
    IServiceScopeFactory scopeFactory,
    IOptions<SchedulerOptions> options,
    ILogger<SchedulerSweeperService> logger)
    : BackgroundService
{
    public override Task StartAsync(CancellationToken ct)
    {
        // A sweeper with nothing to sweep is a silent no-op that looks healthy — fail on the
        // startup path instead, the way the recurring-job installer validates its declarations.
        using var scope = scopeFactory.CreateScope();
        if (scope.ServiceProvider.GetService<ISchedulerSweep>() is null)
            throw new InvalidOperationException(
                "AddSchedulerSweeper requires a scheduler storage provider to be registered "
                + "(AddFirestoreScheduler, AddMssqlScheduler, or another): the sweeper is only a "
                + "clock, and something has to tell it what is due.");

        return base.StartAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.SweepInterval;
        logger.LogInformation("Scheduler sweep started (every {SweepInterval})", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A fresh scope per sweep. Both in-tree providers register a singleton, so today this
                // resolves the same instance every tick — it is here so a provider that needs
                // per-pass state has somewhere to put it, not because anything currently relies on it.
                using var scope = scopeFactory.CreateScope();
                var result = await scope.ServiceProvider
                    .GetRequiredService<ISchedulerSweep>().SweepAsync(stoppingToken);

                if (result.JobsDispatched > 0)
                    logger.LogInformation("Scheduler sweep dispatched {JobsDispatched} job(s)", result.JobsDispatched);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The sweep itself failed — a store still starting up alongside this host, a
                // transient outage. Never fatal: the next tick tries again.
                logger.LogError(ex, "Scheduler sweep failed; next sweep continues");
            }

            try
            {
                // Load-bearing, not merely a schedule. A provider that cannot make progress returns
                // instead of spinning, and this delay is what stops that becoming a hot loop.
                // Awaiting it also means this loop never overlaps its own sweeps — but that is not a
                // guarantee providers may rely on, since an HTTP trigger can call the same sweep at
                // any moment, including mid-tick.
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Scheduler sweep stopped");
    }
}
