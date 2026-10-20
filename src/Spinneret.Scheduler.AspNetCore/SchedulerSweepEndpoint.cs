using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spinneret.Scheduler;

namespace Spinneret.Scheduler.AspNetCore;

internal static class SchedulerSweepEndpoint
{
    internal const string DefaultRoutePattern = "/internal/scheduler/sweep";

    // Not an extension named MapSchedulerSweep: in this namespace it would shadow the public one
    // for assemblies seeing internals (the tests), silently skipping its guards.
    public static IEndpointRouteBuilder Map(
        IEndpointRouteBuilder endpoints, string authorizationPolicy, string pattern)
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
        var sweep = httpContext.RequestServices.GetRequiredService<ISchedulerSweep>();
        var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Spinneret.Scheduler.AspNetCore.Sweep");

        try
        {
            return Results.Ok(await sweep.SweepAsync(ct));
        }
        catch (Exception ex)
        {
            // A non-success response makes the external scheduler retry the tick.
            logger.LogError(ex, "Scheduler sweep failed");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }
}
