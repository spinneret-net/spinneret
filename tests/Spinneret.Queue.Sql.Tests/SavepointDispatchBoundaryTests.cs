using Microsoft.Extensions.Logging.Abstractions;
using Spinneret.Queue.Sql;

namespace Spinneret.Queue.Sql.Tests;

/// <summary>
/// What the boundary asks of the dialect around a handler: a savepoint before, a rollback to it
/// when the handler fails — including when it "succeeds" on a transaction the database aborted.
/// </summary>
public sealed class SavepointDispatchBoundaryTests
{
    private static readonly QueueDeliveryContext Context = new()
    {
        Envelope = new QueueEnvelope
        {
            RequestTypeName = "Acme.DoThing",
            PayloadJson = "{}",
            EnqueuedAtUtc = DateTimeOffset.UnixEpoch,
        },
        TaskId = "task-1",
    };

    private static (SavepointDispatchBoundary Boundary, FakeDialect Dialect) Create(bool withTransaction = true)
    {
        var dialect = new FakeDialect();
        var storage = new SqlQueueStorage(new FakeDataSource(), dialect, new SqlQueueNames("s", "q", "d"), createSchema: false);
        var transactions = new AsyncLocalSqlTransactionProvider();
        if (withTransaction)
            transactions.Use(new FakeTransaction());

        return (new SavepointDispatchBoundary(storage, transactions, NullLogger<SavepointDispatchBoundary>.Instance), dialect);
    }

    [Test]
    public async Task A_successful_handler_runs_after_a_savepoint_and_keeps_its_work()
    {
        var (boundary, dialect) = Create();
        var ran = false;

        await boundary.ExecuteAsync(Context, () => { ran = true; return Task.CompletedTask; }, CancellationToken.None);

        await Assert.That(ran).IsTrue();
        await Assert.That(dialect.Calls).IsEquivalentTo(["save SpinneretDispatch", "is-aborted"]);
    }

    [Test]
    public async Task A_failing_handler_is_rolled_back_to_the_savepoint_and_its_exception_kept()
    {
        var (boundary, dialect) = Create();

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            boundary.ExecuteAsync(Context, () => throw new TimeoutException("handler"), CancellationToken.None));

        await Assert.That(ex!.Message).IsEqualTo("handler");
        await Assert.That(dialect.Calls).IsEquivalentTo(["save SpinneretDispatch", "rollback-to SpinneretDispatch"]);
    }

    [Test]
    public async Task A_handler_that_returned_on_an_aborted_transaction_fails_and_is_rolled_back()
    {
        // The handler caught a database error itself. Its writes cannot commit — the database
        // refuses the transaction — so reporting success would lose them along with the
        // acknowledge. Failing the attempt keeps the retry and dead-letter accounting honest.
        var (boundary, dialect) = Create();
        dialect.Aborted = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            boundary.ExecuteAsync(Context, () => Task.CompletedTask, CancellationToken.None));

        await Assert.That(ex!.Message).Contains("Acme.DoThing");
        await Assert.That(ex.Message).Contains("aborted the delivery's transaction");
        await Assert.That(dialect.Calls)
            .IsEquivalentTo(["save SpinneretDispatch", "is-aborted", "rollback-to SpinneretDispatch"]);
    }

    [Test]
    public async Task Without_an_ambient_transaction_the_handler_runs_unbracketed()
    {
        var (boundary, dialect) = Create(withTransaction: false);
        var ran = false;

        await boundary.ExecuteAsync(Context, () => { ran = true; return Task.CompletedTask; }, CancellationToken.None);

        await Assert.That(ran).IsTrue();
        await Assert.That(dialect.Calls).IsEmpty();
    }
}
