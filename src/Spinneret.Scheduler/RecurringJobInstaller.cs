using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Spinneret.Scheduler;

/// <summary>
/// Installs every registered <see cref="IRecurringJob"/> into the scheduler at startup, on the
/// schedule it declares. The registration is idempotent, so running on every instance and every
/// deploy simply refreshes the job definitions without creating duplicates or disturbing their
/// cadence. A failure to install one job is logged and never blocks startup — the next instance or
/// restart re-asserts it.
/// </summary>
internal sealed class RecurringJobInstaller(
    IRecurringJobScheduler scheduler,
    IEnumerable<IRecurringJob> jobs,
    ILogger<RecurringJobInstaller> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        foreach (var job in jobs)
        {
            try
            {
                // Inside the try: a job is free to build its schedule from configuration, so reading
                // the property is as capable of throwing as registering is.
                var schedule = job.Schedule;
                await scheduler.RegisterAsync(job.Key, job.CreateRequest(), schedule, ct);
                logger.LogInformation("Installed recurring job '{Key}' ({Schedule}).", job.Key, schedule);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to install recurring job '{Key}'.", job.Key);
            }
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
