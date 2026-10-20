using Microsoft.Extensions.Logging;
using Spinneret.Mediator;

namespace Spinneret.Queue;

/// <summary>
/// <see cref="IQueue"/> straight onto a push transport, for hosts with no message storage. Each
/// enqueue is a call to the transport, so it is not atomic with anything else the caller writes —
/// that guarantee needs a storage package, whose outbox the transport is then fed from.
/// </summary>
internal sealed class DirectQueue(
    IQueueTransport transport,
    QueueEnvelopeFactory envelopes,
    QueueTypeRegistry registry,
    TimeProvider timeProvider,
    ILogger<DirectQueue> logger)
    : IQueue, IEnvelopeQueue
{
    public Task Enqueue<TResponse>(IRequest<TResponse> request, QueueOptions? options = null, CancellationToken ct = default)
        => SendAsync(envelopes.Create(request, options), options?.Delay, options?.DedupeKey, ct);

    public Task Enqueue(QueueEnvelope envelope, TimeSpan? delay = null, CancellationToken ct = default)
        => SendAsync(envelope, delay, dedupeKey: null, ct);

    private async Task SendAsync(QueueEnvelope envelope, TimeSpan? delay, string? dedupeKey, CancellationToken ct)
    {
        var channel = registry.Resolve(envelope.RequestTypeName).Policy.ResolvedChannel;

        using var activity = QueueTracing.StartProducer(channel, envelope, dedupeKey);
        envelope = QueueTracing.StampTraceContext(envelope);

        DateTimeOffset? deliverAt = delay is { } d && d > TimeSpan.Zero ? timeProvider.GetUtcNow() + d : null;
        var messageName = string.IsNullOrWhiteSpace(dedupeKey) ? null : dedupeKey;

        var created = await transport.SendAsync(new OutboundMessage(envelope, channel, deliverAt, messageName), ct);

        // The documented DedupeKey contract is "only one delivery", so a duplicate is success.
        if (!created)
            logger.LogDebug(
                "Skipped enqueue of {RequestType} on channel {Channel}: dedupe key {DedupeKey} already enqueued",
                envelope.RequestTypeName, channel, dedupeKey);
    }
}
