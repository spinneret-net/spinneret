using Spinneret.Functional;
using Spinneret.Mediator;

namespace Spinneret.Scheduler;

/// <summary>
/// A recurring job declared in application code and installed idempotently at startup.
/// Adding a scheduled job is just implementing this interface and registering it in DI —
/// no infrastructure changes: every job rides the single scheduler-dispatch sweep.
/// </summary>
public interface IRecurringJob
{
    /// <summary>
    /// Stable identifier for the job; also used as its document id, so re-installing upserts
    /// rather than creating a duplicate. Must be unique across all recurring jobs.
    /// </summary>
    string Key { get; }

    /// <summary>
    /// When the job is enqueued, as a cron expression in an explicit zone. Read once, while the job
    /// is being installed. Build it from injected configuration if the cadence differs per
    /// environment — the declaration stays the single place the schedule is decided.
    /// </summary>
    Schedule Schedule { get; }

    /// <summary>Builds the request to enqueue on each run.</summary>
    IRequest<Unit> CreateRequest();
}
