using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Spinneret.Queue.Gcp;

/// <summary>
/// HTTP adapter between Cloud Tasks and <see cref="IQueueDeliveryProcessor"/>. The processor owns every
/// decision; this endpoint only translates its outcome to the transport: acknowledge as 200, retry as
/// 429 with a <c>Retry-After</c> header Cloud Tasks honors for the backoff. The queue's own retry
/// config is an effectively unlimited backstop, so a task ends only when this endpoint returns 200.
/// </summary>
internal static class QueueDispatchEndpoint
{
    private const string RoutePattern = "/internal/queue/dispatch";

    private static readonly TimeSpan UnreadableEnvelopeDeadLetterRetryBackoff = TimeSpan.FromMinutes(1);

    public static IEndpointRouteBuilder MapGcpQueueDispatch(this IEndpointRouteBuilder endpoints)
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
        var services = httpContext.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Spinneret.Queue.Gcp.Dispatch");

        string body;
        using (var reader = new StreamReader(httpContext.Request.Body))
            body = await reader.ReadToEndAsync(ct);

        var taskId = ReadTaskId(httpContext);

        QueueEnvelope? envelope = null;
        string? parseError = null;
        try
        {
            envelope = JsonSerializer.Deserialize<QueueEnvelope>(body);
        }
        catch (JsonException ex)
        {
            parseError = ex.Message;
        }

        if (envelope is null)
            return await DeadLetterUnreadableEnvelope(httpContext, services, logger, body, taskId, parseError, ct);

        var processor = services.GetRequiredService<IQueueDeliveryProcessor>();
        var outcome = await processor.ProcessAsync(envelope, taskId, ct);

        return outcome.Ack ? Results.Ok() : RetryIn(httpContext, outcome.RetryAfter!.Value);
    }

    /// <summary>
    /// A task whose envelope cannot even be read is permanently broken — no retry can repair the bytes.
    /// Dead-letter the raw body for inspection instead of retrying or dropping it.
    /// </summary>
    private static async Task<IResult> DeadLetterUnreadableEnvelope(
        HttpContext httpContext, IServiceProvider services, ILogger logger,
        string body, string taskId, string? parseError, CancellationToken ct)
    {
        var error = parseError ?? "Envelope deserialized to null.";
        logger.LogError("Received unreadable queue envelope (task {TaskId}): {Error}", taskId, error);

        try
        {
            await services.GetRequiredService<IDeadLetterWriter>().WriteAsync(new DeadLetterEntry
            {
                IdempotencyKey = taskId,
                Source = DeadLetterSource.Queue,
                CommandTypeName = "<unreadable envelope>",
                PayloadJson = body,
                Error = error,
                Attempts = 1,
            }, ct);

            return Results.Ok();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Failed to write dead-letter for unreadable envelope (task {TaskId}). Body: {Body}", taskId, body);
            return RetryIn(httpContext, UnreadableEnvelopeDeadLetterRetryBackoff);
        }
    }

    private static IResult RetryIn(HttpContext httpContext, TimeSpan after)
    {
        httpContext.Response.Headers.RetryAfter =
            Math.Max(1, (int)Math.Ceiling(after.TotalSeconds)).ToString();
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }

    // Cloud Tasks task name format: projects/{project}/locations/{location}/queues/{queue}/tasks/{id}
    // Slashes are not valid in Firestore document IDs, so we take only the final segment.
    private static string ReadTaskId(HttpContext httpContext)
    {
        var taskName = httpContext.Request.Headers["X-CloudTasks-TaskName"].FirstOrDefault();
        if (taskName is null)
            return Guid.NewGuid().ToString();

        var lastSlash = taskName.LastIndexOf('/');
        return lastSlash >= 0 ? taskName[(lastSlash + 1)..] : taskName;
    }
}
