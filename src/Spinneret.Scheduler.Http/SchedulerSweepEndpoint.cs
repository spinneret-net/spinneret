using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spinneret.Scheduler;

namespace Spinneret.Scheduler.Http;

internal static class SchedulerSweepEndpoint
{
    internal const string DefaultRoutePattern = "/internal/scheduler/sweep";

    public static IEndpointRouteBuilder MapSchedulerSweep(
        this IEndpointRouteBuilder endpoints, string authorizationPolicy, string pattern)
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
            .CreateLogger("Spinneret.Scheduler.Http.Sweep");

        try
        {
            // The result travels back to the caller: an external scheduler is the only thing that
            // knows a sweep happened, so telling it what the sweep did is the difference between an
            // observable cron job and one that reports success either way.
            return Results.Ok(await sweep.SweepAsync(ct));
        }
        catch (Exception ex)
        {
            // 500 rather than a swallowed error: the caller is a scheduler, and a non-success
            // response is what makes it retry the tick.
            logger.LogError(ex, "Scheduler sweep failed");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }
}
