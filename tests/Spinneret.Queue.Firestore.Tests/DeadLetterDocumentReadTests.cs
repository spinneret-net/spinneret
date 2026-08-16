using Google.Cloud.Firestore;

namespace Spinneret.Queue.Firestore.Tests;

/// <summary>
/// The read direction of the same data contract. Expressed over a field map rather than a live
/// <c>DocumentSnapshot</c>, which is what lets the write and the read be pinned against each other
/// without a Firestore to talk to.
/// </summary>
public class DeadLetterDocumentReadTests
{
    private static readonly DateTimeOffset At = DeadLetterDocumentTests.At;

    /// <summary>
    /// What a document written by this library comes back as. Firestore returns every integer as a
    /// 64-bit value whatever width was written, so the round trip is not a straight identity and
    /// this models the widening rather than hiding it.
    /// </summary>
    private static Dictionary<string, object?> AsStored(DeadLetterEntry entry, DateTimeOffset at)
    {
        var fields = DeadLetterDocument.From(entry, at);
        fields[DeadLetterDocument.Fields.Attempts] = Convert.ToInt64(fields[DeadLetterDocument.Fields.Attempts]);
        return fields;
    }

    [Test]
    public async Task Reads_back_everything_the_writer_wrote()
    {
        var entry = DeadLetterDocumentTests.Entry(description: "nightly sync");

        var read = DeadLetterDocument.ToDeadLetter(entry.IdempotencyKey, AsStored(entry, At));

        await Assert.That(read.IdempotencyKey).IsEqualTo(entry.IdempotencyKey);
        await Assert.That(read.Source).IsEqualTo(entry.Source);
        await Assert.That(read.CommandTypeName).IsEqualTo(entry.CommandTypeName);
        await Assert.That(read.Description).IsEqualTo(entry.Description);
        await Assert.That(read.PayloadJson).IsEqualTo(entry.PayloadJson);
        await Assert.That(read.Error).IsEqualTo(entry.Error);
        await Assert.That(read.Attempts).IsEqualTo(entry.Attempts);
        await Assert.That(read.DeadLetteredAt).IsEqualTo(At);
    }

    [Test]
    [Arguments(DeadLetterSource.Queue)]
    [Arguments(DeadLetterSource.Scheduler)]
    public async Task Round_trips_every_source(DeadLetterSource source)
    {
        var entry = DeadLetterDocumentTests.Entry(source);

        var read = DeadLetterDocument.ToDeadLetter(entry.IdempotencyKey, AsStored(entry, At));

        await Assert.That(read.Source).IsEqualTo(source);
    }

    [Test]
    public async Task Reads_an_absent_description_as_null()
    {
        var fields = AsStored(DeadLetterDocumentTests.Entry(description: null), At);
        fields.Remove(DeadLetterDocument.Fields.Description);

        var read = DeadLetterDocument.ToDeadLetter("task-1", fields);

        await Assert.That(read.Description).IsNull();
    }

    [Test]
    public async Task Accepts_attempts_at_either_width()
    {
        // int is what From() writes; long is what Firestore hands back. Both must read.
        var fields = DeadLetterDocument.From(DeadLetterDocumentTests.Entry(), At);

        await Assert.That(DeadLetterDocument.ToDeadLetter("task-1", fields).Attempts).IsEqualTo(3);

        fields[DeadLetterDocument.Fields.Attempts] = 3L;
        await Assert.That(DeadLetterDocument.ToDeadLetter("task-1", fields).Attempts).IsEqualTo(3);
    }

    [Test]
    public async Task Takes_the_idempotency_key_from_the_document_id()
    {
        // The writer files the document under the key rather than duplicating it into a field.
        var read = DeadLetterDocument.ToDeadLetter("some-other-id", AsStored(DeadLetterDocumentTests.Entry(), At));

        await Assert.That(read.IdempotencyKey).IsEqualTo("some-other-id");
        await Assert.That(AsStored(DeadLetterDocumentTests.Entry(), At).Keys).DoesNotContain("idempotencyKey");
    }

    [Test]
    [Arguments(DeadLetterDocument.Fields.Source)]
    [Arguments(DeadLetterDocument.Fields.CommandTypeName)]
    [Arguments(DeadLetterDocument.Fields.PayloadJson)]
    [Arguments(DeadLetterDocument.Fields.Error)]
    [Arguments(DeadLetterDocument.Fields.Attempts)]
    [Arguments(DeadLetterDocument.Fields.DeadLetteredAt)]
    public async Task Refuses_a_document_missing_a_required_field(string field)
    {
        var fields = AsStored(DeadLetterDocumentTests.Entry(), At);
        fields.Remove(field);

        await Assert.That(() => DeadLetterDocument.ToDeadLetter("task-1", fields))
            .Throws<InvalidOperationException>()
            .WithMessageContaining(field);
    }

    [Test]
    public async Task Refuses_a_document_whose_field_holds_the_wrong_type()
    {
        var fields = AsStored(DeadLetterDocumentTests.Entry(), At);
        fields[DeadLetterDocument.Fields.DeadLetteredAt] = "not a timestamp";

        await Assert.That(() => DeadLetterDocument.ToDeadLetter("task-1", fields))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Refuses_a_source_this_library_never_wrote()
    {
        var fields = AsStored(DeadLetterDocumentTests.Entry(), At);
        fields[DeadLetterDocument.Fields.Source] = "queue";

        await Assert.That(() => DeadLetterDocument.ToDeadLetter("task-1", fields))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Reads_a_timestamp_as_the_instant_it_was_written()
    {
        var fields = AsStored(DeadLetterDocumentTests.Entry(), At);
        fields[DeadLetterDocument.Fields.DeadLetteredAt] = Timestamp.FromDateTimeOffset(At.AddMilliseconds(456));

        var read = DeadLetterDocument.ToDeadLetter("task-1", fields);

        await Assert.That(read.DeadLetteredAt).IsEqualTo(At.AddMilliseconds(456));
    }
}
