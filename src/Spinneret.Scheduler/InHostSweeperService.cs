using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Spinneret.Scheduler;

internal sealed class InHostSweeperService(
    IServiceScopeFactory scopeFactory,
    IOptions<SchedulerOptions> options,
    ILogger<InHostSweeperService> logger)
    : BackgroundService
{
    public override Task StartAsync(CancellationToken ct)
    {
        // Without a storage the sweeper would be a silent no-op that looks healthy.
        using var scope = scopeFactory.CreateScope();
        if (scope.ServiceProvider.GetService<ISchedulerSweep>() is null)
            throw new InvalidOperationException(
                "UseInHostSweeper requires a scheduler storage to be registered "
                + "(s.UseMssqlStorage, s.UsePostgresStorage, s.UseFirestoreStorage, or another): the sweeper is only a "
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
                // Never fatal: the next tick tries again.
                logger.LogError(ex, "Scheduler sweep failed; next sweep continues");
            }

            try
            {
                // Load-bearing: a provider that cannot make progress returns, and this stops a hot loop.
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
