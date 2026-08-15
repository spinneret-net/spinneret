using Spinneret.Functional;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Options;
using Spinneret.Mediator;

namespace Spinneret.Scheduler.Firestore;

/// <summary>
/// Schedules and cancels one-shot jobs as part of a caller-owned Firestore transaction, so the job
/// write commits atomically with the caller's other changes (e.g. scheduling an employee's removal
/// in the same transaction that records the termination). The job document is identical to those
/// the standalone scheduler writes, so the shared dispatch sweep picks it up uniformly.
/// </summary>
public interface IFirestoreTransactionalScheduler
{
    /// <summary>
    /// Queues a one-shot job — to run once at <paramref name="executeAt"/> — onto the given
    /// <paramref name="transaction"/>. Returns the handle to pass to <see cref="CancelJob"/>.
    /// </summary>
    string ScheduleJob(Transaction transaction, IRequest<Unit> request, DateTimeOffset executeAt);

    /// <summary>
    /// Cancels the job identified by <paramref name="handle"/> within the transaction.
    /// </summary>
    /// <remarks>
    /// Unconditional, and deliberately so — it differs from the MSSQL scheduler, which cancels only a
    /// still-pending job and silently ignores anything else. Matching that here would require reading
    /// the document first, and Firestore requires every read in a transaction to precede every write;
    /// since this method enlists in a transaction the caller owns and may already have written to,
    /// a read here could invalidate it. Two consequences follow: cancelling a job that already ran
    /// moves it to <c>cancelled</c> rather than being ignored, and cancelling an unknown handle
    /// throws when the transaction commits rather than passing silently.
    /// </remarks>
    void CancelJob(Transaction transaction, string handle);
}

internal sealed class FirestoreTransactionalScheduler(
    FirestoreDb db,
    IOptions<FirestoreSchedulerOptions> options,
    ScheduledJobDocumentFactory factory)
    : IFirestoreTransactionalScheduler
{
    private CollectionReference Collection => db.Collection(options.Value.Collection);

    public string ScheduleJob(Transaction transaction, IRequest<Unit> request, DateTimeOffset executeAt)
    {
        var docRef = Collection.Document();
        transaction.Set(docRef, factory.OneShot(request, executeAt));
        return docRef.Id;
    }

    public void CancelJob(Transaction transaction, string handle) =>
        transaction.Update(
            Collection.Document(handle),
            new Dictionary<string, object> { [ScheduledJob.Fields.Status] = ScheduledJob.StatusValues.Cancelled });
}
