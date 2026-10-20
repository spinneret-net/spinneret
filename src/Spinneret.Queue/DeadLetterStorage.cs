using System.Buffers.Text;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace Spinneret.Queue;

// Shared by every store package so providers never disagree on source spelling or cursor encoding.
internal static class DeadLetterStorage
{
    // Not Enum.TryParse: that would also accept numeric strings, which this library never writes.
    private static readonly FrozenDictionary<string, DeadLetterSource> SourcesByName =
        Enum.GetValues<DeadLetterSource>()
            .ToFrozenDictionary(FormatSource, source => source, StringComparer.Ordinal);

    // Data contract: existing rows and documents carry these strings.
    public static string FormatSource(DeadLetterSource source) => source.ToString();

    public static DeadLetterSource ParseSource(string stored) =>
        SourcesByName.TryGetValue(stored, out var source)
            ? source
            : throw new InvalidOperationException(
                $"Dead-letter entry has an unrecognized source '{stored}'. " +
                $"Expected one of: {string.Join(", ", SourcesByName.Keys)}.");
}

// Keyset cursor over (DeadLetteredAt desc, IdempotencyKey desc): stable while rows are deleted, unlike an offset.
internal readonly record struct DeadLetterCursor(DateTimeOffset DeadLetteredAt, string IdempotencyKey)
{
    public string Encode()
    {
        // Ticks: exact, culture-free, and never contain the separator.
        var payload = string.Create(
            CultureInfo.InvariantCulture, $"{DeadLetteredAt.UtcTicks}:{IdempotencyKey}");
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));
    }

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

        // First separator only: an idempotency key may itself contain colons.
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
