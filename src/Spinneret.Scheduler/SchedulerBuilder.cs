using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.Scheduler;

/// <summary>
/// Composes the scheduler inside <c>services.AddScheduler(s => ...)</c>: a storage and, optionally, an in-host sweeper.
/// </summary>
/// <remarks>
/// Due jobs are enqueued onto the host's queue, so <c>AddQueue</c> is also required, in either order.
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
    /// Sweeps race safely, so every instance may run one. A host that scales to zero should map
    /// <c>MapSchedulerSweep</c> from <c>Spinneret.Scheduler.AspNetCore</c> instead.
    /// </remarks>
    public SchedulerBuilder UseInHostSweeper(Action<SchedulerOptions>? configure = null)
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
                "The scheduler has nowhere to keep its jobs. Choose a storage, e.g. s.UseMssqlStorage(...), s.UsePostgresStorage(...) or s.UseFirestoreStorage(...).");

        Services.AddSingleton<SchedulerRegistered>();
        Storage.Register(Services);
        // After the storage: hosted services start in order, and the schema initializer creates the tables.
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

        Services.AddHostedService<InHostSweeperService>();
    }

    private sealed class SchedulerRegistered;
}

internal sealed record SchedulerStorageRole(string Name, Action<IServiceCollection> Register);
