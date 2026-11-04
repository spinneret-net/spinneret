namespace Spinneret.Scheduler.Firestore;

// No status field: a document is removed once it stops being work, so the sweep queries
// nextExecuteAt alone.
internal static class ScheduledJob
{
    // Tells one-shots from recurring keys without a read, keeping the transactional API write-only.
    public const string OneShotHandlePrefix = "oneshot-";

    public static string NewOneShotHandle() => $"{OneShotHandlePrefix}{Guid.NewGuid():N}";

    public static bool IsOneShotHandle(string handle) =>
        handle.StartsWith(OneShotHandlePrefix, StringComparison.Ordinal);

    internal static class Fields
    {
        public const string RequestTypeName = "requestTypeName";
        public const string PayloadJson = "payloadJson";
        public const string NextExecuteAt = "nextExecuteAt";
        public const string CreatedAt = "createdAt";
        // Present only on recurring jobs; its absence marks a one-shot.
        public const string Schedule = "schedule";
        public const string LastRunAt = "lastRunAt";
        // Present only while a job waits to retry an enqueue that failed.
        public const string DispatchFailures = "dispatchFailures";
        public const string FirstFailedAt = "firstFailedAt";
        // Recurring only: the failed occurrence's dedupe key, which its slot no longer yields once retried.
        public const string PendingDedupeKey = "pendingDedupeKey";
    }
}
