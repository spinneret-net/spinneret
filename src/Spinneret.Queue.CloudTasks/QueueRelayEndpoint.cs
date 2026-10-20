using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Spinneret.Queue.CloudTasks;

/// <summary>
/// HTTP trigger for one relay pass, for hosts that do not run the relay in the background — a host
/// that scales to zero has no thread to tick, so an external scheduler drives the pass instead.
/// </summary>
internal static class QueueRelayEndpoint
{
    internal const string DefaultRoutePattern = "/internal/queue/relay";

    /// <summary>
    /// Maps the route. Deliberately neither named after the public entry point nor an extension
    /// method, for the same reason as <see cref="QueueDispatchEndpoint.Map"/>.
    /// </summary>
    public static IEndpointRouteBuilder Map(IEndpointRouteBuilder endpoints, string authorizationPolicy, string pattern)
    {
        endpoints
            .MapPost(pattern, (Delegate)Handle)
            .RequireAuthorization(authorizationPolicy)
            .ExcludeFromDescription();

        return endpoints;
    }

    private static async Task<IResult> Handle(HttpContext httpContext)
    {
        var ct = httpContext.RequestAborted;
        var relay = httpContext.RequestServices.GetRequiredService<IQueueRelay>();
        var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Spinneret.Queue.CloudTasks.Relay");

        try
        {
            // The result travels back to the caller: an external scheduler is the only thing that
            // knows a pass happened, so telling it what the pass did makes the job observable.
            return Results.Ok(await relay.RelayAsync(ct));
        }
        catch (Exception ex)
        {
            // 500 rather than a swallowed error: the caller is a scheduler, and a non-success
            // response is what makes it retry.
            logger.LogError(ex, "Queue relay pass failed");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }
}
