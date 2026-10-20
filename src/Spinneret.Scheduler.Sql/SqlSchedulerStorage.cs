using Spinneret.Queue.Sql;

namespace Spinneret.Scheduler.Sql;

internal sealed class SqlSchedulerStorage
{
    public SqlSchedulerStorage(SqlQueueStorage queue, SqlSchedulerDialect dialect, string table)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(dialect);

        if (!string.Equals(dialect.QueueDialect.Name, queue.Dialect.Name, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The scheduler's '{dialect.QueueDialect.Name}' storage cannot share a transaction with a "
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

// One-shot handles share the JobKey namespace with recurring keys; the prefix keeps a cancel
// from destroying a recurring schedule.
internal static class ScheduledJobHandle
{
    public const string OneShotPrefix = "oneshot-";

    public static string New() => $"{OneShotPrefix}{Guid.NewGuid():N}";

    public static bool IsOneShot(string handle) => handle.StartsWith(OneShotPrefix, StringComparison.Ordinal);
}
