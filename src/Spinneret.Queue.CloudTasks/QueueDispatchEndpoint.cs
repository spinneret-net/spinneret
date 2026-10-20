using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.Queue.CloudTasks;

/// <summary>
/// HTTP adapter between Cloud Tasks and <see cref="IQueueDeliveryProcessor"/>. The processor owns every
/// decision; this endpoint only translates its outcome to the transport: acknowledge as 200, retry as
/// 429 with a <c>Retry-After</c> header Cloud Tasks honors for the backoff. The queue's own retry
/// config is an effectively unlimited backstop, so a task ends only when this endpoint returns 200.
/// </summary>
internal static class QueueDispatchEndpoint
{
    internal const string DefaultRoutePattern = "/internal/queue/dispatch";

    /// <summary>
    /// Maps the route. Deliberately neither named after the public entry point nor an extension
    /// method: an extension called <c>MapCloudTasksDispatch</c> in this namespace is reached by
    /// enclosing-namespace lookup from any assembly that sits under <c>Spinneret.Queue.CloudTasks</c> and
    /// sees its internals — the test assembly does — where it wins over the public one and silently
    /// skips the route/<c>DispatcherUrl</c> agreement check that exists to be unskippable.
    /// </summary>
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

        // The task name is the delivery's stable identity — what its dead letter is filed under, so
        // a redelivered write lands on the entry it already wrote. Cloud Tasks always sends it;
        // a request without one did not come from Cloud Tasks.
        if (ReadTaskId(httpContext) is not { } taskId)
            return Results.BadRequest("Missing X-CloudTasks-TaskName header.");

        // The unit of work is the storage's: with SQL storage the delivery's writes, bookings and
        // dead letter commit together before the 200, and a failed commit becomes a redelivery.
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

    // Cloud Tasks task name format: projects/{project}/locations/{location}/queues/{queue}/tasks/{id}
    // Slashes are not valid in Firestore document IDs, so we take only the final segment.
    private static string? ReadTaskId(HttpContext httpContext)
    {
        var taskName = httpContext.Request.Headers["X-CloudTasks-TaskName"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(taskName))
            return null;

        var lastSlash = taskName.LastIndexOf('/');
        return lastSlash >= 0 ? taskName[(lastSlash + 1)..] : taskName;
    }
}
