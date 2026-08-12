using Google.Cloud.Firestore;
using NodaTime;
using Spinneret.Mediator;
using Spinneret.Queue;

namespace Spinneret.Scheduler.Gcp;

/// <summary>
/// Builds scheduled-job document field maps from a mediator request — the single place a job
/// document is shaped, shared by the standalone <see cref="FirestoreScheduler"/> and the
/// transaction-enlisted <see cref="FirestoreTransactionalScheduler"/>.
/// </summary>
internal sealed class ScheduledJobDocumentFactory(QueueTypeRegistry typeRegistry, IQueuePayloadSerializer serializer)
{
    /// <summary>Fields for a one-shot job that runs once at <paramref name="executeAt"/>.</summary>
    public Dictionary<string, object> OneShot(IRequest<Unit> request, Instant executeAt)
    {
        var ts = Timestamp.FromDateTimeOffset(executeAt.ToDateTimeOffset());
        return new Dictionary<string, object>(Payload(request))
        {
            [ScheduledJob.Fields.Status] = ScheduledJob.StatusValues.Pending,
            [ScheduledJob.Fields.ExecuteAt] = ts,
            [ScheduledJob.Fields.NextExecuteAt] = ts,
            [ScheduledJob.Fields.CreatedAt] = Timestamp.GetCurrentTimestamp(),
        };
    }

    /// <summary>
    /// The recurrence-defining fields (type, payload, interval) for a recurring job. The status and
    /// run timestamps are owned by the caller's idempotent register-or-refresh, not by this builder.
    /// The interval is persisted in whole seconds, so it must be at least one second — a sub-second
    /// value would persist as intervalSeconds = 0, which the dispatcher treats as a one-shot job.
    /// </summary>
    public Dictionary<string, object> RecurringDefinition(IRequest<Unit> request, Duration interval)
    {
        if (interval < Duration.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(interval),
                "A recurring interval must be at least one second: the interval is persisted in whole "
                + "seconds, so a sub-second value would degrade the job to a one-shot.");

        return new Dictionary<string, object>(Payload(request))
        {
            [ScheduledJob.Fields.IntervalSeconds] = (long)interval.TotalSeconds,
        };
    }

    private Dictionary<string, object> Payload(IRequest<Unit> request)
    {
        var requestType = request.GetType();
        return new Dictionary<string, object>
        {
            [ScheduledJob.Fields.RequestTypeName] = typeRegistry.GetName(requestType),
            [ScheduledJob.Fields.PayloadJson] = serializer.Serialize(request, requestType),
        };
    }
}
