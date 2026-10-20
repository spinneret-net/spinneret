using Spinneret.Mediator;

namespace Spinneret.Scheduler;

/// <summary>
/// Registers durable recurring mediator jobs.
/// </summary>
public interface IRecurringJobScheduler
{
    /// <summary>
    /// Registers or updates a recurring job by <paramref name="key"/>; idempotent and safe to call on every startup.
    /// </summary>
    /// <remarks>
    /// The next run is rescheduled only when the schedule changed. A failed run is dead-lettered but never stops the schedule.
    /// </remarks>
    Task RegisterAsync<TResponse>(
        string key, IRequest<TResponse> request, Schedule schedule, CancellationToken ct = default);

    /// <summary>
    /// Removes the recurring job with <paramref name="key"/>; a missing key or a one-shot job is left alone.
    /// </summary>
    Task UnregisterAsync(string key, CancellationToken ct = default);
}
