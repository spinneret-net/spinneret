namespace Spinneret.Scheduler;

/// <summary>
/// One pass over the scheduled jobs that are currently due, implemented by whichever storage
/// provider the host registered. This is the seam a trigger drives: <c>AddSchedulerSweeper()</c>
/// runs it on a timer, an HTTP endpoint runs it on request, and neither needs to know where the
/// jobs live.
/// </summary>
/// <remarks>
/// Only the trigger is shared. What a pass does is emphatically provider-specific — the SQL Server
/// sweep claims a job under a row lock and commits its enqueue inside that same transaction, while
/// the Firestore sweep leases optimistically and commits before enqueueing — so the engines stay in
/// their own packages behind this one method.
/// </remarks>
public interface ISchedulerSweep
{
    /// <summary>
    /// Dispatches the jobs that are due now. Implementations decide how much a single pass covers:
    /// a store that can claim jobs one at a time may drain until nothing is left, while one that
    /// reads a snapshot processes that snapshot and returns, leaving anything that fell due
    /// mid-pass to the next one.
    /// </summary>
    /// <remarks>
    /// A failing job must not abort the pass — the jobs behind it are still due, and a sweep that
    /// gave up on the first bad one would never get past it. Implementations therefore handle
    /// per-job failures internally; an exception out of here means the pass itself could not run.
    /// Implementations must also tolerate concurrent passes: the timer never overlaps its own
    /// sweeps, but an HTTP trigger can be called at any time, including while a timer tick runs.
    /// </remarks>
    Task<SweepResult> SweepAsync(CancellationToken ct);
}
