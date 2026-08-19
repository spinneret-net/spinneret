using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Google.Cloud.Tasks.V2;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using GcpHttpMethod = Google.Cloud.Tasks.V2.HttpMethod;
using Task = System.Threading.Tasks.Task;

namespace Spinneret.Queue.Gcp.Tests;

public sealed class CloudTasksQueueTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static (IQueue Queue, IEnvelopeQueue Envelopes, FakeCloudTasksClient Client) CreateQueue(
        Action<Dictionary<string, string?>>? mutateConfig = null,
        Assembly? requestAssembly = null)
    {
        var client = new FakeCloudTasksClient();
        var provider = TestSetup.BuildProvider(TestSetup.Config(mutateConfig), client, requestAssembly: requestAssembly);

        return (provider.GetRequiredService<IQueue>(), provider.GetRequiredService<IEnvelopeQueue>(), client);
    }

    private static QueueEnvelope ReadEnvelope(CreateTaskRequest request)
        => JsonSerializer.Deserialize<QueueEnvelope>(request.Task.HttpRequest.Body.ToByteArray())!;

    [Test]
    public async Task Enqueue_builds_post_task_targeting_dispatcher_url()
    {
        var (queue, _, client) = CreateQueue();

        await queue.Enqueue(new PlainCommand("widget", 1));

        var request = client.SingleRequest;
        await Assert.That(request.Parent)
            .IsEqualTo($"projects/{TestSetup.ProjectId}/locations/{TestSetup.LocationId}/queues/{TestSetup.DefaultQueueId}");
        await Assert.That(request.Task.HttpRequest.Url).IsEqualTo(TestSetup.DispatcherUrl);
        await Assert.That(request.Task.HttpRequest.HttpMethod).IsEqualTo(GcpHttpMethod.Post);
        await Assert.That(request.Task.HttpRequest.Headers["Content-Type"]).IsEqualTo("application/json");
    }

    [Test]
    public async Task Enqueue_serializes_envelope_with_type_name_and_payload()
    {
        var (queue, _, client) = CreateQueue();
        var before = DateTimeOffset.UtcNow;

        await queue.Enqueue(new PlainCommand("widget", 3));

        var after = DateTimeOffset.UtcNow;
        var envelope = ReadEnvelope(client.SingleRequest);
        await Assert.That(envelope.RequestTypeName).IsEqualTo(typeof(PlainCommand).FullName!);
        await Assert.That(envelope.PriorFailures).IsEqualTo(0);
        await Assert.That(envelope.EnqueuedAtUtc >= before && envelope.EnqueuedAtUtc <= after).IsTrue();

        var payload = JsonSerializer.Deserialize<PlainCommand>(envelope.PayloadJson, WebJson);
        await Assert.That(payload).IsEqualTo(new PlainCommand("widget", 3));
    }

    [Test]
    public async Task Enqueue_configures_oidc_token_with_audience_defaulting_to_dispatcher_url()
    {
        var (queue, _, client) = CreateQueue();

        await queue.Enqueue(new PlainCommand("widget", 1));

        var oidc = client.SingleRequest.Task.HttpRequest.OidcToken;
        await Assert.That(oidc.ServiceAccountEmail).IsEqualTo(TestSetup.ServiceAccountEmail);
        await Assert.That(oidc.Audience).IsEqualTo(TestSetup.DispatcherUrl);
    }

    [Test]
    public async Task Enqueue_uses_explicit_oidc_audience_when_configured()
    {
        var (queue, _, client) = CreateQueue(values => values["Queue:Gcp:OidcAudience"] = "custom-audience");

        await queue.Enqueue(new PlainCommand("widget", 1));

        await Assert.That(client.SingleRequest.Task.HttpRequest.OidcToken.Audience).IsEqualTo("custom-audience");
    }

    [Test]
    public async Task Enqueue_without_options_leaves_schedule_time_and_name_unset()
    {
        var (queue, _, client) = CreateQueue();

        await queue.Enqueue(new PlainCommand("widget", 1));

        await Assert.That(client.SingleRequest.Task.ScheduleTime).IsNull();
        await Assert.That(client.SingleRequest.Task.Name).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Enqueue_with_delay_sets_schedule_time_in_the_future()
    {
        var (queue, _, client) = CreateQueue();
        var delay = TimeSpan.FromMinutes(10);
        var before = DateTimeOffset.UtcNow;

        await queue.Enqueue(new PlainCommand("widget", 1), new QueueOptions { Delay = delay });

        var after = DateTimeOffset.UtcNow;
        var scheduled = client.SingleRequest.Task.ScheduleTime.ToDateTimeOffset();
        await Assert.That(scheduled >= before + delay).IsTrue();
        await Assert.That(scheduled <= after + delay).IsTrue();
    }

    [Test]
    [Arguments(0)]
    [Arguments(-30)]
    public async Task Enqueue_with_non_positive_delay_leaves_schedule_time_unset(int delaySeconds)
    {
        var (queue, _, client) = CreateQueue();

        await queue.Enqueue(
            new PlainCommand("widget", 1),
            new QueueOptions { Delay = TimeSpan.FromSeconds(delaySeconds) });

        await Assert.That(client.SingleRequest.Task.ScheduleTime).IsNull();
    }

    [Test]
    public async Task Enqueue_with_dedupe_key_sets_full_task_name()
    {
        var (queue, _, client) = CreateQueue();

        await queue.Enqueue(new PlainCommand("widget", 1), new QueueOptions { DedupeKey = "my-key" });

        await Assert.That(client.SingleRequest.Task.Name).IsEqualTo(
            $"projects/{TestSetup.ProjectId}/locations/{TestSetup.LocationId}/queues/{TestSetup.DefaultQueueId}/tasks/my-key");
    }

    [Test]
    public async Task Enqueue_with_whitespace_dedupe_key_leaves_task_name_unset()
    {
        var (queue, _, client) = CreateQueue();

        await queue.Enqueue(new PlainCommand("widget", 1), new QueueOptions { DedupeKey = "   " });

        await Assert.That(client.SingleRequest.Task.Name).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Enqueue_routes_channel_command_to_mapped_queue()
    {
        var (queue, _, client) = CreateQueue();

        await queue.Enqueue(new BulkCommand("bulk-1"));

        await Assert.That(client.SingleRequest.Parent)
            .IsEqualTo($"projects/{TestSetup.ProjectId}/locations/{TestSetup.LocationId}/queues/{TestSetup.BulkQueueId}");
    }

    [Test]
    public async Task Enqueue_captures_description_and_current_trace_id()
    {
        var (queue, _, client) = CreateQueue();
        using var activity = new Activity("test-activity");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();

        await queue.Enqueue(
            new PlainCommand("widget", 1),
            new QueueOptions { Description = "widget sync" });

        var envelope = ReadEnvelope(client.SingleRequest);
        await Assert.That(envelope.Description).IsEqualTo("widget sync");

        // The trace is what must survive the hop, not one particular span: when a listener records
        // the publish span, that span - not the caller's activity - is current at capture, so the
        // envelope points at the publish. Asserting the exact id would encode "no listener present".
        await Assert.That(ActivityContext.TryParse(envelope.TraceParent, null, out var captured)).IsTrue();
        await Assert.That(captured.TraceId).IsEqualTo(activity.TraceId);

        // The same context rides the delivery request in-band, so the dispatch endpoint's own server
        // span joins this trace instead of starting a fresh root.
        await Assert.That(client.SingleRequest.Task.HttpRequest.Headers["traceparent"])
            .IsEqualTo(envelope.TraceParent);
    }

    [Test]
    public async Task Enqueue_request_with_non_unit_response_uses_same_pipeline()
    {
        var (queue, _, client) = CreateQueue();

        await queue.Enqueue(new StringResponseCommand("value"));

        var envelope = ReadEnvelope(client.SingleRequest);
        await Assert.That(envelope.RequestTypeName).IsEqualTo(typeof(StringResponseCommand).FullName!);
    }

    [Test]
    public async Task Enqueue_unregistered_request_type_throws()
    {
        // Registry built from Spinneret.Queue itself, which contains no IRequest<> types.
        var (queue, _, _) = CreateQueue(requestAssembly: typeof(QueueEnvelope).Assembly);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await queue.Enqueue(new PlainCommand("widget", 1)));

        await Assert.That(ex!.Message).Contains("not registered");
    }

    [Test]
    public async Task Enqueue_swallows_already_exists_for_deduped_task()
    {
        var (queue, _, client) = CreateQueue();
        client.ThrowOnCreateTask = new RpcException(new Status(StatusCode.AlreadyExists, "task exists"));

        await queue.Enqueue(new PlainCommand("widget", 1), new QueueOptions { DedupeKey = "my-key" });

        await Assert.That(client.Requests).IsEmpty();
    }

    [Test]
    public async Task Enqueue_propagates_already_exists_without_dedupe_key()
    {
        var (queue, _, client) = CreateQueue();
        client.ThrowOnCreateTask = new RpcException(new Status(StatusCode.AlreadyExists, "task exists"));

        var ex = await Assert.ThrowsAsync<RpcException>(
            async () => await queue.Enqueue(new PlainCommand("widget", 1)));

        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.AlreadyExists);
    }

    [Test]
    public async Task Enqueue_propagates_other_rpc_errors_for_deduped_task()
    {
        var (queue, _, client) = CreateQueue();
        client.ThrowOnCreateTask = new RpcException(new Status(StatusCode.Unavailable, "down"));

        var ex = await Assert.ThrowsAsync<RpcException>(
            async () => await queue.Enqueue(new PlainCommand("widget", 1), new QueueOptions { DedupeKey = "my-key" }));

        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.Unavailable);
    }

    [Test]
    public async Task Envelope_enqueue_preserves_all_envelope_fields()
    {
        var (_, envelopes, client) = CreateQueue();
        var original = new QueueEnvelope
        {
            RequestTypeName = typeof(PlainCommand).FullName!,
            PayloadJson = """{"name":"widget","count":1}""",
            EnqueuedAtUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            PriorFailures = 3,
            TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            Description = "retry generation",
        };

        await envelopes.Enqueue(original, TimeSpan.FromMinutes(5));

        var roundTripped = ReadEnvelope(client.SingleRequest);
        await Assert.That(roundTripped).IsEqualTo(original);
        await Assert.That(client.SingleRequest.Task.ScheduleTime).IsNotNull();
    }

    [Test]
    public async Task Envelope_enqueue_routes_by_registered_policy_channel()
    {
        var (_, envelopes, client) = CreateQueue();
        var envelope = new QueueEnvelope
        {
            RequestTypeName = typeof(BulkCommand).FullName!,
            PayloadJson = """{"id":"bulk-1"}""",
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
        };

        await envelopes.Enqueue(envelope);

        await Assert.That(client.SingleRequest.Parent)
            .IsEqualTo($"projects/{TestSetup.ProjectId}/locations/{TestSetup.LocationId}/queues/{TestSetup.BulkQueueId}");
    }
}
