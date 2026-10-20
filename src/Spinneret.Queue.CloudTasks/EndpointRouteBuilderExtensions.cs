using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Spinneret.Queue;
using Spinneret.Queue.CloudTasks;

// ReSharper disable once CheckNamespace — deliberate: endpoint extensions live in the
// builder namespace so Map* calls are discoverable without a using directive.
namespace Microsoft.AspNetCore.Builder;

public static class CloudTasksEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the OIDC-protected Cloud Tasks dispatch endpoint at the path of
    /// <c>Queue:CloudTasks:DispatcherUrl</c>. Map this only on hosts that consume the queue; those hosts
    /// must have registered an <see cref="IDeadLetterWriter"/>.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="pattern">
    /// Route for the endpoint. Defaults to the path of <c>Queue:CloudTasks:DispatcherUrl</c>, which is the
    /// address Cloud Tasks actually posts to — so the two cannot drift. Supplying a pattern that
    /// disagrees with that path throws.
    /// </param>
    public static IEndpointRouteBuilder MapCloudTasksDispatch(
        this IEndpointRouteBuilder endpoints,
        string? pattern = null)
    {
        // The dead-letter writer is only exercised on a failure path, in production, possibly
        // weeks after deploy — so its absence must fail here, not there.
        if (endpoints.ServiceProvider.GetService<IDeadLetterWriter>() is null)
            throw new InvalidOperationException(
                "MapCloudTasksDispatch requires an IDeadLetterWriter registration. " +
                "Choose one in AddQueue — a SQL storage provides its own, and q.UseFirestoreDeadLetters() the " +
                "Firestore-backed default.");

        return QueueDispatchEndpoint.Map(endpoints, ResolvePattern(endpoints, pattern));
    }

    /// <summary>
    /// Maps an endpoint that runs one pass of the relay forwarding a SQL storage's outbox to Cloud
    /// Tasks, guarded by <paramref name="authorizationPolicy"/>. For hosts that turn
    /// <c>Queue:CloudTasks:Relay:RunInBackground</c> off — a host that scales to zero has no thread
    /// to tick — and drive the pass from Cloud Scheduler instead.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="authorizationPolicy">
    /// Name of the policy protecting the endpoint, e.g. <see cref="CloudTasksAuth.PolicyName"/> to
    /// accept the same Google-minted OIDC tokens as the dispatch endpoint. Required rather than
    /// defaulted: guessing wrong would leave the relay open.
    /// </param>
    /// <param name="pattern">Route for the endpoint; defaults to <c>/internal/queue/relay</c>.</param>
    public static IEndpointRouteBuilder MapQueueRelay(
        this IEndpointRouteBuilder endpoints,
        string authorizationPolicy,
        string? pattern = null)
    {
        if (string.IsNullOrWhiteSpace(authorizationPolicy))
            throw new ArgumentException(
                "An authorization policy name is required: the relay endpoint forwards the whole outbox.",
                nameof(authorizationPolicy));

        // Exercised only when the external scheduler fires, so a queue without a relay must fail
        // here rather than 500 on the first tick.
        if (endpoints.ServiceProvider.GetService<IQueueRelay>() is null)
            throw new InvalidOperationException(
                "MapQueueRelay requires a queue whose SQL storage feeds Cloud Tasks: "
                + "AddQueue(q => q.UseMssqlStorage(...).UseCloudTasksDelivery(...)), or UsePostgresStorage likewise.");

        return QueueRelayEndpoint.Map(endpoints, authorizationPolicy, pattern ?? QueueRelayEndpoint.DefaultRoutePattern);
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
        // to a default-constructed options object even when UseCloudTasksDelivery was never called.
        var dispatcherUrl = endpoints.ServiceProvider
            .GetRequiredService<IOptions<CloudTasksOptions>>().Value.DispatcherUrl;

        if (string.IsNullOrWhiteSpace(dispatcherUrl))
            throw new InvalidOperationException(
                "MapCloudTasksDispatch requires Queue:CloudTasks:DispatcherUrl to be configured. " +
                "Call q.UseCloudTasksDelivery(...) in AddQueue before building the app.");

        if (!CloudTasksOptionsValidator.IsHttpUrl(dispatcherUrl, out var uri))
            throw new InvalidOperationException(
                $"Queue:CloudTasks:DispatcherUrl must be an absolute http(s) URL; got '{dispatcherUrl}'.");

        var routeFromUrl = uri.AbsolutePath;
        if (pattern is null)
            return routeFromUrl;

        if (!string.Equals(pattern.TrimEnd('/'), routeFromUrl.TrimEnd('/'), StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"MapCloudTasksDispatch was given the route '{pattern}', but Cloud Tasks posts to " +
                $"'{routeFromUrl}' (the path of Queue:CloudTasks:DispatcherUrl). Tasks would 404 and retry " +
                "until they expire. Change one so they match, or omit the pattern to use the URL's path.");

        return pattern;
    }
}
