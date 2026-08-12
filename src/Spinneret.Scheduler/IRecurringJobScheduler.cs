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
    Task RegisterAsync(string key, IRequest<Unit> request, Schedule schedule, CancellationToken ct = default);
}
