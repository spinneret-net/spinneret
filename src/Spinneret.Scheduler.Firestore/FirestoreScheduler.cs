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

            // Re-arm only on a schedule change, so restarts never reset the cadence.
            if (StoredSchedule(snapshot) != scheduleText)
                definition[ScheduledJob.Fields.NextExecuteAt] = NextRunFromNow(schedule);
            else if (Matches(snapshot, definition))
                // Skip the write: Firestore bills identical writes, and instances would contend.
                return;

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

            // One-shots share this collection; only a recurring job has a schedule field.
            if (snapshot.Exists && StoredSchedule(snapshot) is not null)
                transaction.Delete(docRef);
        }, cancellationToken: ct);
    }

    private static bool Matches(DocumentSnapshot snapshot, Dictionary<string, object> definition) =>
        definition.All(field =>
            snapshot.TryGetValue<object>(field.Key, out var stored) && Equals(stored, field.Value));

    private static string? StoredSchedule(DocumentSnapshot snapshot) =>
        snapshot.ContainsField(ScheduledJob.Fields.Schedule)
            ? snapshot.GetValue<string>(ScheduledJob.Fields.Schedule)
            : null;

    private Timestamp NextRunFromNow(Schedule schedule) =>
        Timestamp.FromDateTimeOffset(schedule.NextRun(timeProvider.GetUtcNow()));
}
