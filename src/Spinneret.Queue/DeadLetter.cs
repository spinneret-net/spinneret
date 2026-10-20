namespace Spinneret.Queue;

/// <summary>A stored dead letter as read back from an <see cref="IDeadLetterStore"/>.</summary>
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

    /// <summary>When the entry was first recorded; later writes never move it.</summary>
    public required DateTimeOffset DeadLetteredAt { get; init; }
}

/// <summary>A request for one page of <see cref="IDeadLetterStore.ListAsync"/>, newest first.</summary>
public sealed record DeadLetterQuery
{
    /// <summary>The largest <see cref="PageSize"/> a store will accept.</summary>
    public const int MaxPageSize = 500;

    /// <summary>How many entries to return.</summary>
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
    /// Opaque <see cref="DeadLetterPage.NextCursor"/> from the previous page, or null for the first.
    /// </summary>
    public string? Cursor { get; init; }
}

/// <summary>One page of dead letters, newest first.</summary>
public sealed record DeadLetterPage
{
    public required IReadOnlyList<DeadLetter> Items { get; init; }

    /// <summary>
    /// Pass as <see cref="DeadLetterQuery.Cursor"/> for the next page; null on the last page.
    /// </summary>
    public string? NextCursor { get; init; }
}
