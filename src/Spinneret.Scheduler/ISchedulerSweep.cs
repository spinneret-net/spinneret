namespace Spinneret.Scheduler;

/// <summary>
/// One pass over the due scheduled jobs, implemented by the storage provider and driven by a trigger.
/// </summary>
public interface ISchedulerSweep
{
    /// <summary>
    /// Dispatches the jobs that are due now.
    /// </summary>
    /// <remarks>
    /// Implementations handle per-job failures internally and must tolerate concurrent passes;
    /// an exception means the pass itself could not run.
    /// </remarks>
    Task<SweepResult> SweepAsync(CancellationToken ct);
}
