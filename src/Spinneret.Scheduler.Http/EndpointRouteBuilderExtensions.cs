using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Spinneret.Scheduler;
using Spinneret.Scheduler.Http;

// ReSharper disable once CheckNamespace — deliberate: endpoint extensions live in the
// builder namespace so Map* calls are discoverable without a using directive.
namespace Microsoft.AspNetCore.Builder;

public static class SchedulerSweepEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps an endpoint that runs one scheduler sweep per request, guarded by
    /// <paramref name="authorizationPolicy"/>. The counterpart to <c>AddSchedulerSweeper()</c>:
    /// use this where an external scheduler owns the clock — notably on a host that scales to
    /// zero, which has no thread of its own to tick.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="authorizationPolicy">
    /// Name of the authorization policy protecting the endpoint. The sweep dispatches every due
    /// job, so it must never be reachable unauthenticated. Hosts on Cloud Tasks pass
    /// <c>OidcAuthSetup.PolicyName</c> to reuse the queue's OIDC scheme; any other host passes its
    /// own policy. Required rather than defaulted, because this package cannot know what
    /// authenticates the trigger — and guessing wrong would silently leave the sweep open.
    /// </param>
    /// <param name="pattern">
    /// Route for the endpoint; must match the scheduler trigger's target path. Defaults to
    /// <c>/internal/scheduler/sweep</c>. Resolved here rather than declared as the parameter's
    /// default value, because an optional-parameter default is copied into the calling assembly —
    /// changing it later would move the route only for consumers who happened to recompile.
    /// </param>
    public static IEndpointRouteBuilder MapSchedulerSweep(
        this IEndpointRouteBuilder endpoints,
        string authorizationPolicy,
        string? pattern = null)
    {
        if (string.IsNullOrWhiteSpace(authorizationPolicy))
            throw new ArgumentException(
                "An authorization policy name is required: the sweep endpoint dispatches every due job.",
                nameof(authorizationPolicy));

        // Exercised only when the trigger fires — in production, possibly long after deploy — so a
        // missing scheduler must fail here rather than 500 on the first tick.
        if (endpoints.ServiceProvider.GetService<ISchedulerSweep>() is null)
            throw new InvalidOperationException(
                "MapSchedulerSweep requires a scheduler storage provider to be registered "
                + "(AddFirestoreScheduler, AddMssqlScheduler, or another): this package only exposes "
                + "the trigger, and something has to tell it what is due.");

        return SchedulerSweepEndpoint.MapSchedulerSweep(
            endpoints, authorizationPolicy, pattern ?? SchedulerSweepEndpoint.DefaultRoutePattern);
    }
}
