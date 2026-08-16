namespace Spinneret.Scheduler.Firestore;

/// <summary>
/// The single source of truth for the scheduled-job document shape. Every reader and writer of the
/// scheduler collection — <see cref="FirestoreScheduler"/>, <see cref="FirestoreTransactionalScheduler"/>
/// and <see cref="FirestoreSchedulerDispatcher"/> — refers to these names so the schema can never drift.
/// </summary>
internal static class ScheduledJob
{
    internal static class Fields
    {
        public const string RequestTypeName = "requestTypeName";
        public const string PayloadJson = "payloadJson";
        public const string Status = "status";
        public const string NextExecuteAt = "nextExecuteAt";
        public const string CreatedAt = "createdAt";
        // Canonical Schedule string (Schedule.ToString/Parse); present only on recurring jobs —
        // its absence marks a one-shot job.
        public const string Schedule = "schedule";
        // Observability: when a recurring job was last enqueued.
        public const string LastRunAt = "lastRunAt";
    }

    internal static class StatusValues
    {
        public const string Pending = "pending";
        public const string Cancelled = "cancelled";
        public const string Enqueued = "enqueued";
        public const string Failed = "failed";
    }
}
