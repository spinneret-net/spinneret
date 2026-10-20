using Spinneret.Mediator;

namespace Spinneret.Scheduler;

/// <summary>
/// A recurring job declared in code and installed idempotently at startup.
/// </summary>
/// <remarks>
/// Implement <see cref="IRecurringJob{TResponse}"/>, not this non-generic base.
/// </remarks>
public interface IRecurringJob
{
    /// <summary>
    /// Stable identifier, unique across all recurring jobs; re-installing the same key upserts.
    /// </summary>
    string Key { get; }

    /// <summary>
    /// When the job is enqueued; read once per install.
    /// </summary>
    Schedule Schedule { get; }

    // Lets the job pass its typed request, since only it knows the response type.
    internal Task Register(IRecurringJobScheduler scheduler, Schedule schedule, CancellationToken ct);
}

/// <summary>
/// A recurring job whose request produces <typeparamref name="TResponse"/>; the response is discarded.
/// </summary>
public interface IRecurringJob<TResponse> : IRecurringJob
{
    /// <summary>Builds the request to enqueue on each run.</summary>
    IRequest<TResponse> CreateRequest();

    Task IRecurringJob.Register(IRecurringJobScheduler scheduler, Schedule schedule, CancellationToken ct) =>
        scheduler.RegisterAsync(Key, CreateRequest(), schedule, ct);
}
