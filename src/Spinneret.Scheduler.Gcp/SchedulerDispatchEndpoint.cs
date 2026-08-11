using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spinneret.Queue.Gcp;

namespace Spinneret.Scheduler.Gcp;

internal static class SchedulerDispatchEndpoint
{
    private const string RoutePattern = "/internal/scheduler/dispatch";

    public static IEndpointRouteBuilder MapGcpSchedulerDispatch(this IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapPost(RoutePattern, (Delegate)Handle)
            .RequireAuthorization(OidcAuthSetup.PolicyName)
            .ExcludeFromDescription();

        return endpoints;
    }

    private static async Task<IResult> Handle(HttpContext httpContext)
    {
        var ct = httpContext.RequestAborted;
        var dispatcher = httpContext.RequestServices.GetRequiredService<GcpSchedulerDispatcher>();
        var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Spinneret.Scheduler.Gcp.Dispatch");

        try
        {
            await dispatcher.DispatchDueJobsAsync(ct);
            return Results.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduler dispatch sweep failed");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }
}
