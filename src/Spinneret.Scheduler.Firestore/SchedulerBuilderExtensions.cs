using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spinneret.Scheduler;
using Spinneret.Scheduler.Firestore;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Use* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection;

public static class FirestoreSchedulerBuilderExtensions
{
    /// <summary>
    /// Stores scheduled jobs in Firestore: provides <see cref="IRecurringJobScheduler"/>,
    /// <see cref="IFirestoreTransactionalScheduler"/> and the sweep engine. Works with any queue
    /// composition — due jobs are leased in Firestore, then enqueued.
    /// </summary>
    /// <remarks>
    /// Requires <c>AddQueue</c> (for the type registry and payload serializer the sweep dispatches
    /// through, in any registration order) and a host-registered <c>FirestoreDb</c>. The sweep runs
    /// only when something drives it: <c>s.UseInHostSweeper()</c> for a timer, or the endpoint from
    /// <c>Spinneret.Scheduler.AspNetCore</c> for an external cron. Configuration is read from the
    /// <c>Scheduler:Firestore</c> section.
    /// </remarks>
    public static SchedulerBuilder UseFirestoreStorage(this SchedulerBuilder scheduler, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(FirestoreSchedulerOptions.SectionName);
        return scheduler.UseFirestoreStorageCore(options => section.Bind(options));
    }

    /// <summary>As <see cref="UseFirestoreStorage(SchedulerBuilder, IConfiguration)"/>, configured in code.</summary>
    public static SchedulerBuilder UseFirestoreStorage(this SchedulerBuilder scheduler, Action<FirestoreSchedulerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return scheduler.UseFirestoreStorageCore(configure);
    }

    /// <summary>As <see cref="UseFirestoreStorage(SchedulerBuilder, IConfiguration)"/>, with the defaults.</summary>
    public static SchedulerBuilder UseFirestoreStorage(this SchedulerBuilder scheduler) =>
        scheduler.UseFirestoreStorageCore(_ => { });

    private static SchedulerBuilder UseFirestoreStorageCore(this SchedulerBuilder scheduler, Action<FirestoreSchedulerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(scheduler);

        return scheduler.UseStorage(new SchedulerStorageRole(nameof(UseFirestoreStorage), services =>
        {
            services.AddOptions<FirestoreSchedulerOptions>()
                .Configure(configure)
                .Validate(ValidateOptions, "Scheduler:Firestore options are invalid: Collection must be set and OneShotLeaseWindow positive.")
                .ValidateOnStart();

            // Normally already registered by the queue; added here so the order of AddQueue and
            // AddScheduler does not matter.
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<ScheduledJobDocumentFactory>();
            services.TryAddSingleton<IRecurringJobScheduler, FirestoreScheduler>();
            services.TryAddSingleton<IFirestoreTransactionalScheduler, FirestoreTransactionalScheduler>();
            // The sweep engine, not the trigger: registering it costs nothing until something drives
            // it, and the host chooses that separately with UseInHostSweeper() or an HTTP trigger.
            services.TryAddSingleton<ISchedulerSweep, FirestoreSchedulerDispatcher>();
        }));
    }

    private static bool ValidateOptions(FirestoreSchedulerOptions o) =>
        !string.IsNullOrWhiteSpace(o.Collection) && o.OneShotLeaseWindow > TimeSpan.Zero;
}
