using System.Data.Common;

namespace Spinneret.Queue.Sql;

/// <summary>
/// Everything about the queue's storage that differs between databases: the statements, the DDL,
/// and how timestamps and savepoints are spelled to the provider. The rest — the outbox, the worker,
/// the unit of work, the dead-letter store — is written once against ADO.NET in this package.
/// </summary>
/// <remarks>
/// <para>
/// Statements bind named parameters with an <c>@</c> prefix, which both SqlClient and Npgsql accept.
/// Each statement's parameters and result shape are documented on <see cref="SqlQueueStatements"/>
/// and are the contract a dialect implements.
/// </para>
/// <para>
/// Internal and visible to the in-tree dialect packages only: the statements are tightly coupled to
/// the code that reads their results, and publishing them would freeze both.
/// </para>
/// </remarks>
internal abstract class SqlQueueDialect
{
    /// <summary>Short provider name, e.g. <c>mssql</c>; the scheduler checks it matches its own dialect.</summary>
    public abstract string Name { get; }

    /// <summary>The statements for the configured table names, built once at startup.</summary>
    public abstract SqlQueueStatements CreateStatements(SqlQueueNames names);

    /// <summary>
    /// Idempotent creation script for the queue and dead-letter tables. Must be safe to run
    /// concurrently: every host runs it at startup, and a fleet scaling from zero runs it from every
    /// replica at once.
    /// </summary>
    public abstract string CreateSchemaScript(SqlQueueNames names);

    /// <summary>
    /// Every index and constraint name the DDL derives from the configured table names. Validation
    /// checks them against <see cref="MaxIdentifierLength"/> along with the configured names, since a
    /// table name that fits can still derive one that does not — which SQL Server rejects at startup
    /// and Postgres silently truncates, until two truncated names collide.
    /// </summary>
    public abstract IEnumerable<string> DerivedIdentifiers(SqlQueueNames names);

    /// <summary>The longest identifier the database accepts, in characters (identifiers are ASCII).</summary>
    public abstract int MaxIdentifierLength { get; }

    /// <summary>Quotes and escapes a configured identifier: defense in depth behind startup validation.</summary>
    public abstract string QuoteIdentifier(string identifier);

    /// <summary>A schema-qualified, quoted table name.</summary>
    public string Qualify(string schema, string table) => $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";

    /// <summary>
    /// Whether <paramref name="identifier"/> is a plain identifier — letters, digits and underscores,
    /// not starting with a digit — short enough for the database. Configured names are spliced into
    /// SQL, so nothing else is accepted, even though quoting would make more of it safe.
    /// </summary>
    public bool IsValidIdentifier(string identifier) =>
        !string.IsNullOrWhiteSpace(identifier)
        && identifier.Length <= MaxIdentifierLength
        && (char.IsAsciiLetter(identifier[0]) || identifier[0] == '_')
        && identifier.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>Binds a UTC instant to a timestamp column of the dialect's DDL.</summary>
    public abstract void AddTimestamp(DbCommand command, string name, DateTimeOffset utc);

    /// <summary>Reads back a timestamp written through <see cref="AddTimestamp"/>.</summary>
    public abstract DateTimeOffset ReadTimestamp(DbDataReader reader, int ordinal);

    /// <summary>
    /// Whether a statement failed in <paramref name="transaction"/> in a way that left it refusing
    /// all further work until a rollback. Checked after each handler, which may have caught such an
    /// error itself. The default is false: on SQL Server a failed statement leaves the transaction
    /// usable, and one the server has doomed fails its commit loudly anyway.
    /// </summary>
    public virtual Task<bool> IsAbortedAsync(DbTransaction transaction, CancellationToken ct) => Task.FromResult(false);

    /// <summary>Sets a savepoint on <paramref name="transaction"/>.</summary>
    public virtual Task SaveAsync(DbTransaction transaction, string savepoint, CancellationToken ct) =>
        transaction.SaveAsync(savepoint, ct);

    /// <summary>Rolls <paramref name="transaction"/> back to a savepoint, keeping the transaction open.</summary>
    public virtual Task RollbackToAsync(DbTransaction transaction, string savepoint, CancellationToken ct) =>
        transaction.RollbackAsync(savepoint, ct);
}

/// <summary>The configured names the statements and the DDL are built from, already validated.</summary>
internal sealed record SqlQueueNames(string Schema, string QueueTable, string DeadLetterTable);

/// <summary>
/// The statements the SQL storage executes. Each is described by its parameters and the shape of its
/// result; a dialect is free in how it achieves them.
/// </summary>
internal sealed record SqlQueueStatements
{
    /// <summary>
    /// Inserts one message. Parameters: <c>@MessageId</c> (guid), <c>@Channel</c>,
    /// <c>@VisibleAt</c> (timestamp), <c>@DedupeKey</c> (nullable), <c>@Envelope</c>. Returns one
    /// scalar that is true when a row was inserted; false, or no row at all, when a message with the
    /// same dedupe key is already pending. A dedupe conflict must not abort the caller's transaction.
    /// </summary>
    public required string Enqueue { get; init; }

    /// <summary>
    /// Destructively claims the oldest due message on a channel, skipping rows other deliveries hold
    /// and rows a relay has leased. Parameters: <c>@Channel</c>, <c>@Now</c> (timestamp). Returns
    /// zero or one row of (Id bigint, MessageId guid, Envelope string).
    /// </summary>
    public required string Dequeue { get; init; }

    /// <summary>
    /// Leases up to <c>@Batch</c> messages due by <c>@Horizon</c> (timestamp) that no relay holds
    /// — unleased, or with a lease that ran out before <c>@Now</c> — oldest due first, skipping rows
    /// other transactions hold. Sets <c>LeasedUntil = @LeaseUntil</c> and
    /// <c>LeaseToken = @LeaseToken</c> (guid). Returns the leased rows as (Id bigint, MessageId guid,
    /// Channel string, VisibleAt timestamp, DedupeKey string nullable, Envelope string).
    /// </summary>
    public required string LeaseForRelay { get; init; }

    /// <summary>
    /// As <see cref="LeaseForRelay"/> for the one message under <c>@MessageId</c> — the eager
    /// forward of a message just committed — still only if it is due by <c>@Horizon</c>. Parameters:
    /// <c>@MessageId</c>, <c>@Horizon</c>, <c>@Now</c>, <c>@LeaseUntil</c>, <c>@LeaseToken</c>.
    /// </summary>
    public required string LeaseById { get; init; }

    /// <summary>
    /// Removes a forwarded message, only while the lease that forwarded it still holds it — a relay
    /// whose lease ran out must not delete a row another relay re-leased. Parameters: <c>@Id</c>,
    /// <c>@LeaseToken</c>.
    /// </summary>
    public required string DeleteRelayed { get; init; }

    /// <summary>
    /// Keeps a message that failed to send leased until <c>@RetryAt</c> (timestamp), so the next
    /// attempt waits out a backoff. Parameters: <c>@Id</c>, <c>@LeaseToken</c>, <c>@RetryAt</c>.
    /// </summary>
    public required string ReleaseLease { get; init; }

    /// <summary>Moves a message's visibility. Parameters: <c>@Id</c>, <c>@VisibleAt</c> (timestamp).</summary>
    public required string Reschedule { get; init; }

    /// <summary>
    /// Records a dead letter; a duplicate idempotency key is a no-op, not an error, and must not
    /// abort the caller's transaction. Parameters: <c>@IdempotencyKey</c>, <c>@Source</c>,
    /// <c>@CommandTypeName</c>, <c>@Description</c>, <c>@PayloadJson</c>, <c>@Error</c>,
    /// <c>@Attempts</c>, <c>@DeadLetteredAt</c> (timestamp), <c>@TraceId</c>.
    /// </summary>
    public required string WriteDeadLetter { get; init; }

    /// <summary>
    /// The newest <c>@Take</c> dead letters in <see cref="DeadLetterColumns"/> order, sorted by
    /// (DeadLetteredAt desc, IdempotencyKey desc).
    /// </summary>
    public required string ListDeadLetters { get; init; }

    /// <summary>
    /// As <see cref="ListDeadLetters"/>, continuing after the row at (<c>@CursorDeadLetteredAt</c>,
    /// <c>@CursorIdempotencyKey</c>).
    /// </summary>
    public required string ListDeadLettersAfter { get; init; }

    /// <summary>The dead letter under <c>@IdempotencyKey</c>, in <see cref="DeadLetterColumns"/> order.</summary>
    public required string GetDeadLetter { get; init; }

    /// <summary>Deletes the dead letter under <c>@IdempotencyKey</c>; the affected row count tells whether it existed.</summary>
    public required string DeleteDeadLetter { get; init; }

    /// <summary>
    /// The projection every dead-letter read returns, in the order the store reads its ordinals.
    /// TraceId is last on purpose: a column added anywhere else would silently shift every existing
    /// field one place.
    /// </summary>
    public const string DeadLetterColumns =
        "IdempotencyKey, Source, CommandTypeName, Description, PayloadJson, Error, Attempts, DeadLetteredAt, TraceId";
}
