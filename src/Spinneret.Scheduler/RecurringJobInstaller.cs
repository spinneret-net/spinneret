using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Spinneret.Scheduler;

// Retries until each job is installed, then stops: re-asserting on a timer would make two
// revisions overwrite each other's definitions during a rolling deploy.
internal sealed class RecurringJobInstaller : BackgroundService
{
    internal static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(5);

    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    internal const int EscalateAfterAttempts = 5;

    // Case-insensitive: SQL Server's usual collation treats 'Sync' and 'sync' as one row.
    private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

    private readonly IRecurringJobScheduler _scheduler;
    private readonly IRecurringJob[] _jobs;
    private readonly IRetiredRecurringJob[] _retired;
    private readonly ILogger<RecurringJobInstaller> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public RecurringJobInstaller(
        IRecurringJobScheduler scheduler,
        IEnumerable<IRecurringJob> jobs,
        IEnumerable<IRetiredRecurringJob> retired,
        ILogger<RecurringJobInstaller> logger)
        : this(scheduler, jobs, retired, logger, (delay, ct) => Task.Delay(delay, ct))
    {
    }

    internal RecurringJobInstaller(
        IRecurringJobScheduler scheduler,
        IEnumerable<IRecurringJob> jobs,
        IEnumerable<IRetiredRecurringJob> retired,
        ILogger<RecurringJobInstaller> logger,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(retired);

        _scheduler = scheduler;
        _jobs = jobs.ToArray();
        _retired = retired.ToArray();
        _logger = logger;
        _delay = delay;
    }

    // Validation fails startup (code bugs); installing runs in the background so an unreachable store doesn't.
    public override Task StartAsync(CancellationToken ct)
    {
        Validate(_jobs, _retired);
        return base.StartAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var pendingJobs = new List<IRecurringJob>(_jobs);
        var pendingRetirements = new List<IRetiredRecurringJob>(_retired);

        for (var attempt = 1; !ct.IsCancellationRequested; attempt++)
        {
            var failedJobs = new List<IRecurringJob>();
            foreach (var job in pendingJobs)
                if (!await TryInstallAsync(job, attempt, ct))
                    failedJobs.Add(job);
            pendingJobs = failedJobs;

            var failedRetirements = new List<IRetiredRecurringJob>();
            foreach (var retirement in pendingRetirements)
                if (!await TryRetireAsync(retirement, attempt, ct))
                    failedRetirements.Add(retirement);
            pendingRetirements = failedRetirements;

            if (pendingJobs.Count == 0 && pendingRetirements.Count == 0)
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

    private async Task<bool> TryInstallAsync(IRecurringJob job, int attempt, CancellationToken ct)
    {
        try
        {
            // Inside the try: a schedule built from configuration can throw.
            var schedule = job.Schedule;
            await job.Register(_scheduler, schedule, ct);
            LogInstalled("Installed recurring job '{Key}' ({Schedule})", job.Key, attempt, schedule);
            return true;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            LogAttemptFailed(ex, "install recurring job", job.Key, attempt);
            return false;
        }
    }

    private async Task<bool> TryRetireAsync(IRetiredRecurringJob retirement, int attempt, CancellationToken ct)
    {
        try
        {
            await _scheduler.UnregisterAsync(retirement.Key, ct);
            LogInstalled("Retired recurring job '{Key}'", retirement.Key, attempt, schedule: null);
            return true;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            LogAttemptFailed(ex, "retire recurring job", retirement.Key, attempt);
            return false;
        }
    }

    private void LogInstalled(string message, string key, int attempt, Schedule? schedule)
    {
        if (attempt == 1)
            _logger.LogInformation(message + ".", key, schedule);
        else
            _logger.LogInformation(message + " after {Attempts} attempts.", key, schedule, attempt);
    }

    private void LogAttemptFailed(Exception ex, string what, string key, int attempt)
    {
        // Persistent failures won't resolve on their own, so escalate them to errors.
        if (attempt < EscalateAfterAttempts)
            _logger.LogWarning(ex, "Failed to {What} '{Key}' (attempt {Attempt}); retrying.", what, key, attempt);
        else
            _logger.LogError(ex,
                "Failed to {What} '{Key}' on {Attempt} consecutive attempts; still retrying.", what, key, attempt);
    }

    // Equal jitter keeps instances from retrying in lockstep after a shared outage.
    private static TimeSpan RetryDelay(int attempt)
    {
        var full = BaseDelayForAttempt(attempt);
        return full / 2 + Random.Shared.NextDouble() * (full / 2);
    }

    internal static TimeSpan BaseDelayForAttempt(int attempt)
    {
        // Cap the exponent first; doubling would otherwise overflow.
        var doublings = Math.Min(attempt - 1, 20);
        var scaled = BaseRetryDelay * Math.Pow(2, doublings);
        return scaled > MaxRetryDelay ? MaxRetryDelay : scaled;
    }

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
