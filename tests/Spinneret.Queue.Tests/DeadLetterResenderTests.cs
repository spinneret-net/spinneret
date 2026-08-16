using System.Text.Json;
using Spinneret.Functional;

namespace Spinneret.Queue.Tests;

public class DeadLetterResenderTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 15, 10, 30, 0, TimeSpan.Zero);

    private static DeadLetter DeadLetter(
        string key = "task-1",
        string? commandTypeName = null,
        string payloadJson = "{}") =>
        new()
        {
            IdempotencyKey = key,
            Source = DeadLetterSource.Queue,
            CommandTypeName = commandTypeName ?? typeof(UnannotatedCommand).FullName!,
            PayloadJson = payloadJson,
            Error = "boom",
            Attempts = 3,
            DeadLetteredAt = At,
        };

    private static (DeadLetterResender Resender, FakeQueue Queue, FakeDeadLetterStore Store,
        FakeSerializer Serializer, RecordingQueueTransactionScope Scope) Build(
        params DeadLetter[] stored)
    {
        var store = new FakeDeadLetterStore();
        foreach (var deadLetter in stored)
            store.Add(deadLetter);

        var queue = new FakeQueue();
        var serializer = new FakeSerializer();
        var scope = new RecordingQueueTransactionScope();
        var registry = new QueueTypeRegistry([typeof(DeadLetterResenderTests).Assembly]);

        return (new DeadLetterResender(store, queue, registry, serializer, scope), queue, store, serializer, scope);
    }

    private static TError ExpectError<TError>(Result<TError> result) =>
        result.Match(
            () => throw new InvalidOperationException("Expected an error but the resend succeeded."),
            error => error);

    // -----------------------------------------------------------------------------------------
    // The happy path.
    // -----------------------------------------------------------------------------------------

    [Test]
    public async Task Enqueues_the_stored_command_and_removes_the_entry()
    {
        var (resender, queue, store, _, _) = Build(DeadLetter());

        var result = await resender.ResendAsync("task-1");

        await Assert.That(result.Match(() => true, _ => false)).IsTrue();
        await Assert.That(Expect.Single(queue.Enqueued)).IsTypeOf<UnannotatedCommand>();
        await Assert.That(await store.GetAsync("task-1")).IsNull();
    }

    [Test]
    public async Task Enqueues_before_deleting()
    {
        // Losing the entry ahead of a failed enqueue would take the payload with it; the other order
        // leaves an entry to resend again. Asserted through a queue that refuses to enqueue.
        var (resender, queue, store, _, _) = Build(DeadLetter());
        queue.Throw = new InvalidOperationException("transport down");

        await Assert.That(async () => await resender.ResendAsync("task-1"))
            .Throws<InvalidOperationException>();

        await Assert.That(store.Deleted).IsEmpty();
        await Assert.That(await store.GetAsync("task-1")).IsNotNull();
    }

    [Test]
    public async Task Groups_the_enqueue_and_the_delete_into_one_scope()
    {
        // The MSSQL scope turns this grouping into a single commit; here it only has to be the case
        // that both operations happen inside it rather than around it.
        var (resender, queue, store, _, scope) = Build(DeadLetter());
        var insideAtEnqueue = false;
        var insideAtDelete = false;

        var observing = new DeadLetterResender(
            new ObservingStore(store, () => insideAtDelete = scope.IsInside),
            new ObservingQueue(queue, () => insideAtEnqueue = scope.IsInside),
            new QueueTypeRegistry([typeof(DeadLetterResenderTests).Assembly]),
            new FakeSerializer(),
            scope);

        await observing.ResendAsync("task-1");

        await Assert.That(scope.Executions).IsEqualTo(1);
        await Assert.That(insideAtEnqueue).IsTrue();
        await Assert.That(insideAtDelete).IsTrue();
    }

    [Test]
    public async Task Resends_a_replacement_payload_instead_of_the_stored_one()
    {
        var (resender, _, _, serializer, _) = Build(DeadLetter(payloadJson: """{"broken":true}"""));
        string? deserialized = null;
        serializer.OnDeserialize = (json, type) =>
        {
            deserialized = json;
            return Activator.CreateInstance(type);
        };

        await resender.ResendAsync("task-1", """{"fixed":true}""");

        await Assert.That(deserialized).IsEqualTo("""{"fixed":true}""");
    }

    [Test]
    public async Task Uses_the_stored_payload_when_no_replacement_is_given()
    {
        var (resender, _, _, serializer, _) = Build(DeadLetter(payloadJson: """{"id":1}"""));
        string? deserialized = null;
        serializer.OnDeserialize = (json, type) =>
        {
            deserialized = json;
            return Activator.CreateInstance(type);
        };

        await resender.ResendAsync("task-1");

        await Assert.That(deserialized).IsEqualTo("""{"id":1}""");
    }

    [Test]
    public async Task Takes_the_command_type_from_the_entry_rather_than_the_replacement_payload()
    {
        // The payload is operator input; the type it deserializes into must never be.
        var (resender, queue, _, _, _) = Build(
            DeadLetter(commandTypeName: typeof(SecondChannelCommand).FullName!));

        await resender.ResendAsync("task-1", $$"""{"$type":"{{typeof(UnannotatedCommand).FullName}}"}""");

        await Assert.That(Expect.Single(queue.Enqueued)).IsTypeOf<SecondChannelCommand>();
    }

    // -----------------------------------------------------------------------------------------
    // The three refusals.
    // -----------------------------------------------------------------------------------------

    [Test]
    public async Task Reports_a_key_that_is_no_longer_stored()
    {
        var (resender, queue, _, _, _) = Build();

        var error = ExpectError(await resender.ResendAsync("gone"));

        await Assert.That(error).IsTypeOf<ResendDeadLetterError.NotFound>();
        await Assert.That(((ResendDeadLetterError.NotFound)error).IdempotencyKey).IsEqualTo("gone");
        await Assert.That(queue.Enqueued).IsEmpty();
    }

    [Test]
    public async Task Reports_a_command_type_the_queue_no_longer_knows_and_keeps_the_entry()
    {
        var (resender, queue, store, _, _) = Build(DeadLetter(commandTypeName: "Acme.RenamedCommand"));

        var error = ExpectError(await resender.ResendAsync("task-1"));

        await Assert.That(error).IsTypeOf<ResendDeadLetterError.UnknownCommandType>();
        await Assert.That(((ResendDeadLetterError.UnknownCommandType)error).CommandTypeName)
            .IsEqualTo("Acme.RenamedCommand");
        await Assert.That(queue.Enqueued).IsEmpty();
        // The payload is still readable, so discarding it here would destroy recoverable work.
        await Assert.That(await store.GetAsync("task-1")).IsNotNull();
    }

    [Test]
    public async Task Reports_a_payload_that_will_not_deserialize_and_keeps_the_entry()
    {
        var (resender, queue, store, serializer, _) = Build(DeadLetter());
        serializer.OnDeserialize = (_, _) => throw new JsonException("unexpected token");

        var error = ExpectError(await resender.ResendAsync("task-1", "not json"));

        await Assert.That(error).IsTypeOf<ResendDeadLetterError.InvalidPayload>();
        await Assert.That(((ResendDeadLetterError.InvalidPayload)error).Message).Contains("unexpected token");
        await Assert.That(queue.Enqueued).IsEmpty();
        await Assert.That(await store.GetAsync("task-1")).IsNotNull();
    }

    [Test]
    public async Task Reports_a_payload_whose_shape_the_serializer_cannot_map()
    {
        var (resender, queue, _, serializer, _) = Build(DeadLetter());
        serializer.OnDeserialize = (_, _) => throw new NotSupportedException("no converter");

        var error = ExpectError(await resender.ResendAsync("task-1"));

        await Assert.That(error).IsTypeOf<ResendDeadLetterError.InvalidPayload>();
        await Assert.That(queue.Enqueued).IsEmpty();
    }

    [Test]
    public async Task Reports_a_payload_that_deserializes_to_null()
    {
        var (resender, queue, _, serializer, _) = Build(DeadLetter());
        serializer.OnDeserialize = (_, _) => null;

        var error = ExpectError(await resender.ResendAsync("task-1", "null"));

        await Assert.That(error).IsTypeOf<ResendDeadLetterError.InvalidPayload>();
        await Assert.That(queue.Enqueued).IsEmpty();
    }

    [Test]
    public async Task Lets_an_unexpected_serializer_failure_through()
    {
        // Only the two malformed-payload shapes are turned into a result. Anything else is a bug in
        // the host's serializer, and swallowing it would report it back as the operator's fault.
        var (resender, _, _, serializer, _) = Build(DeadLetter());
        serializer.OnDeserialize = (_, _) => throw new InvalidOperationException("serializer misconfigured");

        await Assert.That(async () => await resender.ResendAsync("task-1"))
            .Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Rejects_a_blank_key(string key)
    {
        var (resender, _, _, _, _) = Build();

        await Assert.That(async () => await resender.ResendAsync(key)).Throws<ArgumentException>();
    }

    // -----------------------------------------------------------------------------------------
    // Decorators used to observe when, relative to the scope, each operation runs.
    // -----------------------------------------------------------------------------------------

    private sealed class ObservingQueue(IQueue inner, Action onEnqueue) : IQueue
    {
        public Task Enqueue<TResponse>(
            Mediator.IRequest<TResponse> request, QueueOptions? options = null, CancellationToken ct = default)
        {
            onEnqueue();
            return inner.Enqueue(request, options, ct);
        }
    }

    private sealed class ObservingStore(IDeadLetterStore inner, Action onDelete) : IDeadLetterStore
    {
        public Task<DeadLetterPage> ListAsync(DeadLetterQuery query, CancellationToken ct = default) =>
            inner.ListAsync(query, ct);

        public Task<DeadLetter?> GetAsync(string idempotencyKey, CancellationToken ct = default) =>
            inner.GetAsync(idempotencyKey, ct);

        public Task<bool> DeleteAsync(string idempotencyKey, CancellationToken ct = default)
        {
            onDelete();
            return inner.DeleteAsync(idempotencyKey, ct);
        }
    }
}
