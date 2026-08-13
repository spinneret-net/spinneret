using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.Queue.Gcp.Tests;

public sealed class QueueDispatchEndpointTests
{
    private const string TaskNameHeader = "X-CloudTasks-TaskName";
    private const string FullTaskName = "projects/p/locations/l/queues/q/tasks/task-123";

    private sealed class FakeProcessor : IQueueDeliveryProcessor
    {
        public QueueDeliveryOutcome Outcome { get; set; } = QueueDeliveryOutcome.Acked;
        public QueueEnvelope? Envelope { get; private set; }
        public string? TaskId { get; private set; }

        public Task<QueueDeliveryOutcome> ProcessAsync(QueueDeliveryContext context, CancellationToken ct)
        {
            Envelope = context.Envelope;
            TaskId = context.TaskId;
            return Task.FromResult(Outcome);
        }
    }

    private sealed class FakeDeadLetterWriter : IDeadLetterWriter
    {
        public List<DeadLetterEntry> Entries { get; } = [];
        public Exception? ThrowOnWrite { get; set; }

        public Task WriteAsync(DeadLetterEntry entry, CancellationToken ct = default)
        {
            if (ThrowOnWrite is not null)
                throw ThrowOnWrite;

            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class TestEndpointRouteBuilder(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private static (RouteEndpoint Endpoint, IServiceProvider Provider) BuildEndpoint(
        FakeProcessor? processor = null, FakeDeadLetterWriter? deadLetters = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouting();
        services.AddSingleton<IQueueDeliveryProcessor>(processor ?? new FakeProcessor());
        services.AddSingleton<IDeadLetterWriter>(deadLetters ?? new FakeDeadLetterWriter());
        var provider = services.BuildServiceProvider();

        var builder = new TestEndpointRouteBuilder(provider);
        builder.MapGcpQueueDispatch();

        var endpoint = (RouteEndpoint)builder.DataSources.Single().Endpoints.Single();
        return (endpoint, provider);
    }

    private static async Task<DefaultHttpContext> Invoke(
        RouteEndpoint endpoint, IServiceProvider provider, string body, string? taskName = FullTaskName)
    {
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        if (taskName is not null)
            context.Request.Headers[TaskNameHeader] = taskName;

        await endpoint.RequestDelegate!(context);
        return context;
    }

    private static string EnvelopeJson(QueueEnvelope envelope) => JsonSerializer.Serialize(envelope);

    private static QueueEnvelope SampleEnvelope() => new()
    {
        RequestTypeName = typeof(PlainCommand).FullName!,
        PayloadJson = """{"name":"widget","count":1}""",
        EnqueuedAtUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        PriorFailures = 2,
        TraceId = "trace-1",
        Description = "sample",
    };

    [Test]
    public async Task MapGcpQueueDispatch_maps_post_route_guarded_by_oidc_policy()
    {
        var (endpoint, _) = BuildEndpoint();

        await Assert.That(endpoint.RoutePattern.RawText).IsEqualTo("/internal/queue/dispatch");

        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>();
        await Assert.That(methods).IsNotNull();
        await Assert.That(methods!.HttpMethods).Contains("POST");

        var authorize = endpoint.Metadata.GetMetadata<IAuthorizeData>();
        await Assert.That(authorize).IsNotNull();
        await Assert.That(authorize!.Policy).IsEqualTo(OidcAuthSetup.PolicyName);

        await Assert.That(endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()).IsNotNull();
    }

    [Test]
    public async Task MapGcpQueueDispatch_returns_builder_for_chaining()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouting();
        services.AddSingleton<IDeadLetterWriter, FakeDeadLetterWriter>();
        var builder = new TestEndpointRouteBuilder(services.BuildServiceProvider());

        var returned = builder.MapGcpQueueDispatch();

        await Assert.That(ReferenceEquals(returned, builder)).IsTrue();
    }

    [Test]
    public async Task MapGcpQueueDispatch_without_dead_letter_writer_fails_at_map_time()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouting();
        var builder = new TestEndpointRouteBuilder(services.BuildServiceProvider());

        var ex = Assert.Throws<InvalidOperationException>(() => builder.MapGcpQueueDispatch());

        await Assert.That(ex.Message).Contains("IDeadLetterWriter");
    }

    [Test]
    public async Task MapGcpQueueDispatch_maps_a_custom_route_pattern()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouting();
        services.AddSingleton<IDeadLetterWriter, FakeDeadLetterWriter>();
        var builder = new TestEndpointRouteBuilder(services.BuildServiceProvider());

        builder.MapGcpQueueDispatch("/hooks/queue");

        var endpoint = builder.DataSources
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Single();
        await Assert.That(endpoint.RoutePattern.RawText).IsEqualTo("/hooks/queue");
    }

    [Test]
    public async Task Handle_acked_delivery_returns_200_and_passes_envelope_and_task_id()
    {
        var processor = new FakeProcessor();
        var (endpoint, provider) = BuildEndpoint(processor);
        var envelope = SampleEnvelope();

        var context = await Invoke(endpoint, provider, EnvelopeJson(envelope));

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status200OK);
        await Assert.That(processor.Envelope).IsEqualTo(envelope);
        await Assert.That(processor.TaskId).IsEqualTo("task-123");
    }

    [Test]
    public async Task Handle_retry_outcome_returns_429_with_retry_after_seconds()
    {
        var processor = new FakeProcessor { Outcome = QueueDeliveryOutcome.RetryIn(TimeSpan.FromSeconds(90)) };
        var (endpoint, provider) = BuildEndpoint(processor);

        var context = await Invoke(endpoint, provider, EnvelopeJson(SampleEnvelope()));

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status429TooManyRequests);
        await Assert.That(context.Response.Headers.RetryAfter.ToString()).IsEqualTo("90");
    }

    [Test]
    [Arguments(0, "1")]
    [Arguments(200, "1")]
    [Arguments(90_500, "91")]
    public async Task Handle_retry_after_rounds_up_to_at_least_one_second(int retryMilliseconds, string expectedHeader)
    {
        var processor = new FakeProcessor
        {
            Outcome = QueueDeliveryOutcome.RetryIn(TimeSpan.FromMilliseconds(retryMilliseconds)),
        };
        var (endpoint, provider) = BuildEndpoint(processor);

        var context = await Invoke(endpoint, provider, EnvelopeJson(SampleEnvelope()));

        await Assert.That(context.Response.Headers.RetryAfter.ToString()).IsEqualTo(expectedHeader);
    }

    [Test]
    public async Task Handle_uses_full_header_value_as_task_id_when_it_has_no_slash()
    {
        var processor = new FakeProcessor();
        var (endpoint, provider) = BuildEndpoint(processor);

        await Invoke(endpoint, provider, EnvelopeJson(SampleEnvelope()), taskName: "plain-task-id");

        await Assert.That(processor.TaskId).IsEqualTo("plain-task-id");
    }

    [Test]
    public async Task Handle_generates_guid_task_id_when_header_missing()
    {
        var processor = new FakeProcessor();
        var (endpoint, provider) = BuildEndpoint(processor);

        await Invoke(endpoint, provider, EnvelopeJson(SampleEnvelope()), taskName: null);

        await Assert.That(Guid.TryParse(processor.TaskId, out _)).IsTrue();
    }

    [Test]
    public async Task Handle_dead_letters_malformed_body_and_acks()
    {
        var processor = new FakeProcessor();
        var deadLetters = new FakeDeadLetterWriter();
        var (endpoint, provider) = BuildEndpoint(processor, deadLetters);
        const string body = "{not valid json";

        var context = await Invoke(endpoint, provider, body);

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status200OK);
        await Assert.That(processor.Envelope).IsNull();

        var entry = deadLetters.Entries.Single();
        await Assert.That(entry.CommandTypeName).IsEqualTo("<unreadable envelope>");
        await Assert.That(entry.PayloadJson).IsEqualTo(body);
        await Assert.That(entry.Source).IsEqualTo(DeadLetterSource.Queue);
        await Assert.That(entry.Attempts).IsEqualTo(1);
        await Assert.That(entry.IdempotencyKey).IsEqualTo("task-123");
    }

    [Test]
    public async Task Handle_dead_letters_null_envelope_with_explicit_error()
    {
        var deadLetters = new FakeDeadLetterWriter();
        var (endpoint, provider) = BuildEndpoint(deadLetters: deadLetters);

        var context = await Invoke(endpoint, provider, "null");

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status200OK);
        await Assert.That(deadLetters.Entries.Single().Error).IsEqualTo("Envelope deserialized to null.");
    }

    [Test]
    public async Task Handle_returns_429_with_one_minute_backoff_when_dead_letter_write_fails()
    {
        var deadLetters = new FakeDeadLetterWriter { ThrowOnWrite = new InvalidOperationException("store down") };
        var (endpoint, provider) = BuildEndpoint(deadLetters: deadLetters);

        var context = await Invoke(endpoint, provider, "{not valid json");

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status429TooManyRequests);
        await Assert.That(context.Response.Headers.RetryAfter.ToString()).IsEqualTo("60");
    }
}
