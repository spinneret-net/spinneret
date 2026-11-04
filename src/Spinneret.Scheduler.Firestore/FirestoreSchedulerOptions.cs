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

    /// <summary>How long a job whose enqueue keeps failing is retried before it is dead-lettered.</summary>
    /// <remarks>
    /// Keep it within the transport's dedupe window (about an hour on Cloud Tasks): past it, a retry of an
    /// enqueue that landed unseen is delivered a second time.
    /// </remarks>
    public TimeSpan EnqueueRetryWindow { get; set; } = TimeSpan.FromHours(1);
}
