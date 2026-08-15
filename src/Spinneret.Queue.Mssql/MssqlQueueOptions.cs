using System.Reflection;

namespace Spinneret.Queue.Mssql;

public sealed class MssqlQueueOptions
{
    public static readonly string SectionName = "Queue:Mssql";

    /// <summary>
    /// Assemblies scanned for the <c>IRequest&lt;&gt;</c> command types this queue can carry. Set in
    /// code — assemblies cannot come from configuration — and required: a queue with nothing to
    /// enqueue fails at registration rather than at the first <c>Enqueue</c>.
    /// </summary>
    /// <remarks>
    /// Types are registered by <see cref="Type.FullName"/>, which is the name on the wire, so
    /// renaming or moving a queued command type breaks messages already in flight.
    /// </remarks>
    public IReadOnlyCollection<Assembly> RequestAssemblies { get; set; } = [];

    /// <summary>
    /// SQL Server connection string for the queue tables. The queue must live in the same database
    /// as the application's own tables — that is what makes an enqueue atomic with the business
    /// write it belongs to, and a handler's writes atomic with the dequeue.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Name of an entry in the standard ConnectionStrings section, read when
    /// <see cref="ConnectionString"/> is not set directly.
    /// </summary>
    public string? ConnectionStringName { get; set; }

    /// <summary>Schema the queue tables live in.</summary>
    public string SchemaName { get; set; } = "dbo";

    /// <summary>Table holding pending queue messages.</summary>
    public string QueueTableName { get; set; } = "SpinneretQueue";

    /// <summary>Table the built-in <see cref="Spinneret.Queue.IDeadLetterWriter"/> writes to.</summary>
    public string DeadLetterTableName { get; set; } = "SpinneretDeadLetters";

    /// <summary>
    /// Create the queue tables idempotently at startup. Disable when the host owns the schema
    /// through its own migrations; <see cref="MssqlQueueSchema.CreateScript"/> yields the DDL.
    /// </summary>
    public bool CreateSchema { get; set; } = true;

    /// <summary>How long an idle worker waits before polling its channel again.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Concurrent deliveries per channel; a channel not listed here is consumed one message at a
    /// time. Keys must be channels declared by a [QueuePolicy] (or the default channel), so a typo
    /// fails the host at boot instead of silently configuring a channel that never runs.
    /// </summary>
    public Dictionary<string, int> ChannelParallelism { get; set; } = new(StringComparer.Ordinal);
}
