using Spinneret.Mediator;

namespace Spinneret.Queue;

/// <summary>
/// Builds the envelope for a freshly enqueued request — the one place that decides its wire name,
/// payload serialization and enqueue time, so every <see cref="IQueue"/> implementation produces
/// envelopes another can deliver.
/// </summary>
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
