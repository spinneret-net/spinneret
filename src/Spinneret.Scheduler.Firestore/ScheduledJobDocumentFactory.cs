using Google.Cloud.Firestore;
using Spinneret.Mediator;
using Spinneret.Queue;

namespace Spinneret.Scheduler.Firestore;

internal sealed class ScheduledJobDocumentFactory(
    QueueTypeRegistry typeRegistry, IQueuePayloadSerializer serializer, TimeProvider timeProvider)
{
    public Dictionary<string, object> OneShot<TResponse>(IRequest<TResponse> request, DateTimeOffset executeAt)
    {
        return new Dictionary<string, object>(Payload(request))
        {
            [ScheduledJob.Fields.NextExecuteAt] = Timestamp.FromDateTimeOffset(executeAt),
            [ScheduledJob.Fields.CreatedAt] = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow()),
        };
    }

    // Run timestamps are left to the caller's register-or-refresh.
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
