using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Spinneret.Queue;
using Spinneret.Queue.Gcp;

// ReSharper disable once CheckNamespace — deliberate: endpoint extensions live in the
// builder namespace so Map* calls are discoverable without a using directive.
namespace Microsoft.AspNetCore.Builder;

public static class GcpQueueEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the OIDC-protected Cloud Tasks dispatch endpoint at the path of
    /// <c>Queue:Gcp:DispatcherUrl</c>. Map this only on hosts that consume the queue; those hosts
    /// must have registered an <see cref="IDeadLetterWriter"/>.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="pattern">
    /// Route for the endpoint. Defaults to the path of <c>Queue:Gcp:DispatcherUrl</c>, which is the
    /// address Cloud Tasks actually posts to — so the two cannot drift. Supplying a pattern that
    /// disagrees with that path throws.
    /// </param>
    public static IEndpointRouteBuilder MapGcpQueueDispatch(
        this IEndpointRouteBuilder endpoints,
        string? pattern = null)
    {
        // The dead-letter writer is only exercised on a failure path, in production, possibly
        // weeks after deploy — so its absence must fail here, not there.
        if (endpoints.ServiceProvider.GetService<IDeadLetterWriter>() is null)
            throw new InvalidOperationException(
                "MapGcpQueueDispatch requires an IDeadLetterWriter registration. " +
                "Register one before building the app — AddFirestoreDeadLetters() provides the " +
                "Firestore-backed default.");

        return QueueDispatchEndpoint.Map(endpoints, ResolvePattern(endpoints, pattern));
    }

    /// <summary>
    /// The route Cloud Tasks will actually post to. A mapped route that does not match
    /// <c>DispatcherUrl</c> is close to undetectable in production: every task 404s, and because the
    /// queue's retry configuration is deliberately an unlimited backstop, it retries indefinitely
    /// rather than surfacing. So the URL is the single source of truth, and disagreement is an error.
    /// </summary>
    private static string ResolvePattern(IEndpointRouteBuilder endpoints, string? pattern)
    {
        // Not GetService(...) is null: IOptions<T> is registered as an open generic, so it resolves
        // to a default-constructed options object even when AddGcpQueue was never called.
        var dispatcherUrl = endpoints.ServiceProvider
            .GetRequiredService<IOptions<GcpQueueOptions>>().Value.DispatcherUrl;

        if (string.IsNullOrWhiteSpace(dispatcherUrl))
            throw new InvalidOperationException(
                "MapGcpQueueDispatch requires Queue:Gcp:DispatcherUrl to be configured. " +
                "Call AddGcpQueue before building the app.");

        if (!GcpQueueOptionsValidator.IsHttpUrl(dispatcherUrl, out var uri))
            throw new InvalidOperationException(
                $"Queue:Gcp:DispatcherUrl must be an absolute http(s) URL; got '{dispatcherUrl}'.");

        var routeFromUrl = uri.AbsolutePath;
        if (pattern is null)
            return routeFromUrl;

        if (!string.Equals(pattern.TrimEnd('/'), routeFromUrl.TrimEnd('/'), StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"MapGcpQueueDispatch was given the route '{pattern}', but Cloud Tasks posts to " +
                $"'{routeFromUrl}' (the path of Queue:Gcp:DispatcherUrl). Tasks would 404 and retry " +
                "until they expire. Change one so they match, or omit the pattern to use the URL's path.");

        return pattern;
    }
}
