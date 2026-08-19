using Google.Cloud.Firestore;

namespace Spinneret.Queue.Firestore.Tests;

/// <summary>
/// The document shape is a data contract: readers bind to these field names, so a rename here
/// silently breaks every existing dead-letter page. These tests pin the names and the value forms.
/// Talking to Firestore itself needs a live server and is intentionally out of scope.
/// </summary>
public class DeadLetterDocumentTests
{
    internal static readonly DateTimeOffset At = new(2026, 8, 15, 10, 30, 0, TimeSpan.Zero);

    internal static DeadLetterEntry Entry(
        DeadLetterSource source = DeadLetterSource.Queue,
        string? description = null,
        string? traceId = "4bf92f3577b34da6a3ce929d0e0e4736") =>
        new()
        {
            IdempotencyKey = "task-1",
            Source = source,
            CommandTypeName = "Acme.SyncCommand",
            Description = description,
            PayloadJson = """{"id":1}""",
            Error = "boom",
            Attempts = 3,
            TraceId = traceId,
        };

    [Test]
    public async Task Writes_every_contract_field()
    {
        var fields = DeadLetterDocument.From(Entry(), At);

        await Assert.That(fields.Keys).IsEquivalentTo(new[]
        {
            "source", "commandTypeName", "description", "payloadJson", "error", "attempts", "deadLetteredAt",
            "traceId",
        });
    }

    [Test]
    public async Task Maps_entry_values_onto_their_fields()
    {
        var fields = DeadLetterDocument.From(Entry(description: "nightly sync"), At);

        await Assert.That(fields["commandTypeName"]).IsEqualTo("Acme.SyncCommand");
        await Assert.That(fields["description"]).IsEqualTo("nightly sync");
        await Assert.That(fields["payloadJson"]).IsEqualTo("""{"id":1}""");
        await Assert.That(fields["error"]).IsEqualTo("boom");
        await Assert.That(fields["attempts"]).IsEqualTo(3);
        await Assert.That(fields["traceId"]).IsEqualTo("4bf92f3577b34da6a3ce929d0e0e4736");
    }

    [Test]
    public async Task Reads_back_the_trace_id_it_wrote()
    {
        var fields = DeadLetterDocument.From(Entry(), At);

        var deadLetter = DeadLetterDocument.ToDeadLetter("task-1", fields!);

        await Assert.That(deadLetter.TraceId).IsEqualTo("4bf92f3577b34da6a3ce929d0e0e4736");
    }

    [Test]
    public async Task Reads_a_document_written_before_the_trace_id_existed()
    {
        // Optional on read, unlike every required field: entries already in the store predate the
        // column, and an old dead letter must stay readable rather than throw on the admin page.
        var fields = DeadLetterDocument.From(Entry(), At);
        fields.Remove("traceId");

        var deadLetter = DeadLetterDocument.ToDeadLetter("task-1", fields!);

        await Assert.That(deadLetter.TraceId).IsNull();
    }

    [Test]
    [Arguments(DeadLetterSource.Queue, "Queue")]
    [Arguments(DeadLetterSource.Scheduler, "Scheduler")]
    public async Task Persists_the_source_as_its_member_name(DeadLetterSource source, string expected)
    {
        // Matches the MSSQL writer's column value, so one reader serves either store.
        var fields = DeadLetterDocument.From(Entry(source), At);

        await Assert.That(fields["source"]).IsEqualTo(expected);
    }

    [Test]
    public async Task Writes_the_supplied_time_rather_than_the_ambient_clock()
    {
        var fields = DeadLetterDocument.From(Entry(), At);

        await Assert.That(fields["deadLetteredAt"]).IsEqualTo(Timestamp.FromDateTimeOffset(At));
    }

    [Test]
    public async Task Keeps_a_null_description_as_an_explicit_null()
    {
        // Present-but-null rather than absent, so a reader binding the field sees a consistent
        // shape across entries instead of a missing-field error on some of them.
        var fields = DeadLetterDocument.From(Entry(description: null), At);

        await Assert.That(fields.ContainsKey("description")).IsTrue();
        await Assert.That(fields["description"]).IsNull();
    }
}
