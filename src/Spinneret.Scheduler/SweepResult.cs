namespace Spinneret.Scheduler;

/// <summary>
/// What one <see cref="ISchedulerSweep.SweepAsync"/> pass did.
/// </summary>
/// <remarks>
/// New members must be optional init-only properties so existing providers and triggers keep compiling.
/// </remarks>
public sealed record SweepResult
{
    /// <summary>A pass that found nothing due.</summary>
    public static SweepResult Nothing { get; } = new();

    /// <summary>A pass that enqueued <paramref name="jobs"/> jobs.</summary>
    public static SweepResult Dispatched(int jobs) => new() { JobsDispatched = jobs };

    /// <summary>How many jobs this pass enqueued; zero is the ordinary case, not a failure.</summary>
    public int JobsDispatched { get; init; }
}
