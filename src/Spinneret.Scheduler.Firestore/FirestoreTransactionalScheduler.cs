using Google.Cloud.Firestore;
using Microsoft.Extensions.Options;
using Spinneret.Mediator;

namespace Spinneret.Scheduler.Firestore;

/// <summary>
/// Schedules and cancels one-shot jobs in a caller-owned Firestore transaction, committing atomically with the caller's changes.
/// </summary>
public interface IFirestoreTransactionalScheduler
{
    /// <summary>
    /// Writes a one-shot job to run once at <paramref name="executeAt"/> onto <paramref name="transaction"/>.
    /// </summary>
    /// <returns>The handle to pass to <see cref="CancelJob"/>.</returns>
    string ScheduleJob<TResponse>(Transaction transaction, IRequest<TResponse> request, DateTimeOffset executeAt);

    /// <summary>
    /// Cancels the job identified by <paramref name="handle"/> without reading; an unknown or already-run handle is a no-op.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="handle"/> was not issued by <see cref="ScheduleJob{TResponse}"/>; use <c>UnregisterAsync</c> for recurring jobs.
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
