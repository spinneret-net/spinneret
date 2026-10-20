using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.Queue.Tests;

public class QueueDeliveryTracingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeDispatcher _dispatcher = new();
    private readonly FakeEnvelopeQueue _envelopeQueue = new();
    private readonly FakeDeadLetterWriter _deadLetters = new();

    private IQueueDeliveryProcessor CreateProcessor()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IQueueDispatcher>(_dispatcher);
        services.AddSingleton<IEnvelopeQueue>(_envelopeQueue);
        services.AddSingleton<IDeadLetterWriter>(_deadLetters);
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        services.AddNullLogging();
        services.AddQueueCore(new QueueTypeRegistry([typeof(QueueDeliveryTracingTests).Assembly]));

        return services.BuildServiceProvider().GetRequiredService<IQueueDeliveryProcessor>();
    }

    private static QueueEnvelope Envelope<TCommand>(
        string? traceParent = null, string? traceState = null, int priorFailures = 0) => new()
    {
        RequestTypeName = typeof(TCommand).FullName!,
        PayloadJson = """{"id":42}""",
        EnqueuedAtUtc = Now,
        PriorFailures = priorFailures,
        TraceParent = traceParent,
        TraceState = traceState,
    };

    private static string TraceParentFor(ActivityTraceId traceId, ActivitySpanId spanId) =>
        $"00-{traceId}-{spanId}-01";

    [Test]
    public async Task Handler_runs_in_the_trace_the_producer_recorded()
    {
        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();

        await CreateProcessor().ProcessAsync(
            Envelope<UnannotatedCommand>(TraceParentFor(traceId, spanId)), "task-1", CancellationToken.None);

        await Assert.That(_dispatcher.ObservedContext.TraceId).IsEqualTo(traceId);
        await Assert.That(_dispatcher.ObservedParentSpanId).IsEqualTo(spanId);
    }

    [Test]
    public async Task Tracestate_rides_along_with_the_traceparent()
    {
        var traceId = ActivityTraceId.CreateRandom();

        await CreateProcessor().ProcessAsync(
            Envelope<UnannotatedCommand>(TraceParentFor(traceId, ActivitySpanId.CreateRandom()), "vendor=value"),
            "task-1", CancellationToken.None);

        await Assert.That(_dispatcher.ObservedContext.TraceState).IsEqualTo("vendor=value");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("garbage")]
    [Arguments("|hierarchical.1.")]
    [Arguments("00-00000000000000000000000000000000-00f067aa0ba902b7-01")]
    public async Task An_unusable_traceparent_still_dispatches(string? traceParent)
    {
        // A malformed value is a diagnostics problem; losing the message would be a business one.
        var outcome = await CreateProcessor().ProcessAsync(
            Envelope<UnannotatedCommand>(traceParent), "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(_dispatcher.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task A_booked_retry_stays_in_the_original_trace()
    {
        var traceParent = TraceParentFor(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom());
        _dispatcher.Throw = new InvalidOperationException("boom");

        await CreateProcessor().ProcessAsync(
            Envelope<UnannotatedCommand>(traceParent), "task-1", CancellationToken.None);

        // Every attempt of a task has to answer one trace query.
        await Assert.That(Expect.Single(_envelopeQueue.Enqueued).Envelope.TraceParent).IsEqualTo(traceParent);
    }

    [Test]
    public async Task A_deferral_stays_in_the_original_trace()
    {
        var traceParent = TraceParentFor(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom());
        _dispatcher.Throw = new QueueHandlerRetryAfterException(TimeSpan.FromMinutes(5));

        await CreateProcessor().ProcessAsync(
            Envelope<UnannotatedCommand>(traceParent), "task-1", CancellationToken.None);

        await Assert.That(Expect.Single(_envelopeQueue.Enqueued).Envelope.TraceParent).IsEqualTo(traceParent);
    }

    [Test]
    public async Task A_dead_letter_records_the_trace_id_to_search_logs_by()
    {
        var traceId = ActivityTraceId.CreateRandom();
        _dispatcher.Throw = new QueueHandlerPermanentException("unrecoverable");

        await CreateProcessor().ProcessAsync(
            Envelope<UnannotatedCommand>(TraceParentFor(traceId, ActivitySpanId.CreateRandom())),
            "task-1", CancellationToken.None);

        // The trace id, not the traceparent: this is what an operator searches logs by.
        await Assert.That(Expect.Single(_deadLetters.Entries).TraceId).IsEqualTo(traceId.ToHexString());
    }

    [Test]
    public async Task A_dead_letter_without_a_traceparent_falls_back_to_the_ambient_trace()
    {
        using var ambient = new Activity("caller");
        ambient.SetIdFormat(ActivityIdFormat.W3C);
        ambient.Start();

        _dispatcher.Throw = new QueueHandlerPermanentException("unrecoverable");

        await CreateProcessor().ProcessAsync(
            Envelope<UnannotatedCommand>(traceParent: null), "task-1", CancellationToken.None);

        await Assert.That(Expect.Single(_deadLetters.Entries).TraceId).IsEqualTo(ambient.TraceId.ToHexString());
    }

    // Tags spelled as literals: docs/queue.md publishes them, so a rename must fail here.

    private async Task<Activity> ProcessAndCollect<TCommand>(string taskId, int priorFailures = 0)
    {
        using var spans = new SpanCollector();
        await CreateProcessor().ProcessAsync(
            Envelope<TCommand>(priorFailures: priorFailures), taskId, CancellationToken.None);

        return spans.TaggedWith("messaging.message.id", taskId);
    }

    [Test]
    public async Task A_successful_delivery_is_tagged_acked()
    {
        var span = await ProcessAndCollect<UnannotatedCommand>("outcome-ack");

        await Assert.That(span.GetTagItem("spinneret.queue.outcome")).IsEqualTo("ack");
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Unset);
    }

    [Test]
    public async Task A_booked_retry_is_tagged_retry_and_marked_failed()
    {
        _dispatcher.Throw = new InvalidOperationException("boom");

        var span = await ProcessAndCollect<UnannotatedCommand>("outcome-retry");

        await Assert.That(span.GetTagItem("spinneret.queue.outcome")).IsEqualTo("retry");
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Error);
    }

    [Test]
    public async Task A_deferral_is_tagged_defer_without_failing_the_span()
    {
        _dispatcher.Throw = new QueueHandlerRetryAfterException(TimeSpan.FromMinutes(5));

        var span = await ProcessAndCollect<UnannotatedCommand>("outcome-defer");

        await Assert.That(span.GetTagItem("spinneret.queue.outcome")).IsEqualTo("defer");
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Unset);
    }

    [Test]
    public async Task A_dead_letter_is_tagged_deadletter()
    {
        _dispatcher.Throw = new QueueHandlerPermanentException("unrecoverable");

        var span = await ProcessAndCollect<UnannotatedCommand>("outcome-deadletter");

        await Assert.That(span.GetTagItem("spinneret.queue.outcome")).IsEqualTo("deadletter");
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Error);
    }

    [Test]
    public async Task A_discard_is_tagged_discard()
    {
        _dispatcher.Throw = new QueueHandlerFailedException("SomeBusinessError");

        var span = await ProcessAndCollect<AnnotatedCommand>("outcome-discard");

        await Assert.That(span.GetTagItem("spinneret.queue.outcome")).IsEqualTo("discard");
    }

    [Test]
    public async Task Falling_back_to_transport_redelivery_is_tagged_transport_retry()
    {
        _dispatcher.Throw = new InvalidOperationException("boom");
        _envelopeQueue.Throw = new InvalidOperationException("queue down");

        var span = await ProcessAndCollect<UnannotatedCommand>("outcome-transport-retry");

        await Assert.That(span.GetTagItem("spinneret.queue.outcome")).IsEqualTo("transport-retry");
    }

    [Test]
    public async Task The_consumer_span_carries_the_message_the_channel_and_the_attempt()
    {
        var span = await ProcessAndCollect<AnnotatedCommand>("outcome-tags", priorFailures: 1);

        await Assert.That(span.DisplayName).IsEqualTo("AnnotatedCommand process");
        await Assert.That(span.Kind).IsEqualTo(ActivityKind.Consumer);
        await Assert.That(span.GetTagItem("messaging.system")).IsEqualTo("spinneret");
        await Assert.That(span.GetTagItem("messaging.operation")).IsEqualTo("process");
        await Assert.That(span.GetTagItem("messaging.destination.name")).IsEqualTo("test-channel");
        await Assert.That(span.GetTagItem("spinneret.request.type")).IsEqualTo(typeof(AnnotatedCommand).FullName);
        await Assert.That(span.GetTagItem("spinneret.queue.attempt")).IsEqualTo(2);
        await Assert.That(span.GetTagItem("spinneret.queue.max_attempts")).IsEqualTo(2);
    }

    [Test]
    public async Task An_unsampled_delivery_records_its_outcome_nowhere()
    {
        // Reaching for Activity.Current would fail the host's own span. Asserted on SetOutcome directly
        // because the test host's catch-all listener means the span can't be suppressed in-process.
        using var host = new Activity("host request");
        host.SetIdFormat(ActivityIdFormat.W3C);
        host.Start();

        Activity? unsampled = null;
        unsampled.SetOutcome(QueueOutcome.DeadLetter, "unrecoverable");

        await Assert.That(host.GetTagItem("spinneret.queue.outcome")).IsNull();
        await Assert.That(host.Status).IsEqualTo(ActivityStatusCode.Unset);
    }
}
