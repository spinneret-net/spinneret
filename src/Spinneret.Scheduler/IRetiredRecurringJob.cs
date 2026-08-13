namespace Spinneret.Scheduler;

/// <summary>
/// A recurring job key that used to be declared and must now be removed from the scheduler. A job
/// deleted from code alone leaves its stored definition behind, dispatching forever with nothing in
/// the codebase to explain it; retiring the key is what actually removes it, and doing so in code
/// means the removal travels with the deploy and shows up in review.
/// </summary>
/// <remarks>
/// Leave a retirement declared for at least one full deploy. During a rolling deploy the old
/// instances still declare the job and re-install it, so the two fight until the last old instance
/// is gone — after which the retirement wins for good. Removing the line in the same release that
/// deletes the job is what leaves the definition stranded.
/// </remarks>
public interface IRetiredRecurringJob
{
    /// <summary>The <see cref="IRecurringJob.Key"/> the job used to be installed under.</summary>
    string Key { get; }
}
