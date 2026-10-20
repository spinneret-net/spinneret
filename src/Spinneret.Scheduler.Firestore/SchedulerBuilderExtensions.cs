using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spinneret.Scheduler;
using Spinneret.Scheduler.Firestore;

// ReSharper disable once CheckNamespace — DI namespace for discoverability.
namespace Microsoft.Extensions.DependencyInjection;

public static class FirestoreSchedulerBuilderExtensions
{
    /// <summary>
    /// Stores scheduled jobs in Firestore, configured from <c>Scheduler:Firestore</c>; works with any queue.
    /// </summary>
    /// <remarks>
    /// Requires <c>AddQueue</c> and a registered <c>FirestoreDb</c>. The sweep runs only when driven,
    /// by <c>s.UseInHostSweeper()</c> or the <c>Spinneret.Scheduler.AspNetCore</c> endpoint.
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

            // Also registered by the queue; added so AddQueue/AddScheduler order doesn't matter.
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<ScheduledJobDocumentFactory>();
            services.TryAddSingleton<IRecurringJobScheduler, FirestoreScheduler>();
            services.TryAddSingleton<IFirestoreTransactionalScheduler, FirestoreTransactionalScheduler>();
            services.TryAddSingleton<ISchedulerSweep, FirestoreSchedulerDispatcher>();
        }));
    }

    private static bool ValidateOptions(FirestoreSchedulerOptions o) =>
        !string.IsNullOrWhiteSpace(o.Collection) && o.OneShotLeaseWindow > TimeSpan.Zero;
}
