using Spinneret.Mediator;

namespace Spinneret.Queue;

// The one place that builds envelopes, so every IQueue produces envelopes any other can deliver.
internal sealed class QueueEnvelopeFactory(
    QueueTypeRegistry registry,
    IQueuePayloadSerializer serializer,
    TimeProvider timeProvider)
{
    public QueueEnvelope Create<TResponse>(IRequest<TResponse> request, QueueOptions? options)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        return new QueueEnvelope
        {
            RequestTypeName = registry.GetName(requestType),
            PayloadJson = serializer.Serialize(request, requestType),
            EnqueuedAtUtc = timeProvider.GetUtcNow(),
            Description = options?.Description,
        };
    }
}
