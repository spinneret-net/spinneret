using Spinneret.Queue.Sql;

namespace Spinneret.Queue.Sql.Tests;

public sealed class AsyncLocalSqlTransactionProviderTests
{
    [Test]
    public async Task Current_is_null_by_default()
    {
        var provider = new AsyncLocalSqlTransactionProvider();

        await Assert.That(provider.Current).IsNull();
    }

    [Test]
    public async Task Use_publishes_to_the_current_async_flow()
    {
        var provider = new AsyncLocalSqlTransactionProvider();
        var transaction = new FakeTransaction();

        provider.Use(transaction);

        await Assert.That(provider.Current).IsSameReferenceAs(transaction);
        provider.Use(null);
        await Assert.That(provider.Current).IsNull();
    }

    [Test]
    public async Task Parallel_flows_see_their_own_transaction()
    {
        var provider = new AsyncLocalSqlTransactionProvider();
        var a = new FakeTransaction();
        var b = new FakeTransaction();

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
        // Children cannot leak upward.
        await Assert.That(provider.Current).IsNull();
    }
}
