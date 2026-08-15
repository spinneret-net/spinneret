using Spinneret.Mediator;

namespace Spinneret.Scheduler;

/// <summary>
/// A recurring job declared in application code and installed idempotently at startup.
/// Adding a scheduled job is just implementing <see cref="IRecurringJob{TResponse}"/> and
/// registering it in DI — no infrastructure changes: every job rides the single
/// scheduler-dispatch sweep.
/// </summary>
/// <remarks>
/// This non-generic base exists so the installer can hold jobs of differing response types in one
/// <c>IEnumerable&lt;IRecurringJob&gt;</c>. Implement <see cref="IRecurringJob{TResponse}"/>, not
/// this: <see cref="Register"/> is internal, so this interface cannot be implemented from outside
/// the package, which is what guarantees every job in that collection carries a real
/// <see cref="IRequest{TResponse}"/> the queue can dispatch.
/// </remarks>
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

    /// <summary>
    /// Registers this job with <paramref name="scheduler"/>. The job does this itself rather than
    /// handing its request to the installer because only it still knows the request's response
    /// type — which is what lets the scheduler take a typed <see cref="IRequest{TResponse}"/>
    /// instead of an untyped request it could only fail on at runtime.
    /// </summary>
    /// <param name="scheduler">The scheduler to register with.</param>
    /// <param name="schedule">
    /// The schedule the installer already read, passed back in so <see cref="Schedule"/> is read
    /// exactly once per install attempt.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    internal Task Register(IRecurringJobScheduler scheduler, Schedule schedule, CancellationToken ct);
}

/// <summary>
/// A recurring job whose request produces <typeparamref name="TResponse"/>. Any request may be
/// scheduled: each run is enqueued and executed on a worker, so the response is discarded either
/// way — use <see cref="Spinneret.Functional.Unit"/> when the handler returns nothing.
/// </summary>
public interface IRecurringJob<TResponse> : IRecurringJob
{
    /// <summary>Builds the request to enqueue on each run.</summary>
    IRequest<TResponse> CreateRequest();

    Task IRecurringJob.Register(IRecurringJobScheduler scheduler, Schedule schedule, CancellationToken ct) =>
        scheduler.RegisterAsync(Key, CreateRequest(), schedule, ct);
}
