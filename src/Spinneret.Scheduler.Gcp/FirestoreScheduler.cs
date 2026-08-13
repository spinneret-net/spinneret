using Google.Cloud.Firestore;
using Microsoft.Extensions.Options;
using Spinneret.Mediator;

namespace Spinneret.Scheduler.Gcp;

internal sealed class FirestoreScheduler(
    FirestoreDb db,
    IOptions<GcpSchedulerOptions> options,
    ScheduledJobDocumentFactory factory)
    : IRecurringJobScheduler
{
    public Task RegisterAsync(string key, IRequest<Unit> request, Schedule schedule, CancellationToken ct = default)
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
                var firstRun = NextRunFromNow(schedule);
                transaction.Set(docRef, new Dictionary<string, object>(definition)
                {
                    [ScheduledJob.Fields.Status] = ScheduledJob.StatusValues.Pending,
                    [ScheduledJob.Fields.ExecuteAt] = firstRun,
                    [ScheduledJob.Fields.NextExecuteAt] = firstRun,
                    [ScheduledJob.Fields.CreatedAt] = Timestamp.GetCurrentTimestamp(),
                });
                return;
            }

            // Idempotent refresh: update the definition in place. Re-arm only if a previous
            // incarnation went terminal (e.g. cancelled) or the schedule itself changed; a pending
            // job with an unchanged schedule keeps its cadence so frequent restarts never reset it.
            if (snapshot.GetValue<string>(ScheduledJob.Fields.Status) != ScheduledJob.StatusValues.Pending
                || StoredSchedule(snapshot) != scheduleText)
            {
                var nextRun = NextRunFromNow(schedule);
                definition[ScheduledJob.Fields.Status] = ScheduledJob.StatusValues.Pending;
                definition[ScheduledJob.Fields.ExecuteAt] = nextRun;
                definition[ScheduledJob.Fields.NextExecuteAt] = nextRun;
            }

            transaction.Update(docRef, definition);
        }, cancellationToken: ct);
    }

    /// <summary>The stored canonical schedule, or null if the document is a one-shot job.</summary>
    private static string? StoredSchedule(DocumentSnapshot snapshot) =>
        snapshot.ContainsField(ScheduledJob.Fields.Schedule)
            ? snapshot.GetValue<string>(ScheduledJob.Fields.Schedule)
            : null;

    private static Timestamp NextRunFromNow(Schedule schedule) =>
        Timestamp.FromDateTimeOffset(schedule.NextRun(DateTimeOffset.UtcNow));
}
