namespace Spinneret.Scheduler;

/// <summary>
/// What one <see cref="ISchedulerSweep.SweepAsync"/> pass did. A result object rather than a bare
/// <c>Task</c> because a return type is the one part of an interface that cannot be widened later
/// without breaking every implementer and every caller at once — so the pass reports through a type
/// that can grow instead.
/// </summary>
/// <remarks>
/// New members must be optional (non-required) init-only properties, so out-of-tree providers keep
/// compiling and a trigger written against an older version keeps reading what it knew about.
/// </remarks>
public sealed record SweepResult
{
    /// <summary>A pass that found nothing due.</summary>
    public static SweepResult Nothing { get; } = new();

    /// <summary>A pass that enqueued <paramref name="jobs"/> jobs.</summary>
    public static SweepResult Dispatched(int jobs) => new() { JobsDispatched = jobs };

    /// <summary>
    /// How many jobs this pass enqueued. Zero is the ordinary case — most sweeps find nothing due —
    /// so treat it as information for a trigger to log or return, not as a failure signal.
    /// </summary>
    public int JobsDispatched { get; init; }
}
