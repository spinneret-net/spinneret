namespace Spinneret.Scheduler;

/// <summary>
/// A formerly declared recurring job key to remove from the scheduler at startup.
/// </summary>
/// <remarks>
/// Keep it declared for at least one full deploy; old instances re-install the job until they are gone.
/// </remarks>
public interface IRetiredRecurringJob
{
    /// <summary>The <see cref="IRecurringJob.Key"/> the job used to be installed under.</summary>
    string Key { get; }
}
