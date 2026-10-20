using Spinneret.Queue.Sql;

namespace Spinneret.Scheduler.Sql;

/// <summary>
/// The scheduler's half of the SQL storage: its dialect and table, beside the queue storage it
/// shares a database — and a transaction — with.
/// </summary>
internal sealed class SqlSchedulerStorage
{
    public SqlSchedulerStorage(SqlQueueStorage queue, SqlSchedulerDialect dialect, string table)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(dialect);

        if (!string.Equals(dialect.QueueDialectName, queue.Dialect.Name, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The scheduler's '{dialect.QueueDialectName}' storage cannot share a transaction with a "
                + $"'{queue.Dialect.Name}' queue: the scheduler and the queue must live in the same database.");

        Queue = queue;
        Dialect = dialect;
        Names = new SqlSchedulerNames(queue.Names.Schema, table);
        Statements = dialect.CreateStatements(Names);
    }

    public SqlQueueStorage Queue { get; }
    public SqlSchedulerDialect Dialect { get; }
    public SqlSchedulerNames Names { get; }
    public SqlSchedulerStatements Statements { get; }
}

/// <summary>
/// One-shot job handles. One-shot handles and caller-chosen recurring keys share the JobKey
/// namespace, so the prefix is what tells them apart without a read — it keeps a cancel from
/// destroying a recurring job's schedule.
/// </summary>
internal static class ScheduledJobHandle
{
    public const string OneShotPrefix = "oneshot-";

    public static string New() => $"{OneShotPrefix}{Guid.NewGuid():N}";

    public static bool IsOneShot(string handle) => handle.StartsWith(OneShotPrefix, StringComparison.Ordinal);
}
