using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.Queue.CloudTasks;

// Translates the processor's outcome: ack is 200, retry is 429 with Retry-After.
// The queue's own retry config is an unlimited backstop, so a task ends only on 200.
internal static class QueueDispatchEndpoint
{
    internal const string DefaultRoutePattern = "/internal/queue/dispatch";

    // Not an extension named MapCloudTasksDispatch: from inside this namespace it would shadow the
    // public one and skip the DispatcherUrl check.
    public static IEndpointRouteBuilder Map(IEndpointRouteBuilder endpoints, string pattern)
    {
        endpoints
            .MapPost(pattern, (Delegate)Handle)
            .RequireAuthorization(CloudTasksAuth.PolicyName)
            .ExcludeFromDescription();

        return endpoints;
    }

    private static async Task<IResult> Handle(HttpContext httpContext)
    {
        var ct = httpContext.RequestAborted;
        var services = httpContext.RequestServices;

        string body;
        using (var reader = new StreamReader(httpContext.Request.Body))
            body = await reader.ReadToEndAsync(ct);

        // The dead-letter key; Cloud Tasks always sends it.
        if (ReadTaskId(httpContext) is not { } taskId)
            return Results.BadRequest("Missing X-CloudTasks-TaskName header.");

        var pipeline = services.GetRequiredService<QueueDeliveryPipeline>();
        var outcome = await services.GetRequiredService<IQueueUnitOfWorkFactory>().ExecuteAsync(
            (unit, token) => pipeline.DeliverAsync(unit, body, taskId, token), ct);

        return outcome.Ack ? Results.Ok() : RetryIn(httpContext, outcome.RetryAfter!.Value);
    }

    private static IResult RetryIn(HttpContext httpContext, TimeSpan after)
    {
        httpContext.Response.Headers.RetryAfter =
            Math.Max(1, (int)Math.Ceiling(after.TotalSeconds)).ToString();
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }

    // Last segment only: slashes are invalid in Firestore document ids.
    private static string? ReadTaskId(HttpContext httpContext)
    {
        var taskName = httpContext.Request.Headers["X-CloudTasks-TaskName"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(taskName))
            return null;

        var lastSlash = taskName.LastIndexOf('/');
        return lastSlash >= 0 ? taskName[(lastSlash + 1)..] : taskName;
    }
}
