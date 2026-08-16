namespace Spinneret.Scheduler.Firestore;

/// <summary>
/// The single source of truth for the scheduled-job document shape. Every reader and writer of the
/// scheduler collection — <see cref="FirestoreScheduler"/>, <see cref="FirestoreTransactionalScheduler"/>
/// and <see cref="FirestoreSchedulerDispatcher"/> — refers to these names so the schema can never drift.
/// </summary>
/// <remarks>
/// There is no status field: a document's existence *is* its status. A job is removed the moment it
/// stops being work to do — a one-shot that ran, one whose failure reached the dead-letter store, or
/// one that was cancelled — so every document in the collection is due or waiting to be. That keeps
/// the collection from growing without bound and lets the sweep query on <c>nextExecuteAt</c> alone.
/// </remarks>
internal static class ScheduledJob
{
    /// <summary>
    /// Document-id prefix for one-shot jobs. One-shot handles and caller-chosen recurring keys share
    /// this collection, and cancelling must never delete a recurring job's schedule, so the prefix is
    /// what tells them apart — a check that costs no read, which is what keeps
    /// <see cref="IFirestoreTransactionalScheduler"/> write-only.
    /// </summary>
    public const string OneShotHandlePrefix = "oneshot-";

    /// <summary>Mints a one-shot handle; the entropy after the prefix is what spreads the writes.</summary>
    public static string NewOneShotHandle() => $"{OneShotHandlePrefix}{Guid.NewGuid():N}";

    /// <summary>True for a handle this library minted for a one-shot job.</summary>
    public static bool IsOneShotHandle(string handle) =>
        handle.StartsWith(OneShotHandlePrefix, StringComparison.Ordinal);

    internal static class Fields
    {
        public const string RequestTypeName = "requestTypeName";
        public const string PayloadJson = "payloadJson";
        public const string NextExecuteAt = "nextExecuteAt";
        public const string CreatedAt = "createdAt";
        // Canonical Schedule string (Schedule.ToString/Parse); present only on recurring jobs —
        // its absence marks a one-shot job.
        public const string Schedule = "schedule";
        // Observability: when a recurring job was last enqueued. A one-shot job never carries a
        // meaningful value — it is deleted on the run that would have set it.
        public const string LastRunAt = "lastRunAt";
    }
}
