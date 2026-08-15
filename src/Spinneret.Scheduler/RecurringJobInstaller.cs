using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Spinneret.Scheduler;

/// <summary>
/// Installs every registered <see cref="IRecurringJob"/> into the scheduler on the schedule it
/// declares, and removes every <see cref="IRetiredRecurringJob"/> key. Registration is idempotent,
/// so every instance asserting the same job on every deploy converges on one job definition without
/// duplicates or a disturbed cadence.
/// </summary>
/// <remarks>
/// <para>
/// Work runs in the background and retries with capped, jittered backoff until each job is
/// installed, so a store that is briefly unreachable at startup costs a short delay rather than a
/// missing job. A single always-on host would otherwise carry that gap until its next restart,
/// which may be months away.
/// </para>
/// <para>
/// Each job stops being retried the moment its own registration succeeds, and the loop ends once
/// nothing is left — this is a retry, not a reconciliation loop. Re-asserting on a timer would make
/// the instances of two revisions overwrite each other's definitions for the length of every
/// rolling deploy. Instances need no coordination beyond that: registration is idempotent, so an
/// instance whose attempt failed simply succeeds on a later one, whether or not another instance
/// got there first.
/// </para>
/// </remarks>
internal sealed class RecurringJobInstaller : BackgroundService
{
    /// <summary>Delay before the second attempt; doubles per attempt up to <see cref="MaxRetryDelay"/>.</summary>
    internal static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Ceiling on the backoff. Retrying is indefinite — giving up would strand the job until the
    /// next restart — so the cap is what bounds the load a permanently failing job puts on the store.
    /// </summary>
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Attempt at which a failure stops looking transient and starts being logged as an error. Below
    /// it a warning keeps a routine startup blip from paging anyone.
    /// </summary>
    internal const int EscalateAfterAttempts = 5;

    // Job keys are compared case-insensitively because not every store distinguishes them: a SQL
    // Server table under the usual case-insensitive collation treats 'Sync' and 'sync' as one row,
    // so a pair that coexists happily in Firestore would silently collapse into one job there.
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

    /// <summary>Test seam: lets a test drive the retry loop without waiting out the backoff.</summary>
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

    /// <summary>
    /// Validates before the host starts, then hands the installing itself to the background. The
    /// validation is deliberately on the startup path — it reports code bugs, which should stop a
    /// deploy — while the retrying is deliberately off it, so an unreachable store delays jobs
    /// instead of holding the whole application down.
    /// </summary>
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
                return; // The host is shutting down; the next startup re-asserts what is left.
            }
        }
    }

    private async Task<bool> TryInstallAsync(IRecurringJob job, int attempt, CancellationToken ct)
    {
        try
        {
            // Inside the try: a job is free to build its schedule from configuration, so reading
            // the property is as capable of throwing as registering is.
            var schedule = job.Schedule;
            // The job registers itself: it is the only thing that still knows its request's response
            // type, which is what keeps the scheduler's signature typed rather than untyped.
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
        // A failure that outlives a few attempts is no longer plausibly a blip: it is a request type
        // that was never registered with the queue, an unparseable schedule in this environment's
        // configuration, or a store this instance cannot reach. Those never resolve on their own, so
        // they have to become loud enough to alert on.
        if (attempt < EscalateAfterAttempts)
            _logger.LogWarning(ex, "Failed to {What} '{Key}' (attempt {Attempt}); retrying.", what, key, attempt);
        else
            _logger.LogError(ex,
                "Failed to {What} '{Key}' on {Attempt} consecutive attempts; still retrying.", what, key, attempt);
    }

    /// <summary>
    /// Exponential backoff, capped, with equal jitter — half the computed delay plus a random share
    /// of the other half. The jitter is what keeps many instances from retrying in lockstep after a
    /// shared outage and hammering the store in synchronised waves; the halving keeps a retry from
    /// ever landing arbitrarily close to the previous one.
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

    /// <summary>
    /// Rejects contradictory registrations before touching the scheduler. Unlike an install failure
    /// — per-host, transient, and worth retrying — these are code bugs: identical on every instance,
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
