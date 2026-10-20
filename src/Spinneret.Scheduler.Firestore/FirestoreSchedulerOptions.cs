namespace Spinneret.Scheduler.Firestore;

public sealed class FirestoreSchedulerOptions
{
    public static readonly string SectionName = "Scheduler:Firestore";

    /// <summary>Firestore collection storing scheduled job documents.</summary>
    public string Collection { get; set; } = "scheduled_jobs";

    /// <summary>
    /// How long a leased one-shot job is hidden before a later sweep retries it; must exceed one enqueue's duration.
    /// </summary>
    public TimeSpan OneShotLeaseWindow { get; set; } = TimeSpan.FromMinutes(5);
}
