using System.Text.Json;
using Google.Cloud.Tasks.V2;
using Grpc.Core;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;
using CloudTask = Google.Cloud.Tasks.V2.Task;
using GcpHttpMethod = Google.Cloud.Tasks.V2.HttpMethod;

namespace Spinneret.Queue.CloudTasks;

internal sealed class CloudTasksTransport(
    CloudTasksClient client,
    IOptions<CloudTasksOptions> cloudTasksOptions)
    : IQueueTransport
{
    public async System.Threading.Tasks.Task<bool> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        var value = cloudTasksOptions.Value;
        var envelope = message.Envelope;
        var queueId = value.QueueIdFor(message.Channel);

        var task = new CloudTask
        {
            HttpRequest = new HttpRequest
            {
                Url = value.DispatcherUrl,
                HttpMethod = GcpHttpMethod.Post,
                Body = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(envelope)),
                OidcToken = new OidcToken
                {
                    ServiceAccountEmail = value.ServiceAccountEmail,
                    Audience = value.ResolvedOidcAudience,
                },
                Headers =
                {
                    ["Content-Type"] = "application/json"
                }
            },
        };

        // From the envelope, not the ambient activity, so header and body cannot disagree.
        if (envelope.TraceParent is { } traceParent)
        {
            task.HttpRequest.Headers["traceparent"] = traceParent;
            if (envelope.TraceState is { } traceState)
                task.HttpRequest.Headers["tracestate"] = traceState;
        }

        if (message.DeliverAt is { } deliverAt)
            task.ScheduleTime = Timestamp.FromDateTimeOffset(deliverAt);

        var parent = new QueueName(value.ProjectId, value.LocationId, queueId);
        if (message.MessageName is { } name)
            task.Name = new TaskName(value.ProjectId, value.LocationId, queueId, name).ToString();

        try
        {
            await client.CreateTaskAsync(parent.ToString(), task, ct);
            return true;
        }
        catch (RpcException ex) when (message.MessageName is not null && ex.StatusCode == StatusCode.AlreadyExists)
        {
            return false;
        }
    }
}
