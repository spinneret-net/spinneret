using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spinneret.Mediator;

namespace Spinneret.Queue;

internal sealed class QueueDispatcher(
    QueueTypeRegistry registry,
    ISpinneretMediator mediator,
    IQueuePayloadSerializer serializer,
    ILogger<QueueDispatcher> logger)
    : IQueueDispatcher
{
    private static readonly MethodInfo SendOpenGeneric =
        typeof(ISpinneretMediator).GetMethods()
            .Single(m => m is { Name: nameof(ISpinneretMediator.Send), IsGenericMethod: true });

    public async Task Dispatch(QueueEnvelope envelope, CancellationToken ct)
    {
        var registered = registry.Resolve(envelope.RequestTypeName);
        var requestType = registered.RequestType;
        var responseType = registered.ResponseType;

        object? request;
        try
        {
            request = serializer.Deserialize(envelope.PayloadJson, requestType);
        }
        catch (JsonException ex)
        {
            throw new QueueHandlerPermanentException(
                $"Queue payload for '{envelope.RequestTypeName}' cannot be deserialized: {ex.Message}", ex);
        }

        if (request is null)
            throw new QueueHandlerPermanentException(
                $"Queue payload for '{envelope.RequestTypeName}' deserialized to null.");

        var send = SendOpenGeneric.MakeGenericMethod(responseType);
        var task = (Task)send.Invoke(mediator, [request, ct])!;
        await task;

        var response = task.GetType().GetProperty("Result")?.GetValue(task);

        var error = ResultIntrospection.TryGetError(response);
        if (error is not null)
        {
            logger.LogWarning(
                "Queued request {RequestType} returned an error result: {Error}",
                envelope.RequestTypeName, error);
            throw new QueueHandlerFailedException(error);
        }
    }
}

/// <summary>
/// Serializer abstraction so the queue can use whatever <see cref="JsonSerializerOptions"/>
/// the host configured. Implemented by transports or hosts; register a custom one before the
/// transport's Add* call. Additions to this interface ship as default interface members.
/// </summary>
public interface IQueuePayloadSerializer
{
    string Serialize(object request, Type requestType);
    object? Deserialize(string json, Type requestType);
}
