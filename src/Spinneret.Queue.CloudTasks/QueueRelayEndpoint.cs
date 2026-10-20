using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Spinneret.Queue.CloudTasks;

internal static class QueueRelayEndpoint
{
    internal const string DefaultRoutePattern = "/internal/queue/relay";

    // Not an extension method, for the same reason as QueueDispatchEndpoint.Map.
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
            return Results.Ok(await relay.RelayAsync(ct));
        }
        catch (Exception ex)
        {
            // A non-success response is what makes the scheduler retry.
            logger.LogError(ex, "Queue relay pass failed");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }
}
