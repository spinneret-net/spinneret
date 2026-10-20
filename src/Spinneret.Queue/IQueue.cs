using Spinneret.Mediator;

namespace Spinneret.Queue;

/// <summary>Enqueues mediator requests for durable, fire-and-forget execution on a worker.</summary>
public interface IQueue
{
    Task Enqueue<TResponse>(IRequest<TResponse> request, QueueOptions? options = null, CancellationToken ct = default);
}
