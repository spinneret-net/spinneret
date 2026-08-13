using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Spinneret.Queue;
using Spinneret.Queue.Gcp;

// ReSharper disable once CheckNamespace — deliberate: endpoint extensions live in the
// builder namespace so Map* calls are discoverable without a using directive.
namespace Microsoft.AspNetCore.Builder;

public static class GcpQueueEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the OIDC-protected Cloud Tasks dispatch endpoint. Map this only on hosts that
    /// consume the queue; those hosts must have registered an <see cref="IDeadLetterWriter"/>.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="pattern">Route for the endpoint. Must match <c>Queue:Gcp:DispatcherUrl</c>'s path.</param>
    public static IEndpointRouteBuilder MapGcpQueueDispatch(
        this IEndpointRouteBuilder endpoints,
        string pattern = QueueDispatchEndpoint.DefaultRoutePattern)
    {
        // The dead-letter writer is only exercised on a failure path, in production, possibly
        // weeks after deploy — so its absence must fail here, not there.
        if (endpoints.ServiceProvider.GetService<IDeadLetterWriter>() is null)
            throw new InvalidOperationException(
                "MapGcpQueueDispatch requires an IDeadLetterWriter registration. " +
                "Register one before building the app — the GCP transport does not ship a default.");

        return QueueDispatchEndpoint.MapGcpQueueDispatch(endpoints, pattern);
    }
}
