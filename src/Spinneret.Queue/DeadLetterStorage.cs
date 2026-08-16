using System.Buffers.Text;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace Spinneret.Queue;

/// <summary>
/// The parts of the dead-letter storage contract every store package shares. Kept here rather than
/// duplicated per provider so a Firestore document and a SQL Server row can never disagree about
/// how a source is spelled or how a cursor is encoded.
/// </summary>
internal static class DeadLetterStorage
{
    /// <summary>
    /// Built from <see cref="FormatSource"/> so the two directions cannot drift, and consulted
    /// instead of <c>Enum.TryParse</c>, which would also accept the underlying numbers — "0" is not
    /// a spelling this library ever writes, so it is not one it should read.
    /// </summary>
    private static readonly FrozenDictionary<string, DeadLetterSource> SourcesByName =
        Enum.GetValues<DeadLetterSource>()
            .ToFrozenDictionary(FormatSource, source => source, StringComparer.Ordinal);

    /// <summary>
    /// How <see cref="DeadLetterSource"/> is persisted — the member name, in both stores. A data
    /// contract: existing rows and documents already carry these strings.
    /// </summary>
    public static string FormatSource(DeadLetterSource source) => source.ToString();

    /// <summary>Reads back a value written by <see cref="FormatSource"/>.</summary>
    /// <exception cref="InvalidOperationException">
    /// The stored value names no known source — the store was written by a newer library version,
    /// or edited by hand.
    /// </exception>
    public static DeadLetterSource ParseSource(string stored) =>
        SourcesByName.TryGetValue(stored, out var source)
            ? source
            : throw new InvalidOperationException(
                $"Dead-letter entry has an unrecognized source '{stored}'. " +
                $"Expected one of: {string.Join(", ", SourcesByName.Keys)}.");
}

/// <summary>
/// The position a page of dead letters resumes from: the sort key of the last row returned. Every
/// store orders by (DeadLetteredAt descending, IdempotencyKey descending), so keyset paging is
/// stable while entries are being added and deleted underneath the reader — which an offset would
/// not be, on a page whose whole purpose is deleting rows.
/// </summary>
/// <remarks>
/// Rendered as an opaque base64url string so applications pass it through a query string without
/// reading meaning into it, leaving the encoding free to change.
/// </remarks>
internal readonly record struct DeadLetterCursor(DateTimeOffset DeadLetteredAt, string IdempotencyKey)
{
    public string Encode()
    {
        // Ticks rather than a formatted date: exact, culture-free, and never contains the separator.
        var payload = string.Create(
            CultureInfo.InvariantCulture, $"{DeadLetteredAt.UtcTicks}:{IdempotencyKey}");
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));
    }

    /// <exception cref="ArgumentException">Not a cursor this library produced.</exception>
    public static DeadLetterCursor Decode(string cursor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor);

        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
        }
        catch (FormatException ex)
        {
            throw Invalid(cursor, ex);
        }

        // Split on the first separator only: an idempotency key may itself contain colons.
        var separator = payload.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == payload.Length - 1)
            throw Invalid(cursor, inner: null);

        if (!long.TryParse(
                payload.AsSpan(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTimeOffset.MinValue.UtcTicks
            || ticks > DateTimeOffset.MaxValue.UtcTicks)
            throw Invalid(cursor, inner: null);

        return new DeadLetterCursor(
            new DateTimeOffset(ticks, TimeSpan.Zero), payload[(separator + 1)..]);
    }

    private static ArgumentException Invalid(string cursor, Exception? inner) =>
        new($"'{cursor}' is not a valid dead-letter cursor. Pass back a " +
            $"{nameof(DeadLetterPage)}.{nameof(DeadLetterPage.NextCursor)} value, or null for the first page.",
            nameof(cursor), inner);
}
