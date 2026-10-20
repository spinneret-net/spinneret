using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.Scheduler;

/// <summary>
/// Composes the scheduler a host runs, inside <c>services.AddScheduler(s => ...)</c>: where jobs are
/// stored (a provider's <c>Use*</c> extension, e.g. <c>UseMssql</c>, <c>UsePostgres</c> or <c>UseFirestore</c>) and,
/// optionally, a timer that dispatches them as they fall due (<see cref="UseSweeper"/>).
/// </summary>
/// <remarks>
/// Due jobs are enqueued onto the host's queue, so the scheduler needs <c>AddQueue</c> as well — in
/// either order. The storage determines how tightly a run's booking and its enqueue are bound: a SQL
/// scheduler commits both in one transaction with a SQL queue storage.
/// </remarks>
public sealed class SchedulerBuilder
{
    private Action<SchedulerOptions>? _sweeper;
    private bool _useSweeper;

    internal SchedulerBuilder(IServiceCollection services) => Services = services;

    /// <summary>The service collection the scheduler is registered into.</summary>
    public IServiceCollection Services { get; }

    internal SchedulerStorageRole? Storage { get; private set; }

    /// <summary>
    /// Runs the storage's sweep on a timer in this host, dispatching jobs as they fall due.
    /// </summary>
    /// <remarks>
    /// Opt-in per host, because a host that declares or schedules jobs is not necessarily one that
    /// should dispatch them; sweeps race safely across hosts, so every instance may run one. A host
    /// that scales to zero has no thread to tick: map <c>MapSchedulerSweep</c> from
    /// <c>Spinneret.Scheduler.AspNetCore</c> instead and drive it from an external cron. To bind the
    /// interval from configuration, call <c>services.Configure&lt;SchedulerOptions&gt;(section)</c> —
    /// this package deliberately takes no dependency on the configuration binder.
    /// </remarks>
    public SchedulerBuilder UseSweeper(Action<SchedulerOptions>? configure = null)
    {
        _useSweeper = true;
        _sweeper = configure;
        return this;
    }

    internal SchedulerBuilder UseStorage(SchedulerStorageRole role)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (Storage is not null)
            throw new InvalidOperationException(
                $"The scheduler's storage is already {Storage.Name}; it cannot also be {role.Name}. Choose one.");

        Storage = role;
        return this;
    }

    internal void Build()
    {
        if (Services.Any(d => d.ServiceType == typeof(SchedulerRegistered)))
            throw new InvalidOperationException(
                "AddScheduler was already called. A host has one scheduler; compose all of it in a single AddScheduler call.");

        if (Storage is null)
            throw new InvalidOperationException(
                "The scheduler has nowhere to keep its jobs. Choose a storage, e.g. s.UseMssql(...), s.UsePostgres(...) or s.UseFirestore(...).");

        Services.AddSingleton<SchedulerRegistered>();
        Storage.Register(Services);
        // After the storage: hosted services start in registration order, and the installer writes
        // job definitions into tables a storage's schema initializer creates. The installer retries,
        // so getting this order wrong would cost a backoff rather than the jobs.
        Services.AddHostedService<RecurringJobInstaller>();

        if (_useSweeper)
            AddSweeper(_sweeper);
    }

    private void AddSweeper(Action<SchedulerOptions>? configure)
    {
        var options = Services.AddOptions<SchedulerOptions>();
        if (configure is not null)
            options.Configure(configure);

        options
            .Validate(o => o.SweepInterval > TimeSpan.Zero,
                "Scheduler:Sweeper:SweepInterval must be positive — it is the sweep cadence and "
                + "the backoff a stalled provider relies on.")
            .ValidateOnStart();

        Services.AddHostedService<SchedulerSweeperService>();
    }

    /// <summary>Marks a collection AddScheduler has composed, so a second call fails instead of half-overriding the first.</summary>
    private sealed class SchedulerRegistered;
}

/// <summary>
/// Where the scheduler's jobs live. <paramref name="Register"/> adds the storage's
/// <see cref="IRecurringJobScheduler"/>, its <see cref="ISchedulerSweep"/> engine, its transactional
/// one-shot API and its schema initializer; the builder adds the installer and the optional timer.
/// </summary>
internal sealed record SchedulerStorageRole(string Name, Action<IServiceCollection> Register);
