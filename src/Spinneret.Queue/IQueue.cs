using Spinneret.Mediator;

namespace Spinneret.Queue;

/// <summary>
/// Enqueues mediator requests for asynchronous, durable execution on a remote worker.
/// Same shape as <see cref="ISpinneretMediator.Send{TResponse}"/> but fire-and-forget:
/// the response is discarded on the worker side. Failures are retried by the transport.
/// </summary>
public interface IQueue
{
    Task Enqueue<TResponse>(IRequest<TResponse> request, QueueOptions? options = null, CancellationToken ct = default);
    Task Enqueue(IRequest<Unit> request, QueueOptions? options = null, CancellationToken ct = default);
}
