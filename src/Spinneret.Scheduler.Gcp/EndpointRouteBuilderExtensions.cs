using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Spinneret.Queue;
using Spinneret.Scheduler.Gcp;

// ReSharper disable once CheckNamespace — deliberate: endpoint extensions live in the
// builder namespace so Map* calls are discoverable without a using directive.
namespace Microsoft.AspNetCore.Builder;

public static class GcpSchedulerEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the OIDC-protected Cloud Scheduler sweep endpoint. Map this only on hosts that
    /// dispatch scheduled jobs; those hosts must have registered a <c>FirestoreDb</c> and an
    /// <see cref="IDeadLetterWriter"/>.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="pattern">Route for the endpoint. Must match the Cloud Scheduler job's target path.</param>
    public static IEndpointRouteBuilder MapGcpSchedulerDispatch(
        this IEndpointRouteBuilder endpoints,
        string pattern = SchedulerDispatchEndpoint.DefaultRoutePattern)
    {
        // Both are exercised only on dispatch/failure paths, in production, possibly long after
        // deploy — so their absence must fail here, not there.
        if (endpoints.ServiceProvider.GetService<FirestoreDb>() is null)
            throw new InvalidOperationException(
                "MapGcpSchedulerDispatch requires a FirestoreDb registration. " +
                "Register one before building the app — the GCP scheduler stores its jobs in Firestore.");

        if (endpoints.ServiceProvider.GetService<IDeadLetterWriter>() is null)
            throw new InvalidOperationException(
                "MapGcpSchedulerDispatch requires an IDeadLetterWriter registration. " +
                "Register one before building the app — the GCP packages do not ship a default.");

        return SchedulerDispatchEndpoint.MapGcpSchedulerDispatch(endpoints, pattern);
    }
}
