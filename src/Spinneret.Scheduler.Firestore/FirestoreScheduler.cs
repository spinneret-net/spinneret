using Google.Cloud.Firestore;
using Microsoft.Extensions.Options;
using Spinneret.Mediator;

namespace Spinneret.Scheduler.Firestore;

internal sealed class FirestoreScheduler(
    FirestoreDb db,
    IOptions<FirestoreSchedulerOptions> options,
    ScheduledJobDocumentFactory factory,
    TimeProvider timeProvider)
    : IRecurringJobScheduler
{
    public Task RegisterAsync<TResponse>(
        string key, IRequest<TResponse> request, Schedule schedule, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("A recurring job requires a stable key.", nameof(key));
        ArgumentNullException.ThrowIfNull(schedule);

        var docRef = db.Collection(options.Value.Collection).Document(key);
        var definition = factory.RecurringDefinition(request, schedule);
        var scheduleText = schedule.ToString();

        return db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(docRef, ct);

            if (!snapshot.Exists)
            {
                transaction.Set(docRef, new Dictionary<string, object>(definition)
                {
                    [ScheduledJob.Fields.NextExecuteAt] = NextRunFromNow(schedule),
                    [ScheduledJob.Fields.CreatedAt] = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow()),
                });
                return;
            }

            // Idempotent refresh: update the definition in place. Re-arm only if the schedule itself
            // changed; an unchanged schedule keeps its cadence so frequent restarts never reset it.
            // A job that went terminal doesn't reach here at all — it was deleted, so the branch
            // above re-creates it.
            if (StoredSchedule(snapshot) != scheduleText)
                definition[ScheduledJob.Fields.NextExecuteAt] = NextRunFromNow(schedule);

            transaction.Update(docRef, definition);
        }, cancellationToken: ct);
    }

    public Task UnregisterAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("A recurring job requires a stable key.", nameof(key));

        var docRef = db.Collection(options.Value.Collection).Document(key);

        return db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(docRef, ct);

            // Absent is a no-op, and so is a one-shot: one-shot handles live in this same
            // collection, and only a recurring job carries a schedule field.
            if (snapshot.Exists && StoredSchedule(snapshot) is not null)
                transaction.Delete(docRef);
        }, cancellationToken: ct);
    }

    /// <summary>The stored canonical schedule, or null if the document is a one-shot job.</summary>
    private static string? StoredSchedule(DocumentSnapshot snapshot) =>
        snapshot.ContainsField(ScheduledJob.Fields.Schedule)
            ? snapshot.GetValue<string>(ScheduledJob.Fields.Schedule)
            : null;

    private Timestamp NextRunFromNow(Schedule schedule) =>
        Timestamp.FromDateTimeOffset(schedule.NextRun(timeProvider.GetUtcNow()));
}
