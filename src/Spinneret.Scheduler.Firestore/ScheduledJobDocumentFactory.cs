using Google.Cloud.Firestore;
using Spinneret.Mediator;
using Spinneret.Queue;

namespace Spinneret.Scheduler.Firestore;

/// <summary>
/// Builds scheduled-job document field maps from a mediator request — the single place a job
/// document is shaped, shared by the standalone <see cref="FirestoreScheduler"/> and the
/// transaction-enlisted <see cref="FirestoreTransactionalScheduler"/>.
/// </summary>
internal sealed class ScheduledJobDocumentFactory(
    QueueTypeRegistry typeRegistry, IQueuePayloadSerializer serializer, TimeProvider timeProvider)
{
    /// <summary>Fields for a one-shot job that runs once at <paramref name="executeAt"/>.</summary>
    public Dictionary<string, object> OneShot<TResponse>(IRequest<TResponse> request, DateTimeOffset executeAt)
    {
        return new Dictionary<string, object>(Payload(request))
        {
            [ScheduledJob.Fields.NextExecuteAt] = Timestamp.FromDateTimeOffset(executeAt),
            [ScheduledJob.Fields.CreatedAt] = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow()),
        };
    }

    /// <summary>
    /// The recurrence-defining fields (type, payload, schedule) for a recurring job. The run
    /// timestamps are owned by the caller's idempotent register-or-refresh, not by this builder.
    /// </summary>
    public Dictionary<string, object> RecurringDefinition<TResponse>(IRequest<TResponse> request, Schedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return new Dictionary<string, object>(Payload(request))
        {
            [ScheduledJob.Fields.Schedule] = schedule.ToString(),
        };
    }

    private Dictionary<string, object> Payload<TResponse>(IRequest<TResponse> request)
    {
        var requestType = request.GetType();
        return new Dictionary<string, object>
        {
            [ScheduledJob.Fields.RequestTypeName] = typeRegistry.GetName(requestType),
            [ScheduledJob.Fields.PayloadJson] = serializer.Serialize(request, requestType),
        };
    }
}
