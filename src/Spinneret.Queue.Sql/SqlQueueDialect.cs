using System.Data.Common;

namespace Spinneret.Queue.Sql;

// Statements bind @-prefixed named parameters, which both SqlClient and Npgsql accept.
// Internal because the statements are coupled to the code reading their results.
internal abstract class SqlQueueDialect
{
    // The scheduler checks this matches its own dialect.
    public abstract string Name { get; }

    public abstract SqlQueueStatements CreateStatements(SqlQueueNames names);

    // Must be idempotent and safe to run concurrently: every replica runs it at startup.
    public abstract string CreateSchemaScript(SqlQueueNames names);

    // Validated against MaxIdentifierLength: Postgres silently truncates long names until two collide.
    public abstract IEnumerable<string> DerivedIdentifiers(SqlQueueNames names);

    public abstract int MaxIdentifierLength { get; }

    // Defense in depth behind startup validation.
    public abstract string QuoteIdentifier(string identifier);

    public string Qualify(string schema, string table) => $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";

    // Configured names are spliced into SQL, so only plain identifiers are accepted.
    public bool IsValidIdentifier(string identifier) =>
        !string.IsNullOrWhiteSpace(identifier)
        && identifier.Length <= MaxIdentifierLength
        && (char.IsAsciiLetter(identifier[0]) || identifier[0] == '_')
        && identifier.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    public abstract void AddTimestamp(DbCommand command, string name, DateTimeOffset utc);

    public abstract DateTimeOffset ReadTimestamp(DbDataReader reader, int ordinal);

    // Whether the transaction refuses further work until rolled back. SQL Server's never does silently.
    public virtual Task<bool> IsAbortedAsync(DbTransaction transaction, CancellationToken ct) => Task.FromResult(false);

    public virtual Task SaveAsync(DbTransaction transaction, string savepoint, CancellationToken ct) =>
        transaction.SaveAsync(savepoint, ct);

    public virtual Task RollbackToAsync(DbTransaction transaction, string savepoint, CancellationToken ct) =>
        transaction.RollbackAsync(savepoint, ct);
}

internal sealed record SqlQueueNames(string Schema, string QueueTable, string DeadLetterTable);

// Each statement's parameters and result shape are the contract a dialect implements.
internal sealed record SqlQueueStatements
{
    // @MessageId, @Channel, @VisibleAt, @DedupeKey?, @Envelope. Returns true if inserted; false or no row on a
    // pending dedupe key, which must not abort the caller's transaction.
    public required string Enqueue { get; init; }

    // Claims the oldest due, unlocked, unleased row. @Channel, @Now. Returns 0..1 (Id, MessageId, Envelope).
    public required string Dequeue { get; init; }

    // Leases up to @Batch unleased (or expired before @Now) rows due by @Horizon, setting @LeaseUntil/@LeaseToken.
    // Returns (Id, MessageId, Channel, VisibleAt, DedupeKey?, Envelope).
    public required string LeaseForRelay { get; init; }

    // As LeaseForRelay, for the single row @MessageId.
    public required string LeaseById { get; init; }

    // @Id, @LeaseToken. Must match the token, so an expired relay can't delete a row another re-leased.
    public required string DeleteRelayed { get; init; }

    // @Id, @LeaseToken, @RetryAt: extends the lease as a send-failure backoff.
    public required string ReleaseLease { get; init; }

    // @Id, @VisibleAt.
    public required string Reschedule { get; init; }

    // A duplicate @IdempotencyKey is a no-op that must not abort the caller's transaction.
    public required string WriteDeadLetter { get; init; }

    // Newest @Take rows, sorted (DeadLetteredAt desc, IdempotencyKey desc).
    public required string ListDeadLetters { get; init; }

    // Continues after (@CursorDeadLetteredAt, @CursorIdempotencyKey).
    public required string ListDeadLettersAfter { get; init; }

    public required string GetDeadLetter { get; init; }

    // The affected row count tells whether it existed.
    public required string DeleteDeadLetter { get; init; }

    // The store reads these by ordinal; add new columns at the end only.
    public const string DeadLetterColumns =
        "IdempotencyKey, Source, CommandTypeName, Description, PayloadJson, Error, Attempts, DeadLetteredAt, TraceId";
}
