namespace Spinneret.Queue.Firestore.Tests;

/// <summary>
/// The admin side against a real Firestore (the emulator, via Testcontainers): the query and the
/// keyset cursor that page a dead-letter screen, and a resend over a transport that has no
/// transaction to offer.
/// </summary>
/// <remarks>
/// <para>
/// The ordering under test — <c>deadLetteredAt</c> descending then document id descending, resumed
/// with a two-value <c>StartAfter</c> — is what lets this page work off Firestore's automatic
/// single-field index. Nothing below proves that: the emulator does not enforce composite-index
/// requirements, so it serves queries production would reject with FAILED_PRECONDITION. What these
/// tests do prove is that the ordering, the tie-break and the cursor are correct.
/// </para>
/// <para>
/// Entries are seeded straight into Firestore rather than written through
/// <see cref="FirestoreDeadLetterWriter"/>, because the writer's own clock cannot produce the exact
/// instants and ties the paging cases need.
/// </para>
/// </remarks>
[ClassDataSource<FirestoreEmulatorFixture>(Shared = SharedType.PerTestSession)]
public sealed class FirestoreDeadLetterStoreTests(FirestoreEmulatorFixture fixture)
{
    private static readonly DateTimeOffset Base = new(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The refusal a resend came back with, or null if it went through.</summary>
    private static async Task<ResendDeadLetterError?> ResendError(
        DeadLetterTestHost host, string key, string? payloadJson = null) =>
        (await host.Resender.ResendAsync(key, payloadJson))
        .Match<ResendDeadLetterError?>(() => null, error => error);

    // ------------------------------------------------------------------------------ reads ---

    [Test]
    public async Task Reads_back_every_field_that_was_stored()
    {
        await using var host = DeadLetterTestHost.Start(fixture);
        await host.Seed("task-1", Base, source: DeadLetterSource.Scheduler);

        var entry = await host.Store.GetAsync("task-1");

        await Assert.That(entry).IsNotNull();
        await Assert.That(entry!.IdempotencyKey).IsEqualTo("task-1");
        await Assert.That(entry.Source).IsEqualTo(DeadLetterSource.Scheduler);
        await Assert.That(entry.CommandTypeName).IsEqualTo(typeof(PingCommand).FullName!);
        await Assert.That(entry.Description).IsEqualTo("seeded task-1");
        await Assert.That(entry.PayloadJson).IsEqualTo("""{"name":"seeded"}""");
        await Assert.That(entry.Error).IsEqualTo("boom");
        await Assert.That(entry.Attempts).IsEqualTo(3);
        await Assert.That(entry.DeadLetteredAt).IsEqualTo(Base);
    }

    [Test]
    public async Task Reads_back_what_the_writer_itself_wrote()
    {
        // The two directions of the document contract, pinned against each other through a real
        // Firestore rather than a hand-built field map.
        await using var host = DeadLetterTestHost.Start(fixture);
        await host.Writer.WriteAsync(new DeadLetterEntry
        {
            IdempotencyKey = "written-1",
            Source = DeadLetterSource.Queue,
            CommandTypeName = typeof(PingCommand).FullName!,
            Description = "nightly sync",
            PayloadJson = """{"name":"ada"}""",
            Error = "handler threw",
            Attempts = 5,
        });

        var entry = await host.Store.GetAsync("written-1");

        await Assert.That(entry!.Description).IsEqualTo("nightly sync");
        await Assert.That(entry.Attempts).IsEqualTo(5);
        await Assert.That(entry.Error).IsEqualTo("handler threw");
        await Assert.That(entry.DeadLetteredAt).IsEqualTo(host.Clock.Now);
    }

    [Test]
    public async Task Reads_a_null_description_back_as_null()
    {
        await using var host = DeadLetterTestHost.Start(fixture);
        await host.Seed("task-1", Base, withDescription: false);

        await Assert.That((await host.Store.GetAsync("task-1"))!.Description).IsNull();
    }

    [Test]
    public async Task Returns_null_for_a_key_that_was_never_stored()
    {
        await using var host = DeadLetterTestHost.Start(fixture);

        await Assert.That(await host.Store.GetAsync("never-stored")).IsNull();
    }

    [Test]
    public async Task Refuses_a_document_this_library_did_not_write()
    {
        // The map-level reader is unit-tested; this proves the same guard fires when the fields
        // arrive off a live DocumentSnapshot, which is a different code path into it.
        await using var host = DeadLetterTestHost.Start(fixture);
        await host.Document("foreign").CreateAsync(new Dictionary<string, object?>
        {
            ["somethingElse"] = "written by something that is not this library",
        });

        await Assert.That(() => host.Store.GetAsync("foreign")).Throws<InvalidOperationException>();
    }

    // ------------------------------------------------------------------------------ paging ---

    [Test]
    public async Task Lists_newest_first()
    {
        await using var host = DeadLetterTestHost.Start(fixture);
        await host.Seed("oldest", Base);
        await host.Seed("middle", Base.AddMinutes(5));
        await host.Seed("newest", Base.AddMinutes(10));

        var page = await host.Store.ListAsync(new DeadLetterQuery { PageSize = 10 });

        await Assert.That(page.Items.Select(i => i.IdempotencyKey))
            .IsEquivalentTo(new[] { "newest", "middle", "oldest" });
    }

    [Test]
    public async Task Reports_no_cursor_when_the_page_is_the_last_one()
    {
        // Exactly a full page with nothing behind it must still end the walk, which is what the
        // limit-plus-one probe is for.
        await using var host = DeadLetterTestHost.Start(fixture);
        await host.Seed("a", Base);
        await host.Seed("b", Base.AddMinutes(1));

        var page = await host.Store.ListAsync(new DeadLetterQuery { PageSize = 2 });

        await Assert.That(page.Items).HasCount(2);
        await Assert.That(page.NextCursor).IsNull();
    }

    [Test]
    public async Task Reports_an_empty_page_with_no_cursor_when_nothing_is_stored()
    {
        await using var host = DeadLetterTestHost.Start(fixture);

        var page = await host.Store.ListAsync(new DeadLetterQuery { PageSize = 10 });

        await Assert.That(page.Items).IsEmpty();
        await Assert.That(page.NextCursor).IsNull();
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(7)]
    public async Task Pages_through_everything_exactly_once(int pageSize)
    {
        await using var host = DeadLetterTestHost.Start(fixture);
        for (var i = 0; i < 7; i++)
            await host.Seed($"task-{i}", Base.AddMinutes(i));

        var keys = await host.PageThrough(pageSize);

        await Assert.That(keys).IsEquivalentTo(
            new[] { "task-6", "task-5", "task-4", "task-3", "task-2", "task-1", "task-0" });
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task Pages_through_entries_sharing_one_instant(int pageSize)
    {
        // The whole reason the query orders by document id as well: entries that tie on
        // deadLetteredAt still have a total order, so a cursor can resume inside the tie without
        // skipping or repeating one.
        await using var host = DeadLetterTestHost.Start(fixture);
        for (var i = 0; i < 5; i++)
            await host.Seed($"tied-{i}", Base);

        var keys = await host.PageThrough(pageSize);

        await Assert.That(keys).IsEquivalentTo(
            new[] { "tied-4", "tied-3", "tied-2", "tied-1", "tied-0" });
    }

    [Test]
    public async Task Pages_through_instants_finer_than_Firestore_stores()
    {
        // Firestore keeps microseconds; DeadLetterCursor encodes 100ns ticks. If the cursor were
        // built from anything but the stored value, StartAfter would land off the row it names and
        // silently skip or repeat entries at every page boundary. The MSSQL store had exactly this
        // bug against DATETIME2(3); this is the Firestore equivalent.
        await using var host = DeadLetterTestHost.Start(fixture);
        for (var i = 0; i < 6; i++)
            await host.Seed($"fine-{i}", Base.AddTicks(i * 7));

        var keys = await host.PageThrough(1);

        await Assert.That(keys).HasCount(6);
        await Assert.That(keys.Distinct()).HasCount(6);
    }

    [Test]
    public async Task Keeps_its_place_when_entries_are_deleted_behind_the_cursor()
    {
        // The page exists to delete entries from, so the reader must survive the collection
        // shrinking underneath it — which an offset-based pager would not.
        await using var host = DeadLetterTestHost.Start(fixture);
        for (var i = 0; i < 6; i++)
            await host.Seed($"task-{i}", Base.AddMinutes(i));

        var first = await host.Store.ListAsync(new DeadLetterQuery { PageSize = 2 });
        await Assert.That(first.Items.Select(i => i.IdempotencyKey)).IsEquivalentTo(new[] { "task-5", "task-4" });

        // Discard both entries just read, then continue from the cursor they produced.
        foreach (var item in first.Items)
            await host.Store.DeleteAsync(item.IdempotencyKey);

        var second = await host.Store.ListAsync(new DeadLetterQuery { PageSize = 2, Cursor = first.NextCursor });

        await Assert.That(second.Items.Select(i => i.IdempotencyKey)).IsEquivalentTo(new[] { "task-3", "task-2" });
    }

    [Test]
    [Arguments("not-a-cursor")]
    [Arguments("!!!!")]
    public async Task Rejects_a_cursor_it_did_not_produce(string cursor)
    {
        await using var host = DeadLetterTestHost.Start(fixture);

        await Assert.That(() => host.Store.ListAsync(new DeadLetterQuery { Cursor = cursor }))
            .Throws<ArgumentException>();
    }

    // ---------------------------------------------------------------------------- deleting ---

    [Test]
    public async Task Deletes_an_entry_once()
    {
        // MustExist rather than an unconditional delete, which Firestore reports as success on a
        // document that was never there — so the second caller learns someone got there first.
        await using var host = DeadLetterTestHost.Start(fixture);
        await host.Seed("task-1", Base);

        await Assert.That(await host.Store.DeleteAsync("task-1")).IsTrue();
        await Assert.That(await host.Store.DeleteAsync("task-1")).IsFalse();
        await Assert.That(await host.Store.GetAsync("task-1")).IsNull();
    }

    [Test]
    public async Task Reports_nothing_deleted_for_a_key_that_was_never_stored()
    {
        await using var host = DeadLetterTestHost.Start(fixture);

        await Assert.That(await host.Store.DeleteAsync("never-stored")).IsFalse();
    }

    // --------------------------------------------------------------------------- resending ---

    [Test]
    public async Task Resend_enqueues_the_command_and_removes_the_entry()
    {
        await using var host = DeadLetterTestHost.Start(fixture);
        await host.Seed("task-1", Base, payloadJson: """{"name":"ada"}""");

        await Assert.That(await ResendError(host, "task-1")).IsNull();

        await Assert.That(host.Queue.Enqueued).IsEquivalentTo(new object[] { new PingCommand("ada") });
        await Assert.That(await host.Store.GetAsync("task-1")).IsNull();
    }

    [Test]
    public async Task Resend_uses_a_corrected_payload()
    {
        await using var host = DeadLetterTestHost.Start(fixture);
        await host.Seed("task-1", Base, payloadJson: """{"name":"typo"}""");

        await Assert.That(await ResendError(host, "task-1", """{"name":"fixed"}""")).IsNull();

        await Assert.That(host.Queue.Enqueued).IsEquivalentTo(new object[] { new PingCommand("fixed") });
    }

    [Test]
    public async Task Resend_of_an_unregistered_command_type_changes_nothing()
    {
        await using var host = DeadLetterTestHost.Start(fixture);
        await host.Seed("task-1", Base, commandTypeName: "Renamed.Or.Moved.Command");

        var error = await ResendError(host, "task-1");

        await Assert.That(error).IsTypeOf<ResendDeadLetterError.UnknownCommandType>();
        await Assert.That(host.Queue.Enqueued).IsEmpty();
        // The payload is still readable, so the entry stays rather than being discarded.
        await Assert.That(await host.Store.GetAsync("task-1")).IsNotNull();
    }

    [Test]
    public async Task Resend_of_a_broken_payload_changes_nothing()
    {
        await using var host = DeadLetterTestHost.Start(fixture);
        await host.Seed("task-1", Base);

        var error = await ResendError(host, "task-1", "{ not json");

        await Assert.That(error).IsTypeOf<ResendDeadLetterError.InvalidPayload>();
        await Assert.That(host.Queue.Enqueued).IsEmpty();
        await Assert.That(await host.Store.GetAsync("task-1")).IsNotNull();
    }

    [Test]
    public async Task Resend_of_a_key_that_is_no_longer_stored_is_reported()
    {
        await using var host = DeadLetterTestHost.Start(fixture);

        await Assert.That(await ResendError(host, "already-handled"))
            .IsTypeOf<ResendDeadLetterError.NotFound>();
    }

    [Test]
    public async Task A_failed_enqueue_leaves_the_entry_to_be_resent_again()
    {
        // Firestore offers no transaction the queue can join, so the resend enqueues first and
        // deletes after. An interruption must therefore leave the work in the store rather than
        // losing it — at-least-once, matching the delivery guarantee the queue already gives.
        await using var host = DeadLetterTestHost.Start(fixture);
        await host.Seed("task-1", Base);
        host.Queue.FailWith = new InvalidOperationException("transport is down");

        await Assert.That(() => host.Resender.ResendAsync("task-1")).Throws<InvalidOperationException>();

        await Assert.That(await host.Store.GetAsync("task-1")).IsNotNull();
    }

    [Test]
    public async Task A_dead_lettered_command_can_be_listed_and_resent()
    {
        // The whole admin loop over one store: what the writer recorded is what the page shows and
        // what the resend puts back.
        await using var host = DeadLetterTestHost.Start(fixture);
        await host.Writer.WriteAsync(new DeadLetterEntry
        {
            IdempotencyKey = "cloud-tasks-id-42",
            Source = DeadLetterSource.Queue,
            CommandTypeName = typeof(PingCommand).FullName!,
            PayloadJson = """{"name":"grace"}""",
            Error = "handler threw",
            Attempts = 3,
        });

        var page = await host.Store.ListAsync(new DeadLetterQuery { PageSize = 10 });
        await Assert.That(page.Items.Select(i => i.IdempotencyKey))
            .IsEquivalentTo(new[] { "cloud-tasks-id-42" });

        await Assert.That(await ResendError(host, "cloud-tasks-id-42")).IsNull();

        await Assert.That(host.Queue.Enqueued).IsEquivalentTo(new object[] { new PingCommand("grace") });
        await Assert.That(await host.DocumentCount()).IsEqualTo(0);
    }
}
