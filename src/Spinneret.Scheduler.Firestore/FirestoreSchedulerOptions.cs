namespace Spinneret.Scheduler.Firestore;

public sealed class FirestoreSchedulerOptions
{
    public static readonly string SectionName = "Scheduler:Firestore";

    /// <summary>Firestore collection storing scheduled job documents.</summary>
    public string Collection { get; set; } = "scheduled_jobs";

    /// <summary>
    /// How long a one-shot job is hidden from the sweep once a dispatcher leases it. If the
    /// dispatcher crashes mid-dispatch the lease lapses and a later sweep retries the job, so it
    /// can never get permanently stuck. Must comfortably exceed the time to enqueue a single job.
    /// </summary>
    public TimeSpan OneShotLeaseWindow { get; set; } = TimeSpan.FromMinutes(5);
}
