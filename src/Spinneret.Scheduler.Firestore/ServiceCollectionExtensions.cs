using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Spinneret.Queue;
using Spinneret.Scheduler;
using Spinneret.Scheduler.Firestore;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Add* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection;

public static class FirestoreSchedulerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Firestore-backed scheduler: <see cref="IRecurringJobScheduler"/>,
    /// <see cref="IFirestoreTransactionalScheduler"/> and the dispatch sweeper.
    /// </summary>
    /// <remarks>
    /// Requires a queue transport to be registered (for the type registry and payload
    /// serializer the sweep dispatches through, in any registration order) and a host-registered
    /// <c>FirestoreDb</c>. This registers the sweep engine but no trigger — add
    /// <c>AddSchedulerSweeper()</c> for a timer, or map the endpoint from
    /// <c>Spinneret.Scheduler.Http</c> to be driven externally.
    /// Configuration is read from the <c>Scheduler:Firestore</c> section.
    /// </remarks>
    public static IServiceCollection AddFirestoreScheduler(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(FirestoreSchedulerOptions.SectionName);
        return services.AddFirestoreSchedulerCore(options => section.Bind(options));
    }

    /// <summary>
    /// Overload for hosts that configure the scheduler in code instead of via
    /// <see cref="IConfiguration"/> (tests, embedded scenarios).
    /// </summary>
    public static IServiceCollection AddFirestoreScheduler(
        this IServiceCollection services,
        Action<FirestoreSchedulerOptions> configure)
    {
        return services.AddFirestoreSchedulerCore(configure);
    }

    private static IServiceCollection AddFirestoreSchedulerCore(
        this IServiceCollection services,
        Action<FirestoreSchedulerOptions> configure)
    {
        // No "was the queue registered first?" guard: this method registers lazily and reads nothing
        // from the collection, so it composes in any order. A genuinely missing transport surfaces
        // when the container resolves the sweep and names the service it could not supply.
        services.AddOptions<FirestoreSchedulerOptions>()
            .Configure(configure)
            .Validate(ValidateOptions, "Scheduler:Firestore options are invalid: Collection must be set and OneShotLeaseWindow positive.")
            .ValidateOnStart();

        // Normally already registered by the queue transport; added here so this package does not
        // depend on that ordering.
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ScheduledJobDocumentFactory>();
        services.TryAddSingleton<IRecurringJobScheduler, FirestoreScheduler>();
        services.TryAddSingleton<IFirestoreTransactionalScheduler, FirestoreTransactionalScheduler>();
        // The sweep engine, not the trigger: registering it costs nothing until something drives it,
        // and the host chooses that separately with AddSchedulerSweeper() or an HTTP trigger.
        services.TryAddSingleton<ISchedulerSweep, FirestoreSchedulerDispatcher>();
        services.AddRecurringJobInstaller();
        return services;
    }

    private static bool ValidateOptions(FirestoreSchedulerOptions o) =>
        !string.IsNullOrWhiteSpace(o.Collection) && o.OneShotLeaseWindow > TimeSpan.Zero;
}
