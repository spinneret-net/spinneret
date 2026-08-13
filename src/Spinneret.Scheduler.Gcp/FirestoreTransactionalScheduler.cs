using Spinneret.Functional;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Options;
using Spinneret.Mediator;

namespace Spinneret.Scheduler.Gcp;

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

    /// <summary>Cancels the job identified by <paramref name="handle"/> within the transaction.</summary>
    void CancelJob(Transaction transaction, string handle);
}

internal sealed class FirestoreTransactionalScheduler(
    FirestoreDb db,
    IOptions<GcpSchedulerOptions> options,
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
