using System.Data;
using System.Data.Common;
using Spinneret.Queue.Sql;

namespace Spinneret.Queue.Sql.Tests;

internal sealed class FakeTransaction : DbTransaction
{
    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
    protected override DbConnection? DbConnection => null;
    public override void Commit() { }
    public override void Rollback() { }
}

internal sealed class FakeDataSource : DbDataSource
{
    public bool Disposed { get; private set; }

    public override string ConnectionString => "fake";

    protected override DbConnection CreateDbConnection() => throw new NotSupportedException();

    protected override void Dispose(bool disposing) => Disposed = true;

    protected override ValueTask DisposeAsyncCore()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeDialect(int maxIdentifierLength = 30) : SqlQueueDialect
{
    public List<string> Calls { get; } = [];

    public bool Aborted { get; set; }

    public override string Name => "fake";

    public override int MaxIdentifierLength => maxIdentifierLength;

    public override string QuoteIdentifier(string identifier) => $"<{identifier}>";

    public override IEnumerable<string> DerivedIdentifiers(SqlQueueNames names) =>
        [$"ix_{names.QueueTable}_long_suffix", $"pk_{names.DeadLetterTable}"];

    public override SqlQueueStatements CreateStatements(SqlQueueNames names) => new()
    {
        Enqueue = "enqueue",
        Dequeue = "dequeue",
        LeaseForRelay = "lease",
        LeaseById = "lease-by-id",
        DeleteRelayed = "delete-relayed",
        ReleaseLease = "release",
        Reschedule = "reschedule",
        WriteDeadLetter = "write",
        ListDeadLetters = "list",
        ListDeadLettersAfter = "list-after",
        GetDeadLetter = "get",
        DeleteDeadLetter = "delete",
    };

    public override string CreateSchemaScript(SqlQueueNames names) => "schema";

    public override void AddTimestamp(DbCommand command, string name, DateTimeOffset utc) =>
        throw new NotSupportedException();

    public override DateTimeOffset ReadTimestamp(DbDataReader reader, int ordinal) =>
        throw new NotSupportedException();

    public override Task<bool> IsAbortedAsync(DbTransaction transaction, CancellationToken ct)
    {
        Calls.Add("is-aborted");
        return Task.FromResult(Aborted);
    }

    public override Task SaveAsync(DbTransaction transaction, string savepoint, CancellationToken ct)
    {
        Calls.Add($"save {savepoint}");
        return Task.CompletedTask;
    }

    public override Task RollbackToAsync(DbTransaction transaction, string savepoint, CancellationToken ct)
    {
        Calls.Add($"rollback-to {savepoint}");
        return Task.CompletedTask;
    }
}

internal sealed class FakeOptions : SqlStorageOptions
{
    public FakeOptions()
        : base("dbo", "queue", "dead")
    {
        ConnectionString = "fake";
    }
}
