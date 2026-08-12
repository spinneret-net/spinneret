using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Spinneret.Queue.Mssql.Tests;

public sealed class AsyncLocalMssqlTransactionProviderTests
{
    [Test]
    public async Task Current_is_null_by_default()
    {
        var provider = new AsyncLocalMssqlTransactionProvider();

        await Assert.That(provider.Current).IsNull();
    }

    [Test]
    public async Task Use_publishes_to_the_current_async_flow()
    {
        var provider = new AsyncLocalMssqlTransactionProvider();
        await using var connection = new SqlConnection();
        DbTransaction transaction = FakeTransaction.For(connection);

        provider.Use(transaction);

        await Assert.That(provider.Current).IsSameReferenceAs(transaction);
        provider.Use(null);
        await Assert.That(provider.Current).IsNull();
    }

    [Test]
    public async Task Parallel_flows_see_their_own_transaction()
    {
        var provider = new AsyncLocalMssqlTransactionProvider();
        await using var connection = new SqlConnection();
        var a = FakeTransaction.For(connection);
        var b = FakeTransaction.For(connection);

        var flowA = Task.Run(async () =>
        {
            provider.Use(a);
            await Task.Delay(50);
            return provider.Current;
        });
        var flowB = Task.Run(async () =>
        {
            provider.Use(b);
            await Task.Delay(50);
            return provider.Current;
        });

        await Assert.That(await flowA).IsSameReferenceAs(a);
        await Assert.That(await flowB).IsSameReferenceAs(b);
        // The outer flow never set one, and children cannot leak upward.
        await Assert.That(provider.Current).IsNull();
    }
}

/// <summary>Minimal DbTransaction stand-in; the provider only stores and returns references.</summary>
internal sealed class FakeTransaction : DbTransaction
{
    private readonly DbConnection _connection;

    private FakeTransaction(DbConnection connection) => _connection = connection;

    public static FakeTransaction For(DbConnection connection) => new(connection);

    public override System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.ReadCommitted;
    protected override DbConnection? DbConnection => _connection;
    public override void Commit() { }
    public override void Rollback() { }
}
