using Google.Cloud.Firestore;
using Grpc.Core;

namespace Spinneret.Queue.Firestore.Tests;

// The emulator doesn't enforce composite indexes, so these pin document shape and semantics only.
[ClassDataSource<FirestoreEmulatorFixture>(Shared = SharedType.PerTestSession)]
public sealed class FirestoreDeadLetterWriterTests(FirestoreEmulatorFixture fixture)
{
    private static DeadLetterEntry Entry(
        string key = "task-1",
        string? description = "nightly sync",
        DeadLetterSource source = DeadLetterSource.Queue) =>
        new()
        {
            IdempotencyKey = key,
            Source = source,
            CommandTypeName = typeof(PingCommand).FullName!,
            Description = description,
            PayloadJson = """{"name":"ada"}""",
            Error = "boom",
            Attempts = 3,
        };

    [Test]
    public async Task Stores_every_field_with_the_type_the_contract_promises()
    {
        await using var host = DeadLetterTestHost.Start(fixture);

        await host.Writer.WriteAsync(Entry());

        var fields = await host.RawFields("task-1");
        await Assert.That(fields).IsNotNull();
        await Assert.That(fields![DeadLetterDocument.Fields.Source]).IsEqualTo("Queue");
        await Assert.That(fields[DeadLetterDocument.Fields.CommandTypeName])
            .IsEqualTo(typeof(PingCommand).FullName!);
        await Assert.That(fields[DeadLetterDocument.Fields.Description]).IsEqualTo("nightly sync");
        await Assert.That(fields[DeadLetterDocument.Fields.PayloadJson]).IsEqualTo("""{"name":"ada"}""");
        await Assert.That(fields[DeadLetterDocument.Fields.Error]).IsEqualTo("boom");
        await Assert.That(fields[DeadLetterDocument.Fields.Attempts]).IsEqualTo(3L);
        await Assert.That(fields[DeadLetterDocument.Fields.DeadLetteredAt])
            .IsEqualTo(Timestamp.FromDateTimeOffset(host.Clock.Now));
    }

    [Test]
    public async Task Files_the_document_under_the_idempotency_key_without_duplicating_it_into_a_field()
    {
        await using var host = DeadLetterTestHost.Start(fixture);

        await host.Writer.WriteAsync(Entry("some-task-id"));

        var fields = await host.RawFields("some-task-id");
        await Assert.That(fields).IsNotNull();
        await Assert.That(fields!.Keys).DoesNotContain("idempotencyKey");
    }

    [Test]
    public async Task Stores_a_null_description_as_null_rather_than_omitting_the_field()
    {
        await using var host = DeadLetterTestHost.Start(fixture);

        await host.Writer.WriteAsync(Entry(description: null));

        var fields = await host.RawFields("task-1");
        await Assert.That(fields!.ContainsKey(DeadLetterDocument.Fields.Description)).IsTrue();
        await Assert.That(fields[DeadLetterDocument.Fields.Description]).IsNull();
    }

    [Test]
    public async Task A_redelivered_write_keeps_the_instant_the_failure_actually_happened()
    {
        // Create, not Set: a Set would move deadLetteredAt forward on every redelivery.
        await using var host = DeadLetterTestHost.Start(fixture);
        var first = host.Clock.Now;

        await host.Writer.WriteAsync(Entry());
        host.Clock.Now = first.AddHours(2);
        await host.Writer.WriteAsync(Entry() with { Error = "a later, different complaint" });

        var stored = await host.Store.GetAsync("task-1");
        await Assert.That(stored!.DeadLetteredAt).IsEqualTo(first);
        await Assert.That(stored.Error).IsEqualTo("boom");
        await Assert.That(await host.DocumentCount()).IsEqualTo(1);
    }

    [Test]
    public async Task Two_writers_racing_on_one_key_yield_a_single_document()
    {
        // Genuine concurrency, so the loser hits the real AlreadyExists branch.
        await using var host = DeadLetterTestHost.Start(fixture);

        await Task.WhenAll(host.Writer.WriteAsync(Entry()), host.Writer.WriteAsync(Entry()));

        await Assert.That(await host.DocumentCount()).IsEqualTo(1);
    }

    [Test]
    public async Task Heavier_contention_still_never_produces_a_second_document()
    {
        // The emulator aborts waiting writers with lock timeouts (retried by the processor in production),
        // so only the invariant holds: a writer may fail, but never writes a second document.
        await using var host = DeadLetterTestHost.Start(fixture);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            try
            {
                await host.Writer.WriteAsync(Entry());
                return true;
            }
            catch (RpcException)
            {
                return false;
            }
        }));

        await Assert.That(await host.DocumentCount()).IsEqualTo(1);
        await Assert.That(outcomes).Contains(true);
    }

    [Test]
    [Arguments(DeadLetterSource.Queue)]
    [Arguments(DeadLetterSource.Scheduler)]
    public async Task Round_trips_every_source_through_Firestore(DeadLetterSource source)
    {
        await using var host = DeadLetterTestHost.Start(fixture);

        await host.Writer.WriteAsync(Entry(source: source));

        await Assert.That((await host.Store.GetAsync("task-1"))!.Source).IsEqualTo(source);
    }
}
