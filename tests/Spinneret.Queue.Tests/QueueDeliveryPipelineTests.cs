using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace Spinneret.Queue.Tests;

public class QueueDeliveryPipelineTests
{
    private readonly ScriptedProcessor _processor = new();
    private readonly FakeDeadLetterWriter _deadLetters = new();
    private readonly RecordingUnit _unit = new();

    private QueueDeliveryPipeline CreatePipeline() =>
        new(_processor, _deadLetters, NullLogger<QueueDeliveryPipeline>.Instance);

    private static string EnvelopeJson() => JsonSerializer.Serialize(new QueueEnvelope
    {
        RequestTypeName = typeof(UnannotatedCommand).FullName!,
        PayloadJson = "{}",
        EnqueuedAtUtc = DateTimeOffset.UnixEpoch,
    });

    [Test]
    public async Task Acknowledged_delivery_commits_the_unit_of_work()
    {
        var outcome = await CreatePipeline().DeliverAsync(_unit, EnvelopeJson(), "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(_unit.Commits).IsEqualTo(1);
        await Assert.That(_processor.Context!.TaskId).IsEqualTo("task-1");
        await Assert.That(_processor.Context.Envelope.RequestTypeName).IsEqualTo(typeof(UnannotatedCommand).FullName!);
    }

    [Test]
    public async Task Unacknowledged_delivery_is_returned_without_committing()
    {
        // The transport redelivers the message as it was, so nothing of the delivery may become durable.
        _processor.Outcome = QueueDeliveryOutcome.RetryIn(TimeSpan.FromSeconds(30));

        var outcome = await CreatePipeline().DeliverAsync(_unit, EnvelopeJson(), "task-1", CancellationToken.None);

        await Assert.That(outcome.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(_unit.Commits).IsEqualTo(0);
    }

    [Test]
    public async Task Failed_commit_becomes_a_redelivery()
    {
        _unit.Throw = new InvalidOperationException("connection lost");

        var outcome = await CreatePipeline().DeliverAsync(_unit, EnvelopeJson(), "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsFalse();
        await Assert.That(outcome.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Unreadable_envelope_is_dead_lettered_raw_and_committed()
    {
        var outcome = await CreatePipeline().DeliverAsync(_unit, "{not json", "task-9", CancellationToken.None);

        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(_processor.Context).IsNull();
        await Assert.That(_unit.Commits).IsEqualTo(1);

        var entry = Expect.Single(_deadLetters.Entries);
        await Assert.That(entry.IdempotencyKey).IsEqualTo("task-9");
        await Assert.That(entry.CommandTypeName).IsEqualTo("<unreadable envelope>");
        await Assert.That(entry.PayloadJson).IsEqualTo("{not json");
        await Assert.That(entry.Source).IsEqualTo(DeadLetterSource.Queue);
        await Assert.That(entry.Attempts).IsEqualTo(1);
    }

    [Test]
    public async Task Null_envelope_is_dead_lettered_with_an_explicit_error()
    {
        await CreatePipeline().DeliverAsync(_unit, "null", "task-9", CancellationToken.None);

        await Assert.That(Expect.Single(_deadLetters.Entries).Error).IsEqualTo("Envelope deserialized to null.");
    }

    [Test]
    public async Task Unreadable_envelope_whose_dead_letter_cannot_be_written_is_redelivered_later()
    {
        _deadLetters.Throw = new InvalidOperationException("store down");

        var outcome = await CreatePipeline().DeliverAsync(_unit, "{not json", "task-9", CancellationToken.None);

        await Assert.That(outcome.RetryAfter).IsEqualTo(TimeSpan.FromMinutes(1));
        await Assert.That(_unit.Commits).IsEqualTo(0);
    }

    private sealed class ScriptedProcessor : IQueueDeliveryProcessor
    {
        public QueueDeliveryOutcome Outcome { get; set; } = QueueDeliveryOutcome.Acked;
        public QueueDeliveryContext? Context { get; private set; }

        public Task<QueueDeliveryOutcome> ProcessAsync(QueueDeliveryContext context, CancellationToken ct)
        {
            Context = context;
            return Task.FromResult(Outcome);
        }
    }

    private sealed class RecordingUnit : IQueueUnitOfWork
    {
        public Exception? Throw { get; set; }
        public int Commits { get; private set; }

        public Task CommitAsync(CancellationToken ct)
        {
            if (Throw is not null)
                return Task.FromException(Throw);

            Commits++;
            return Task.CompletedTask;
        }
    }
}
