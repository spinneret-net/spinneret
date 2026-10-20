using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Spinneret.Scheduler;
using Spinneret.Scheduler.AspNetCore;

// ReSharper disable once CheckNamespace — builder namespace for discoverability.
namespace Microsoft.AspNetCore.Builder;

public static class SchedulerSweepEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps an authorized endpoint that runs one scheduler sweep per request, for hosts driven by an external cron.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="authorizationPolicy">
    /// Policy protecting the endpoint, e.g. <c>CloudTasksAuth.PolicyName</c>; required so the sweep is never left open.
    /// </param>
    /// <param name="pattern">Route for the endpoint; defaults to <c>/internal/scheduler/sweep</c>.</param>
    public static IEndpointRouteBuilder MapSchedulerSweep(
        this IEndpointRouteBuilder endpoints,
        string authorizationPolicy,
        string? pattern = null)
    {
        if (string.IsNullOrWhiteSpace(authorizationPolicy))
            throw new ArgumentException(
                "An authorization policy name is required: the sweep endpoint dispatches every due job.",
                nameof(authorizationPolicy));

        // Fail at startup rather than 500 on the first tick, possibly long after deploy.
        if (endpoints.ServiceProvider.GetService<ISchedulerSweep>() is null)
            throw new InvalidOperationException(
                "MapSchedulerSweep requires a scheduler storage provider to be registered "
                + "(AddScheduler with s.UseMssqlStorage, s.UsePostgresStorage, s.UseFirestoreStorage, or another): this package only exposes "
                + "the trigger, and something has to tell it what is due.");

        // Default resolved here, not as a parameter default, which would be baked into callers.
        return SchedulerSweepEndpoint.Map(
            endpoints, authorizationPolicy, pattern ?? SchedulerSweepEndpoint.DefaultRoutePattern);
    }
}
