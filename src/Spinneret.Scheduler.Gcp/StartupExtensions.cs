using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Spinneret.Scheduler.Gcp;

public static class StartupExtensions
{
    /// <summary>
    /// Registers the Firestore-backed scheduler: <see cref="IRecurringJobScheduler"/>,
    /// <see cref="IFirestoreTransactionalScheduler"/>, the dispatch sweeper, and reuses the
    /// queue's OIDC policy for the dispatch endpoint.
    /// </summary>
    /// <remarks>
    /// Requires <c>AddGcpQueue</c> to be called first (for OIDC auth and type registry).
    /// Call <c>endpoints.MapGcpSchedulerDispatch()</c> to expose the sweeper endpoint.
    /// </remarks>
    public static IServiceCollection AddGcpScheduler(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<GcpSchedulerOptions>(configuration.GetSection(GcpSchedulerOptions.SectionName));
        services.TryAddSingleton<ScheduledJobDocumentFactory>();
        services.TryAddSingleton<IRecurringJobScheduler, FirestoreScheduler>();
        services.TryAddSingleton<IFirestoreTransactionalScheduler, FirestoreTransactionalScheduler>();
        services.TryAddSingleton<GcpSchedulerDispatcher>();
        services.AddRecurringJobInstaller();
        return services;
    }

    public static IEndpointRouteBuilder MapGcpSchedulerDispatch(this IEndpointRouteBuilder endpoints)
        => SchedulerDispatchEndpoint.MapGcpSchedulerDispatch(endpoints);
}
