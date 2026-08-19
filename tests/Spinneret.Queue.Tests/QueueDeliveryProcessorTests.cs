using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.Queue.Tests;

public class QueueDeliveryProcessorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeDispatcher _dispatcher = new();
    private readonly FakeEnvelopeQueue _envelopeQueue = new();
    private readonly FakeDeadLetterWriter _deadLetters = new();

    /// <summary>
    /// QueueDeliveryProcessor is internal, so the processor is obtained the way a host gets it:
    /// through AddQueueCore and the IQueueDeliveryProcessor registration.
    /// </summary>
    private IQueueDeliveryProcessor CreateProcessor()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IQueueDispatcher>(_dispatcher);
        services.AddSingleton<IEnvelopeQueue>(_envelopeQueue);
        services.AddSingleton<IDeadLetterWriter>(_deadLetters);
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        services.AddNullLogging();
        services.AddQueueCore(new QueueTypeRegistry([typeof(QueueDeliveryProcessorTests).Assembly]));

        return services.BuildServiceProvider().GetRequiredService<IQueueDeliveryProcessor>();
    }

    private static QueueEnvelope Envelope<TCommand>(
        int priorFailures = 0, TimeSpan? age = null, string? description = null, string? traceParent = null) => new()
    {
        RequestTypeName = typeof(TCommand).FullName!,
        PayloadJson = """{"id":42}""",
        EnqueuedAtUtc = Now - (age ?? TimeSpan.Zero),
        PriorFailures = priorFailures,
        Description = description,
        TraceParent = traceParent,
    };

    // -----------------------------------------------------------------------------------------
    // Happy path
    // -----------------------------------------------------------------------------------------

    [Test]
    public async Task ProcessAsync_successful_dispatch_acks_without_side_effects()
    {
        var outcome = await CreateProcessor().ProcessAsync(
            Envelope<UnannotatedCommand>(age: TimeSpan.FromMinutes(5)), "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(outcome.RetryAfter).IsNull();
        await Assert.That(_dispatcher.Calls).IsEqualTo(1);
        await Assert.That(_deadLetters.Entries).IsEmpty();
        await Assert.That(_envelopeQueue.Enqueued).IsEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // Transient failures: booked retries with doubling backoff
    // -----------------------------------------------------------------------------------------

    [Test]
    public async Task ProcessAsync_failure_before_max_attempts_re_enqueues_with_doubling_backoff_and_books_the_failure()
    {
        _dispatcher.Throw = new InvalidOperationException("boom");

        var first = await CreateProcessor().ProcessAsync(
            Envelope<UnannotatedCommand>(), "task-1", CancellationToken.None);
        var second = await CreateProcessor().ProcessAsync(
            Envelope<UnannotatedCommand>(priorFailures: 1), "task-2", CancellationToken.None);

        await Assert.That(first.Ack).IsTrue();
        await Assert.That(second.Ack).IsTrue();
        await Assert.That(_deadLetters.Entries).IsEmpty();
        await Assert.That(_envelopeQueue.Enqueued.Count).IsEqualTo(2);
        await Assert.That(_envelopeQueue.Enqueued[0].Delay).IsEqualTo(TimeSpan.FromSeconds(10));
        await Assert.That(_envelopeQueue.Enqueued[0].Envelope.PriorFailures).IsEqualTo(1);
        await Assert.That(_envelopeQueue.Enqueued[1].Delay).IsEqualTo(TimeSpan.FromSeconds(20));
        await Assert.That(_envelopeQueue.Enqueued[1].Envelope.PriorFailures).IsEqualTo(2);
    }

    [Test]
    public async Task ProcessAsync_failure_re_enqueue_preserves_enqueue_time_trace_id_and_payload()
    {
        _dispatcher.Throw = new InvalidOperationException("boom");
        var envelope = Envelope<UnannotatedCommand>(age: TimeSpan.FromMinutes(3), traceParent: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");

        await CreateProcessor().ProcessAsync(envelope, "task-1", CancellationToken.None);

        var reEnqueued = Expect.Single(_envelopeQueue.Enqueued).Envelope;
        await Assert.That(reEnqueued.EnqueuedAtUtc).IsEqualTo(envelope.EnqueuedAtUtc);
        await Assert.That(reEnqueued.TraceParent).IsEqualTo("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
        await Assert.That(reEnqueued.PayloadJson).IsEqualTo(envelope.PayloadJson);
    }

    [Test]
    public async Task ProcessAsync_failure_uses_the_commands_declared_min_backoff()
    {
        _dispatcher.Throw = new InvalidOperationException("boom");

        // AnnotatedCommand declares MinBackoff 5s and MaxAttempts 2: attempt 1 of 2 retries in 5s.
        var outcome = await CreateProcessor().ProcessAsync(
            Envelope<AnnotatedCommand>(), "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(Expect.Single(_envelopeQueue.Enqueued).Delay).IsEqualTo(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ProcessAsync_failure_re_enqueue_failing_falls_back_to_transport_retry_with_backoff()
    {
        _dispatcher.Throw = new InvalidOperationException("boom");
        _envelopeQueue.Throw = new InvalidOperationException("cloud tasks down");

        var outcome = await CreateProcessor().ProcessAsync(
            Envelope<UnannotatedCommand>(), "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsFalse();
        await Assert.That(outcome.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(10));
        await Assert.That(_deadLetters.Entries).IsEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // Exhaustion: attempts and age budgets
    // -----------------------------------------------------------------------------------------

    [Test]
    public async Task ProcessAsync_failure_at_max_attempts_dead_letters()
    {
        _dispatcher.Throw = new InvalidOperationException("boom");

        // Default policy: 7 attempts. 6 booked failures make this execution attempt 7, the final one.
        var outcome = await CreateProcessor().ProcessAsync(
            Envelope<UnannotatedCommand>(priorFailures: 6), "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(_envelopeQueue.Enqueued).IsEmpty();
        var entry = Expect.Single(_deadLetters.Entries);
        await Assert.That(entry.Attempts).IsEqualTo(7);
        await Assert.That(entry.CommandTypeName).IsEqualTo(typeof(UnannotatedCommand).FullName);
    }

    [Test]
    public async Task ProcessAsync_failure_past_max_age_dead_letters_regardless_of_attempt()
    {
        _dispatcher.Throw = new InvalidOperationException("boom");

        var outcome = await CreateProcessor().ProcessAsync(
            Envelope<UnannotatedCommand>(age: TimeSpan.FromHours(25)), "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(_envelopeQueue.Enqueued).IsEmpty();
        await Assert.That(Expect.Single(_deadLetters.Entries).Attempts).IsEqualTo(1);
    }

    [Test]
    public async Task ProcessAsync_failure_at_exactly_max_age_still_retries()
    {
        _dispatcher.Throw = new InvalidOperationException("boom");

        // The age bound is exclusive: a task exactly MaxAge old has not yet exceeded it.
        var outcome = await CreateProcessor().ProcessAsync(
            Envelope<UnannotatedCommand>(age: TimeSpan.FromDays(1)), "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(_deadLetters.Entries).IsEmpty();
        await Assert.That(_envelopeQueue.Enqueued.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ProcessAsync_exhausted_attempts_discards_when_policy_says_discard()
    {
        _dispatcher.Throw = new InvalidOperationException("boom");

        // DiscardOnExhaustion policy: 2 attempts. One booked failure makes this attempt 2, the final one.
        var outcome = await CreateProcessor().ProcessAsync(
            Envelope<DiscardOnExhaustionCommand>(priorFailures: 1), "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(_envelopeQueue.Enqueued).IsEmpty();
        await Assert.That(_deadLetters.Entries).IsEmpty();
    }

    [Test]
    public async Task ProcessAsync_exhausted_age_discards_when_policy_says_discard()
    {
        _dispatcher.Throw = new InvalidOperationException("boom");

        var outcome = await CreateProcessor().ProcessAsync(
            Envelope<DiscardOnExhaustionCommand>(age: TimeSpan.FromHours(2)), "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(_deadLetters.Entries).IsEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // Permanent failures
    // -----------------------------------------------------------------------------------------

    [Test]
    public async Task ProcessAsync_permanent_failure_dead_letters_immediately()
    {
        _dispatcher.Throw = new QueueHandlerPermanentException("gone forever");

        var outcome = await CreateProcessor().ProcessAsync(
            Envelope<UnannotatedCommand>(), "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(_envelopeQueue.Enqueued).IsEmpty();
        await Assert.That(Expect.Single(_deadLetters.Entries).Error).IsEqualTo("gone forever");
    }

    [Test]
    public async Task ProcessAsync_permanent_failure_dead_letters_even_when_policy_discards_exhaustion()
    {
        _dispatcher.Throw = new QueueHandlerPermanentException("defect");

        var outcome = await CreateProcessor().ProcessAsync(
            Envelope<DiscardOnExhaustionCommand>(), "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(Expect.Single(_deadLetters.Entries).Error).IsEqualTo("defect");
    }

    // -----------------------------------------------------------------------------------------
    // Error results: policy-driven handling
    // -----------------------------------------------------------------------------------------

    [Test]
    public async Task ProcessAsync_error_result_dead_letters_by_default()
    {
        _dispatcher.Throw = new QueueHandlerFailedException("SomeBusinessError");

        var outcome = await CreateProcessor().ProcessAsync(
            Envelope<UnannotatedCommand>(), "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(_envelopeQueue.Enqueued).IsEmpty();
        await Assert.That(Expect.Single(_deadLetters.Entries).Attempts).IsEqualTo(1);
    }

    [Test]
    public async Task ProcessAsync_error_result_retries_when_policy_says_retry()
    {
        _dispatcher.Throw = new QueueHandlerFailedException("SomeBusinessError");

        var outcome = await CreateProcessor().ProcessAsync(
            Envelope<RetryOnErrorResultCommand>(), "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(_deadLetters.Entries).IsEmpty();
        var (reEnqueued, delay) = Expect.Single(_envelopeQueue.Enqueued);
        await Assert.That(delay).IsEqualTo(TimeSpan.FromSeconds(10));
        await Assert.That(reEnqueued.PriorFailures).IsEqualTo(1);
    }

    [Test]
    public async Task ProcessAsync_error_result_under_retry_policy_dead_letters_once_attempts_are_exhausted()
    {
        _dispatcher.Throw = new QueueHandlerFailedException("SomeBusinessError");

        var outcome = await CreateProcessor().ProcessAsync(
            Envelope<RetryOnErrorResultCommand>(priorFailures: 6), "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(_envelopeQueue.Enqueued).IsEmpty();
        await Assert.That(Expect.Single(_deadLetters.Entries).Attempts).IsEqualTo(7);
    }

    [Test]
    public async Task ProcessAsync_error_result_acks_when_policy_says_discard()
    {
        _dispatcher.Throw = new QueueHandlerFailedException("SomeBusinessError");

        var outcome = await CreateProcessor().ProcessAsync(
            Envelope<AnnotatedCommand>(), "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(_deadLetters.Entries).IsEmpty();
        await Assert.That(_envelopeQueue.Enqueued).IsEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // Deferrals
    // -----------------------------------------------------------------------------------------

    [Test]
    public async Task ProcessAsync_deferral_re_enqueues_unchanged_preserving_failures_and_original_enqueue_time()
    {
        _dispatcher.Throw = new QueueHandlerRetryAfterException(TimeSpan.FromMinutes(30));

        var envelope = Envelope<UnannotatedCommand>(priorFailures: 2, age: TimeSpan.FromMinutes(10));
        var outcome = await CreateProcessor().ProcessAsync(envelope, "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(_deadLetters.Entries).IsEmpty();
        var (reEnqueued, delay) = Expect.Single(_envelopeQueue.Enqueued);
        await Assert.That(delay).IsEqualTo(TimeSpan.FromMinutes(30));
        // The deferring execution itself is not a failure.
        await Assert.That(reEnqueued.PriorFailures).IsEqualTo(2);
        await Assert.That(reEnqueued.EnqueuedAtUtc).IsEqualTo(envelope.EnqueuedAtUtc);
    }

    [Test]
    public async Task ProcessAsync_deferral_beyond_max_age_dead_letters()
    {
        _dispatcher.Throw = new QueueHandlerRetryAfterException(TimeSpan.FromHours(1));

        // Annotated policy: MaxAge 1h. Age 30m plus a 1h deferral would exceed it.
        var outcome = await CreateProcessor().ProcessAsync(
            Envelope<AnnotatedCommand>(age: TimeSpan.FromMinutes(30)), "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(_envelopeQueue.Enqueued).IsEmpty();
        await Assert.That(Expect.Single(_deadLetters.Entries).CommandTypeName)
            .IsEqualTo(typeof(AnnotatedCommand).FullName);
    }

    [Test]
    public async Task ProcessAsync_deferral_landing_exactly_on_max_age_still_re_enqueues()
    {
        _dispatcher.Throw = new QueueHandlerRetryAfterException(TimeSpan.FromMinutes(30));

        // Age 30m + 30m deferral == MaxAge 1h exactly; the bound is exclusive.
        var outcome = await CreateProcessor().ProcessAsync(
            Envelope<AnnotatedCommand>(age: TimeSpan.FromMinutes(30)), "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(_deadLetters.Entries).IsEmpty();
        await Assert.That(Expect.Single(_envelopeQueue.Enqueued).Delay).IsEqualTo(TimeSpan.FromMinutes(30));
    }

    [Test]
    public async Task ProcessAsync_deferral_beyond_max_age_discards_when_policy_says_discard()
    {
        _dispatcher.Throw = new QueueHandlerRetryAfterException(TimeSpan.FromMinutes(45));

        // DiscardOnExhaustion policy: MaxAge 1h. Age 30m + 45m deferral exceeds it.
        var outcome = await CreateProcessor().ProcessAsync(
            Envelope<DiscardOnExhaustionCommand>(age: TimeSpan.FromMinutes(30)), "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(_envelopeQueue.Enqueued).IsEmpty();
        await Assert.That(_deadLetters.Entries).IsEmpty();
    }

    [Test]
    public async Task ProcessAsync_deferral_re_enqueue_failing_falls_back_to_transport_retry_after_requested_delay()
    {
        _dispatcher.Throw = new QueueHandlerRetryAfterException(TimeSpan.FromMinutes(5));
        _envelopeQueue.Throw = new InvalidOperationException("cloud tasks down");

        var outcome = await CreateProcessor().ProcessAsync(
            Envelope<UnannotatedCommand>(), "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsFalse();
        await Assert.That(outcome.RetryAfter).IsEqualTo(TimeSpan.FromMinutes(5));
        await Assert.That(_deadLetters.Entries).IsEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // Unknown types and dead-letter plumbing
    // -----------------------------------------------------------------------------------------

    [Test]
    public async Task ProcessAsync_unknown_request_type_dead_letters_without_dispatching()
    {
        var envelope = Envelope<UnannotatedCommand>(priorFailures: 3) with { RequestTypeName = "No.Such.Type" };

        var outcome = await CreateProcessor().ProcessAsync(envelope, "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(_dispatcher.Calls).IsEqualTo(0);
        var entry = Expect.Single(_deadLetters.Entries);
        await Assert.That(entry.CommandTypeName).IsEqualTo("No.Such.Type");
        await Assert.That(entry.Attempts).IsEqualTo(4);
    }

    [Test]
    public async Task ProcessAsync_dead_letter_entry_carries_task_metadata()
    {
        _dispatcher.Throw = new QueueHandlerPermanentException("gone forever");
        var envelope = Envelope<UnannotatedCommand>(description: "SyncEmployee → Fortnox");

        await CreateProcessor().ProcessAsync(envelope, "task-42", CancellationToken.None);

        var entry = Expect.Single(_deadLetters.Entries);
        await Assert.That(entry.IdempotencyKey).IsEqualTo("task-42");
        await Assert.That(entry.Source).IsEqualTo(DeadLetterSource.Queue);
        await Assert.That(entry.CommandTypeName).IsEqualTo(typeof(UnannotatedCommand).FullName);
        await Assert.That(entry.Description).IsEqualTo("SyncEmployee → Fortnox");
        await Assert.That(entry.PayloadJson).IsEqualTo(envelope.PayloadJson);
        await Assert.That(entry.Error).IsEqualTo("gone forever");
    }

    [Test]
    public async Task ProcessAsync_dead_letter_write_failure_retries_instead_of_dropping_the_task()
    {
        _dispatcher.Throw = new QueueHandlerPermanentException("gone forever");
        _deadLetters.Throw = new InvalidOperationException("firestore down");

        var outcome = await CreateProcessor().ProcessAsync(
            Envelope<UnannotatedCommand>(), "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsFalse();
        await Assert.That(outcome.RetryAfter).IsEqualTo(TimeSpan.FromMinutes(1));
    }

    // -----------------------------------------------------------------------------------------
    // QueueDeliveryOutcome
    // -----------------------------------------------------------------------------------------

    [Test]
    public async Task QueueDeliveryOutcome_acked_has_no_retry_delay()
    {
        var outcome = QueueDeliveryOutcome.Acked;

        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(outcome.RetryAfter).IsNull();
    }

    [Test]
    public async Task QueueDeliveryOutcome_retry_in_carries_the_delay_and_is_not_an_ack()
    {
        var outcome = QueueDeliveryOutcome.RetryIn(TimeSpan.FromSeconds(30));

        await Assert.That(outcome.Ack).IsFalse();
        await Assert.That(outcome.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(30));
    }
}
