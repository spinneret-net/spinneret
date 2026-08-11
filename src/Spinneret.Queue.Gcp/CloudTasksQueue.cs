using System.Diagnostics;
using System.Text.Json;
using Google.Cloud.Tasks.V2;
using Grpc.Core;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spinneret.Mediator;
using CloudTask = Google.Cloud.Tasks.V2.Task;
using GcpHttpMethod = Google.Cloud.Tasks.V2.HttpMethod;

namespace Spinneret.Queue.Gcp;

internal sealed class CloudTasksQueue(
    CloudTasksClient client,
    IOptions<GcpQueueOptions> gcpOptions,
    IQueuePayloadSerializer serializer,
    QueueTypeRegistry registry,
    ILogger<CloudTasksQueue> logger)
    : IQueue, IEnvelopeQueue
{
    public System.Threading.Tasks.Task Enqueue(IRequest<Unit> request, QueueOptions? options = null, CancellationToken ct = default)
        => Enqueue<Unit>(request, options, ct);

    public async System.Threading.Tasks.Task Enqueue<TResponse>(IRequest<TResponse> request, QueueOptions? queueOptions = null, CancellationToken ct = default)
    {
        var requestType = request.GetType();
        var typeName = registry.GetName(requestType);

        var payloadJson = serializer.Serialize(request, requestType);
        var envelope = new QueueEnvelope
        {
            RequestTypeName = typeName,
            PayloadJson = payloadJson,
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
            TraceId = Activity.Current?.Id,
            Description = queueOptions?.Description,
        };

        await EnqueueEnvelope(envelope, queueOptions?.Delay, queueOptions?.DedupeKey, ct);
    }

    public System.Threading.Tasks.Task Enqueue(QueueEnvelope envelope, TimeSpan? delay = null, CancellationToken ct = default)
        => EnqueueEnvelope(envelope, delay, dedupeKey: null, ct);

    private async System.Threading.Tasks.Task EnqueueEnvelope(
        QueueEnvelope envelope, TimeSpan? delay, string? dedupeKey, CancellationToken ct)
    {
        var value = gcpOptions.Value;
        var channel = registry.Resolve(envelope.RequestTypeName).Policy.ResolvedChannel;
        var queueId = value.QueueIdFor(channel);

        var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(envelope);

        var task = new CloudTask
        {
            HttpRequest = new HttpRequest
            {
                Url = value.DispatcherUrl,
                HttpMethod = GcpHttpMethod.Post,
                Body = ByteString.CopyFrom(envelopeBytes),
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

        if (delay is { } scheduleDelay && scheduleDelay > TimeSpan.Zero)
            task.ScheduleTime = Timestamp.FromDateTime(DateTime.UtcNow + scheduleDelay);

        var parent = new QueueName(value.ProjectId, value.LocationId, queueId);
        var isDeduped = !string.IsNullOrWhiteSpace(dedupeKey);
        if (isDeduped)
            task.Name = new TaskName(value.ProjectId, value.LocationId, queueId, dedupeKey).ToString();

        try
        {
            await client.CreateTaskAsync(parent.ToString(), task, ct);
        }
        catch (RpcException ex) when (isDeduped && ex.StatusCode == StatusCode.AlreadyExists)
        {
            // A task with this dedupe key already exists (or recently did) — the documented
            // DedupeKey contract is "only one delivery", so this is success, not an error.
            logger.LogDebug(
                "Skipped enqueue of {RequestType} to {QueueId}: dedupe key {DedupeKey} already enqueued",
                envelope.RequestTypeName, queueId, dedupeKey);
        }
    }
}
