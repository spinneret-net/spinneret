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
    string ScheduleJob<TResponse>(Transaction transaction, IRequest<TResponse> request, DateTimeOffset executeAt);

    /// <summary>
    /// Cancels the job identified by <paramref name="handle"/> within the transaction, by deleting
    /// its document. Cancelling a job that already ran, or an unknown handle, is a silent no-op —
    /// a delete needs no read and does not care whether the document exists.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="handle"/> was not issued by <see cref="ScheduleJob{TResponse}"/>. Recurring keys live in
    /// the same collection, so this guards against a cancel silently destroying a schedule; use
    /// <c>IRecurringJobScheduler.UnregisterAsync</c> for those. The check is on the handle itself
    /// rather than on the stored document because Firestore requires every read in a transaction to
    /// precede every write, and this method enlists in a transaction the caller may already have
    /// written to — so it must stay write-only.
    /// </exception>
    void CancelJob(Transaction transaction, string handle);
}

internal sealed class FirestoreTransactionalScheduler(
    FirestoreDb db,
    IOptions<FirestoreSchedulerOptions> options,
    ScheduledJobDocumentFactory factory)
    : IFirestoreTransactionalScheduler
{
    private CollectionReference Collection => db.Collection(options.Value.Collection);

    public string ScheduleJob<TResponse>(Transaction transaction, IRequest<TResponse> request, DateTimeOffset executeAt)
    {
        var docRef = Collection.Document(ScheduledJob.NewOneShotHandle());
        transaction.Set(docRef, factory.OneShot(request, executeAt));
        return docRef.Id;
    }

    public void CancelJob(Transaction transaction, string handle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        if (!ScheduledJob.IsOneShotHandle(handle))
            throw new ArgumentException(
                $"'{handle}' is not a one-shot job handle. Recurring jobs are retired with UnregisterAsync.",
                nameof(handle));

        transaction.Delete(Collection.Document(handle));
    }
}
