using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Spinneret.Scheduler;

/// <summary>
/// Installs every registered <see cref="IRecurringJob"/> into the scheduler at startup, on the
/// schedule it declares, and removes every <see cref="IRetiredRecurringJob"/> key. The registration
/// is idempotent, so running on every instance and every deploy simply refreshes the job definitions
/// without creating duplicates or disturbing their cadence. A failure to install or retire one job
/// is logged and never blocks startup — the next instance or restart re-asserts it.
/// </summary>
internal sealed class RecurringJobInstaller(
    IRecurringJobScheduler scheduler,
    IEnumerable<IRecurringJob> jobs,
    IEnumerable<IRetiredRecurringJob> retired,
    ILogger<RecurringJobInstaller> logger) : IHostedService
{
    // Job keys are compared case-insensitively because not every store distinguishes them: a SQL
    // Server table under the usual case-insensitive collation treats 'Sync' and 'sync' as one row,
    // so a pair that coexists happily in Firestore would silently collapse into one job there.
    private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

    public async Task StartAsync(CancellationToken ct)
    {
        var declared = jobs.ToArray();
        var retirements = retired.ToArray();
        Validate(declared, retirements);

        foreach (var job in declared)
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

        foreach (var retirement in retirements)
        {
            try
            {
                await scheduler.UnregisterAsync(retirement.Key, ct);
                logger.LogInformation("Retired recurring job '{Key}'.", retirement.Key);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to retire recurring job '{Key}'.", retirement.Key);
            }
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Rejects contradictory registrations before touching the scheduler. Unlike an install failure
    /// — per-host, transient, and worth surviving — these are code bugs: identical on every instance,
    /// unfixable without a deploy, and silent, since the loser of a duplicated key is simply
    /// overwritten and never runs. Failing startup is the only way they get noticed.
    /// </summary>
    private static void Validate(IRecurringJob[] declared, IRetiredRecurringJob[] retirements)
    {
        var duplicates = declared.GroupBy(j => j.Key, KeyComparer).Where(g => g.Count() > 1).ToArray();
        if (duplicates.Length > 0)
            throw new InvalidOperationException(
                $"Recurring job keys must be unique, but {Quote(duplicates.Select(g => g.Key))} "
                + "identify more than one job each. Registering two jobs under one key installs only "
                + "the last of them; the others never run.");

        var contested = retirements.Select(r => r.Key)
            .Intersect(declared.Select(j => j.Key), KeyComparer).ToArray();
        if (contested.Length > 0)
            throw new InvalidOperationException(
                $"Recurring job keys {Quote(contested)} are both declared and retired. Retire a key "
                + "only after the job that declares it is gone, or the two cancel each other out on "
                + "every startup.");
    }

    private static string Quote(IEnumerable<string> keys) => string.Join(", ", keys.Select(k => $"'{k}'"));
}
