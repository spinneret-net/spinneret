using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Spinneret.Queue;

namespace Spinneret.Queue.Mssql.Tests;

/// <summary>
/// The admin side against a real SQL Server: paging that stays correct while rows are deleted
/// underneath it, and a resend whose enqueue and delete land in one transaction.
/// </summary>
[ClassDataSource<MssqlContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class MssqlDeadLetterStoreTests(MssqlContainerFixture fixture)
{
    private static readonly DateTimeOffset Base = new(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Inserts a dead letter with an exact timestamp — the writer's own clock cannot produce the
    /// ties and orderings these tests need.
    /// </summary>
    private static async Task Seed(
        QueueTestHost host,
        string key,
        DateTimeOffset deadLetteredAt,
        string? commandTypeName = null,
        string payloadJson = """{"Name":"seeded"}""",
        DeadLetterSource source = DeadLetterSource.Queue,
        bool withDescription = true)
    {
        await using var connection = await host.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO [{host.Options.DeadLetterTableName}]
                (IdempotencyKey, Source, CommandTypeName, Description, PayloadJson, Error, Attempts, DeadLetteredAt)
            VALUES (@Key, @Source, @Type, @Description, @Payload, 'boom', 3, @At);
            """;
        command.Parameters.AddWithValue("@Key", key);
        command.Parameters.AddWithValue("@Source", source.ToString());
        command.Parameters.AddWithValue("@Type", commandTypeName ?? typeof(PingCommand).FullName!);
        command.Parameters.AddWithValue(
            "@Description", withDescription ? $"seeded {key}" : DBNull.Value);
        command.Parameters.AddWithValue("@Payload", payloadJson);
        command.Parameters.Add(new SqlParameter("@At", SqlDbType.DateTime2) { Value = deadLetteredAt.UtcDateTime });
        await command.ExecuteNonQueryAsync();
    }

    private static IDeadLetterStore Store(QueueTestHost host) =>
        host.Services.GetRequiredService<IDeadLetterStore>();

    private static IDeadLetterResender Resender(QueueTestHost host) =>
        host.Services.GetRequiredService<IDeadLetterResender>();

    /// <summary>The refusal a resend came back with, or null if it went through.</summary>
    private static async Task<ResendDeadLetterError?> ResendError(
        QueueTestHost host, string key, string? payloadJson = null) =>
        (await Resender(host).ResendAsync(key, payloadJson))
        .Match<ResendDeadLetterError?>(() => null, error => error);

    /// <summary>Walks every page, returning the keys in order and guarding against a runaway loop.</summary>
    private static async Task<List<string>> PageThrough(IDeadLetterStore store, int pageSize)
    {
        var keys = new List<string>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var page = await store.ListAsync(new DeadLetterQuery { PageSize = pageSize, Cursor = cursor });
            keys.AddRange(page.Items.Select(i => i.IdempotencyKey));
            cursor = page.NextCursor;

            if (++pages > 100)
                throw new InvalidOperationException("Paging did not terminate.");
        }
        while (cursor is not null);

        return keys;
    }

    // ------------------------------------------------------------------------------ reads ---

    [Test]
    public async Task Reads_back_every_field_that_was_stored()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString, worker: false);
        await Seed(host, "task-1", Base, source: DeadLetterSource.Scheduler);

        var deadLetter = await Store(host).GetAsync("task-1");

        await Assert.That(deadLetter).IsNotNull();
        await Assert.That(deadLetter!.IdempotencyKey).IsEqualTo("task-1");
        await Assert.That(deadLetter.Source).IsEqualTo(DeadLetterSource.Scheduler);
        await Assert.That(deadLetter.CommandTypeName).IsEqualTo(typeof(PingCommand).FullName);
        await Assert.That(deadLetter.Description).IsEqualTo("seeded task-1");
        await Assert.That(deadLetter.Error).IsEqualTo("boom");
        await Assert.That(deadLetter.Attempts).IsEqualTo(3);
        await Assert.That(deadLetter.DeadLetteredAt).IsEqualTo(Base);
    }

    [Test]
    public async Task Reads_back_what_the_writer_itself_wrote()
    {
        // The seeding helper above writes its own SQL; this proves the writer and the store agree
        // without it in the middle.
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString, worker: false);
        var writer = host.Services.GetRequiredService<IDeadLetterWriter>();

        await writer.WriteAsync(new DeadLetterEntry
        {
            IdempotencyKey = "written-1",
            Source = DeadLetterSource.Scheduler,
            CommandTypeName = typeof(PingCommand).FullName!,
            Description = "from the writer",
            PayloadJson = """{"Name":"x"}""",
            Error = "it broke",
            Attempts = 7,
        });

        var deadLetter = await Store(host).GetAsync("written-1");

        await Assert.That(deadLetter).IsNotNull();
        await Assert.That(deadLetter!.Source).IsEqualTo(DeadLetterSource.Scheduler);
        await Assert.That(deadLetter.Description).IsEqualTo("from the writer");
        await Assert.That(deadLetter.Error).IsEqualTo("it broke");
        await Assert.That(deadLetter.Attempts).IsEqualTo(7);
    }

    [Test]
    public async Task Reads_a_null_description_back_as_null()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString, worker: false);
        await Seed(host, "no-desc", Base, withDescription: false);

        var deadLetter = await Store(host).GetAsync("no-desc");

        await Assert.That(deadLetter!.Description).IsNull();
    }

    [Test]
    public async Task Returns_null_for_a_key_that_was_never_stored()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString, worker: false);

        await Assert.That(await Store(host).GetAsync("absent")).IsNull();
    }

    [Test]
    public async Task Lists_newest_first()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString, worker: false);
        await Seed(host, "oldest", Base);
        await Seed(host, "newest", Base.AddMinutes(2));
        await Seed(host, "middle", Base.AddMinutes(1));

        var page = await Store(host).ListAsync(new DeadLetterQuery());

        await Assert.That(page.Items.Select(i => i.IdempotencyKey).ToArray())
            .IsEquivalentTo(new[] { "newest", "middle", "oldest" });
    }

    [Test]
    public async Task Reports_no_cursor_when_the_page_is_the_last_one()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString, worker: false);
        await Seed(host, "a", Base);
        await Seed(host, "b", Base.AddMinutes(1));

        var page = await Store(host).ListAsync(new DeadLetterQuery { PageSize = 2 });

        // Exactly a full page, nothing behind it: paging has to stop on the null cursor rather than
        // on a short page, or this returns an empty extra page every time.
        await Assert.That(page.Items.Count).IsEqualTo(2);
        await Assert.That(page.NextCursor).IsNull();
    }

    [Test]
    public async Task Reports_an_empty_page_with_no_cursor_when_nothing_is_stored()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString, worker: false);

        var page = await Store(host).ListAsync(new DeadLetterQuery());

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
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString, worker: false);
        var expected = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            await Seed(host, $"task-{i}", Base.AddMinutes(i));
            expected.Insert(0, $"task-{i}");
        }

        var keys = await PageThrough(Store(host), pageSize);

        await Assert.That(keys).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Pages_through_entries_sharing_one_instant()
    {
        // The whole reason the key is in the sort: entries dead-lettered in the same millisecond are
        // ordered only by it, and without that a cursor landing inside the group skips or repeats.
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString, worker: false);
        for (var i = 0; i < 5; i++)
            await Seed(host, $"tie-{i}", Base);

        var keys = await PageThrough(Store(host), pageSize: 2);

        await Assert.That(keys).IsEquivalentTo(new[] { "tie-4", "tie-3", "tie-2", "tie-1", "tie-0" });
    }

    [Test]
    public async Task Keeps_its_place_when_rows_are_deleted_behind_the_cursor()
    {
        // The page exists so entries can be discarded from it, so the reader is always racing its
        // own deletions. Keyset paging holds its position; an offset would slide by one per delete.
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString, worker: false);
        for (var i = 0; i < 6; i++)
            await Seed(host, $"task-{i}", Base.AddMinutes(i));

        var store = Store(host);
        var first = await store.ListAsync(new DeadLetterQuery { PageSize = 2 });
        foreach (var item in first.Items)
            await store.DeleteAsync(item.IdempotencyKey);

        var second = await store.ListAsync(new DeadLetterQuery { PageSize = 2, Cursor = first.NextCursor });

        await Assert.That(first.Items.Select(i => i.IdempotencyKey).ToArray())
            .IsEquivalentTo(new[] { "task-5", "task-4" });
        await Assert.That(second.Items.Select(i => i.IdempotencyKey).ToArray())
            .IsEquivalentTo(new[] { "task-3", "task-2" });
    }

    [Test]
    public async Task Rejects_a_cursor_it_did_not_produce()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString, worker: false);

        await Assert.That(async () =>
                await Store(host).ListAsync(new DeadLetterQuery { Cursor = "nonsense" }))
            .Throws<ArgumentException>();
    }

    // ---------------------------------------------------------------------------- deletes ---

    [Test]
    public async Task Deletes_an_entry_once()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString, worker: false);
        await Seed(host, "task-1", Base);

        await Assert.That(await Store(host).DeleteAsync("task-1")).IsTrue();
        await Assert.That(await Store(host).DeleteAsync("task-1")).IsFalse();
        await Assert.That(await host.DeadLetterRowCount()).IsEqualTo(0);
    }

    // ----------------------------------------------------------------------------- resend ---

    [Test]
    public async Task Resend_enqueues_the_command_and_removes_the_entry()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString);
        await Seed(host, "task-1", Base, payloadJson: """{"Name":"resent"}""");

        var result = await Resender(host).ResendAsync("task-1");

        await Assert.That(result.Match(() => true, _ => false)).IsTrue();
        await Wait.Until(() => host.Log.DeliveryCount("ping:resent") == 1, "the resent command to be delivered");
        await Wait.Until(async () => await host.DeadLetterRowCount() == 0, "the entry to be removed");
    }

    [Test]
    public async Task Resend_uses_a_corrected_payload()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString);
        await Seed(host, "task-1", Base, payloadJson: """{"Name":"broken"}""");

        await Resender(host).ResendAsync("task-1", """{"Name":"fixed"}""");

        await Wait.Until(() => host.Log.DeliveryCount("ping:fixed") == 1, "the corrected command to be delivered");
    }

    [Test]
    public async Task Resend_enqueues_and_deletes_in_one_transaction()
    {
        // Both operations enlist in the caller's ambient transaction, so rolling it back has to undo
        // the pair. That is the guarantee the transaction scope exists for, and the difference from
        // the ordered-but-separate behaviour a transport without a shared database can offer.
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString, worker: false);
        await Seed(host, "task-1", Base);

        await using (var connection = await host.OpenConnectionAsync())
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync())
        {
            host.Transactions.Use(transaction);
            try
            {
                await Resender(host).ResendAsync("task-1");
                await transaction.RollbackAsync();
            }
            finally
            {
                host.Transactions.Use(null);
            }
        }

        await Assert.That(await host.QueueRowCount()).IsEqualTo(0);
        await Assert.That(await host.DeadLetterRowCount()).IsEqualTo(1);
    }

    [Test]
    public async Task Resend_commits_both_halves_when_no_transaction_is_ambient()
    {
        // The scope opens and commits its own transaction; with the worker off, the enqueued row
        // stays put and can be counted.
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString, worker: false);
        await Seed(host, "task-1", Base);

        await Resender(host).ResendAsync("task-1");

        await Assert.That(await host.QueueRowCount()).IsEqualTo(1);
        await Assert.That(await host.DeadLetterRowCount()).IsEqualTo(0);
        await Assert.That(host.Transactions.Current).IsNull();
    }

    [Test]
    public async Task Resend_of_an_unregistered_command_type_changes_nothing()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString, worker: false);
        await Seed(host, "task-1", Base, commandTypeName: "Acme.RenamedCommand");

        var error = await ResendError(host, "task-1");

        await Assert.That(error).IsTypeOf<ResendDeadLetterError.UnknownCommandType>();
        await Assert.That(await host.QueueRowCount()).IsEqualTo(0);
        await Assert.That(await host.DeadLetterRowCount()).IsEqualTo(1);
    }

    [Test]
    public async Task Resend_of_a_broken_payload_changes_nothing()
    {
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString, worker: false);
        await Seed(host, "task-1", Base);

        var error = await ResendError(host, "task-1", "{ not json");

        await Assert.That(error).IsTypeOf<ResendDeadLetterError.InvalidPayload>();
        await Assert.That(await host.QueueRowCount()).IsEqualTo(0);
        await Assert.That(await host.DeadLetterRowCount()).IsEqualTo(1);
    }

    // --------------------------------------------------------------------- dead-letter flow ---

    [Test]
    public async Task A_dead_lettered_command_can_be_listed_and_resent()
    {
        // The whole loop the admin page exists for, with nothing seeded by hand: a command exhausts
        // its attempts, appears in the listing, and comes back to life on resend.
        await using var host = await QueueTestHost.StartAsync(fixture.ConnectionString);

        await host.Queue.Enqueue(new AlwaysFailCommand("doomed"));
        await Wait.Until(async () => await host.DeadLetterRowCount() == 1, "the command to be dead-lettered");

        var page = await Store(host).ListAsync(new DeadLetterQuery());
        var entry = page.Items.Single();

        await Assert.That(entry.Source).IsEqualTo(DeadLetterSource.Queue);
        await Assert.That(entry.CommandTypeName).IsEqualTo(typeof(AlwaysFailCommand).FullName);
        await Assert.That(entry.Attempts).IsGreaterThan(0);
        await Assert.That(entry.DeadLetteredAt).IsGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));

        var attemptsBefore = host.Log.Attempts("doomed");
        await Resender(host).ResendAsync(entry.IdempotencyKey);

        await Wait.Until(() => host.Log.Attempts("doomed") > attemptsBefore, "the resent command to run again");
    }
}
