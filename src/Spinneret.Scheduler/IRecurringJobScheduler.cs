using Spinneret.Mediator;

namespace Spinneret.Scheduler;

/// <summary>
/// Registers durable recurring mediator jobs. One-shot scheduling lives with the provider's
/// transactional API so it can be enlisted in a caller's unit of work.
/// </summary>
public interface IRecurringJobScheduler
{
    /// <summary>
    /// Registers — or updates in place — a durable recurring job identified by the stable
    /// <paramref name="key"/>. The job is enqueued per <paramref name="schedule"/> by the dispatch
    /// sweep. Idempotent: re-registering the same key refreshes the request payload without creating
    /// a duplicate, and disturbs the already-scheduled next run only when the schedule itself
    /// changed, so it is safe to call on every startup. Recurrence is owned by the persisted job —
    /// an individual failed run is dead-lettered but never stops the schedule.
    /// </summary>
    /// <remarks>
    /// Any request may be registered, whatever its response type: each run is enqueued and executed
    /// on a worker, which discards the response. <typeparamref name="TResponse"/> is inferred from
    /// <paramref name="request"/> and never observed — it is named only so the compiler can hold the
    /// caller to a request the queue is able to dispatch.
    /// </remarks>
    Task RegisterAsync<TResponse>(
        string key, IRequest<TResponse> request, Schedule schedule, CancellationToken ct = default);

    /// <summary>
    /// Removes the recurring job identified by <paramref name="key"/>, so it stops being dispatched.
    /// Idempotent: a key with no job is a no-op, not an error, so a retirement can stay declared
    /// across as many deploys as it takes. Only recurring jobs are removable — a key naming a
    /// one-shot job is left untouched, since one-shot handles and job keys share a namespace and
    /// cancelling a pending one-shot is the transactional API's job, not this one's.
    /// </summary>
    Task UnregisterAsync(string key, CancellationToken ct = default);
}
