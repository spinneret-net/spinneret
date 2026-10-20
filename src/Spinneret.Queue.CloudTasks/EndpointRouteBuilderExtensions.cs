using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Spinneret.Queue;
using Spinneret.Queue.CloudTasks;

// ReSharper disable once CheckNamespace — builder namespace, for discoverability.
namespace Microsoft.AspNetCore.Builder;

public static class CloudTasksEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the OIDC-protected Cloud Tasks dispatch endpoint; requires an <see cref="IDeadLetterWriter"/>.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="pattern">
    /// Defaults to the path of <c>Queue:CloudTasks:DispatcherUrl</c>; a pattern that disagrees with it throws.
    /// </param>
    public static IEndpointRouteBuilder MapCloudTasksDispatch(
        this IEndpointRouteBuilder endpoints,
        string? pattern = null)
    {
        // Only used on a failure path, possibly weeks after deploy, so fail now.
        if (endpoints.ServiceProvider.GetService<IDeadLetterWriter>() is null)
            throw new InvalidOperationException(
                "MapCloudTasksDispatch requires an IDeadLetterWriter registration. " +
                "Choose one in AddQueue — a SQL storage provides its own, and q.UseFirestoreDeadLetters() the " +
                "Firestore-backed default.");

        return QueueDispatchEndpoint.Map(endpoints, ResolvePattern(endpoints, pattern));
    }

    /// <summary>
    /// Maps an endpoint that runs one relay pass, for hosts with <c>Relay:RunInBackground</c> off.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="authorizationPolicy">
    /// Policy protecting the endpoint, e.g. <see cref="CloudTasksAuth.PolicyName"/>.
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

        // Fail at startup rather than 500 on the first scheduler tick.
        if (endpoints.ServiceProvider.GetService<IQueueRelay>() is null)
            throw new InvalidOperationException(
                "MapQueueRelay requires a queue whose SQL storage feeds Cloud Tasks: "
                + "AddQueue(q => q.UseMssqlStorage(...).UseCloudTasksDelivery(...)), or UsePostgresStorage likewise.");

        return QueueRelayEndpoint.Map(endpoints, authorizationPolicy, pattern ?? QueueRelayEndpoint.DefaultRoutePattern);
    }

    // A route mismatch would 404 every task and retry silently forever, so DispatcherUrl is the source of truth.
    private static string ResolvePattern(IEndpointRouteBuilder endpoints, string? pattern)
    {
        // IOptions<T> always resolves (open generic), so check the value rather than null.
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
