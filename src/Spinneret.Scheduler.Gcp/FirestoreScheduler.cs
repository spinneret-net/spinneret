using Google.Cloud.Firestore;
using Microsoft.Extensions.Options;
using NodaTime;
using Spinneret.Mediator;

namespace Spinneret.Scheduler.Gcp;

internal sealed class FirestoreScheduler(
    FirestoreDb db,
    IOptions<GcpSchedulerOptions> options,
    ScheduledJobDocumentFactory factory)
    : IRecurringJobScheduler
{
    public Task RegisterAsync(string key, IRequest<Unit> request, Duration interval, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("A recurring job requires a stable key.", nameof(key));
        if (interval < Duration.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(interval),
                "A recurring interval must be at least one second: the interval is persisted in whole "
                + "seconds, so a sub-second value would degrade the job to a one-shot.");

        var docRef = db.Collection(options.Value.Collection).Document(key);
        var definition = factory.RecurringDefinition(request, interval);

        return db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(docRef, ct);

            if (!snapshot.Exists)
            {
                // First registration: arm the first run one interval out.
                var firstRun = NextRunFromNow(interval);
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
            // incarnation went terminal (e.g. cancelled); an already-pending job keeps its
            // cadence so frequent restarts never reset the schedule.
            if (snapshot.GetValue<string>(ScheduledJob.Fields.Status) != ScheduledJob.StatusValues.Pending)
            {
                var nextRun = NextRunFromNow(interval);
                definition[ScheduledJob.Fields.Status] = ScheduledJob.StatusValues.Pending;
                definition[ScheduledJob.Fields.ExecuteAt] = nextRun;
                definition[ScheduledJob.Fields.NextExecuteAt] = nextRun;
            }

            transaction.Update(docRef, definition);
        }, cancellationToken: ct);
    }

    private static Timestamp NextRunFromNow(Duration interval) =>
        Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.Add(interval.ToTimeSpan()));
}
