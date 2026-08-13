using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Spinneret.Queue;
using Spinneret.Scheduler;
using Spinneret.Scheduler.Gcp;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Add* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection;

public static class GcpSchedulerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Firestore-backed scheduler: <see cref="IRecurringJobScheduler"/>,
    /// <see cref="IFirestoreTransactionalScheduler"/>, the dispatch sweeper, and reuses the
    /// queue's OIDC policy for the dispatch endpoint.
    /// </summary>
    /// <remarks>
    /// Requires <c>AddGcpQueue</c> to be called first (for OIDC auth and type registry), and a
    /// host-registered <c>FirestoreDb</c>. Call <c>endpoints.MapGcpSchedulerDispatch()</c> to
    /// expose the sweeper endpoint. Configuration is read from the <c>Scheduler:Gcp</c> section.
    /// </remarks>
    public static IServiceCollection AddGcpScheduler(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(GcpSchedulerOptions.SectionName);
        return services.AddGcpSchedulerCore(options => section.Bind(options));
    }

    /// <summary>
    /// Overload for hosts that configure the scheduler in code instead of via
    /// <see cref="IConfiguration"/> (tests, embedded scenarios).
    /// </summary>
    public static IServiceCollection AddGcpScheduler(
        this IServiceCollection services,
        Action<GcpSchedulerOptions> configure)
    {
        return services.AddGcpSchedulerCore(configure);
    }

    private static IServiceCollection AddGcpSchedulerCore(
        this IServiceCollection services,
        Action<GcpSchedulerOptions> configure)
    {
        // Same precondition style as the MSSQL scheduler: fail at registration, where the fix is
        // one line away, instead of at the first job dispatch.
        if (services.All(d => d.ServiceType != typeof(QueueTypeRegistry)))
            throw new InvalidOperationException(
                "AddGcpScheduler requires AddGcpQueue to be called first: the scheduler dispatches "
                + "onto the queue and reuses its OIDC policy and type registry.");

        services.AddOptions<GcpSchedulerOptions>()
            .Configure(configure)
            .Validate(ValidateOptions, "Scheduler:Gcp options are invalid: Collection must be set and OneShotLeaseWindow positive.")
            .ValidateOnStart();

        services.TryAddSingleton<ScheduledJobDocumentFactory>();
        services.TryAddSingleton<IRecurringJobScheduler, FirestoreScheduler>();
        services.TryAddSingleton<IFirestoreTransactionalScheduler, FirestoreTransactionalScheduler>();
        services.TryAddSingleton<GcpSchedulerDispatcher>();
        services.AddRecurringJobInstaller();
        return services;
    }

    private static bool ValidateOptions(GcpSchedulerOptions o) =>
        !string.IsNullOrWhiteSpace(o.Collection) && o.OneShotLeaseWindow > TimeSpan.Zero;
}
