namespace Spinneret.Queue;

/// <summary>
/// One stored dead letter, as read back out of an <see cref="IDeadLetterStore"/>. The write-side
/// counterpart is <see cref="DeadLetterEntry"/>; this adds the two things only the store knows —
/// the key it was filed under and when it landed.
/// </summary>
/// <remarks>
/// Deliberately flat rather than wrapping a <see cref="DeadLetterEntry"/>: the consumer is an admin
/// page, and <c>deadLetter.Entry.CommandTypeName</c> buys nothing. The duplication is held in check
/// by a test asserting every <see cref="DeadLetterEntry"/> property also exists here, so a field
/// added to the write side fails the build until it is mirrored.
/// </remarks>
public sealed record DeadLetter
{
    /// <summary>The store's key for this entry — <see cref="DeadLetterEntry.IdempotencyKey"/>.</summary>
    public required string IdempotencyKey { get; init; }

    public required DeadLetterSource Source { get; init; }
    public required string CommandTypeName { get; init; }
    public string? Description { get; init; }
    public required string PayloadJson { get; init; }
    public required string Error { get; init; }
    public required int Attempts { get; init; }

    /// <summary>The failed execution's 32-hex trace id — <see cref="DeadLetterEntry.TraceId"/>.</summary>
    public string? TraceId { get; init; }

    /// <summary>When the entry was first recorded. Never moves: writers keep the first write.</summary>
    public required DateTimeOffset DeadLetteredAt { get; init; }
}

/// <summary>
/// One page of an <see cref="IDeadLetterStore.ListAsync"/> request. Entries come back newest first.
/// </summary>
/// <remarks>
/// A record with only optional additions planned — new filters or orderings ship as extra
/// <c>init</c> properties, which existing callers ignore.
/// </remarks>
public sealed record DeadLetterQuery
{
    /// <summary>The largest <see cref="PageSize"/> a store will accept.</summary>
    public const int MaxPageSize = 500;

    /// <summary>
    /// How many entries to return. Out-of-range values throw rather than being clamped silently —
    /// a page size coming off a query string is the caller's to validate.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Not between 1 and <see cref="MaxPageSize"/>.
    /// </exception>
    public int PageSize
    {
        get;
        init => field = value is < 1 or > MaxPageSize
            ? throw new ArgumentOutOfRangeException(
                nameof(value), value, $"PageSize must be between 1 and {MaxPageSize}.")
            : value;
    } = 50;

    /// <summary>
    /// <see cref="DeadLetterPage.NextCursor"/> from the previous page, or null for the first.
    /// Opaque: the encoding belongs to the library, and a cursor is portable between stores only
    /// because every store orders by the same two columns.
    /// </summary>
    public string? Cursor { get; init; }
}

/// <summary>One page of dead letters, newest first.</summary>
public sealed record DeadLetterPage
{
    public required IReadOnlyList<DeadLetter> Items { get; init; }

    /// <summary>
    /// Feed back as <see cref="DeadLetterQuery.Cursor"/> to fetch the next page. Null on the last
    /// page — so paging stops on a null cursor, not on a short page.
    /// </summary>
    public string? NextCursor { get; init; }
}
