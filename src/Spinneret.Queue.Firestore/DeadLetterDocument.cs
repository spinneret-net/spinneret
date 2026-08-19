using System.Globalization;
using Google.Cloud.Firestore;

namespace Spinneret.Queue.Firestore;

/// <summary>
/// The single source of truth for the dead-letter document shape. Field names are a data contract —
/// readers (an admin page, a resend command) bind to them — so they are declared once here rather
/// than spelled inline at the write site.
/// </summary>
/// <remarks>
/// Both directions live here, and both are expressed over a plain field map rather than a
/// <see cref="DocumentSnapshot"/>, so writing and reading are pinned against each other by a
/// round-trip test without needing a live Firestore.
/// </remarks>
internal static class DeadLetterDocument
{
    internal static class Fields
    {
        public const string Source = "source";
        public const string CommandTypeName = "commandTypeName";
        public const string Description = "description";
        public const string PayloadJson = "payloadJson";
        public const string Error = "error";
        public const string Attempts = "attempts";
        public const string TraceId = "traceId";
        public const string DeadLetteredAt = "deadLetteredAt";
    }

    /// <summary>
    /// Field map for one dead letter. <paramref name="deadLetteredAt"/> is passed in rather than read
    /// from the clock here so the caller owns the time source.
    /// </summary>
    public static Dictionary<string, object?> From(DeadLetterEntry entry, DateTimeOffset deadLetteredAt) =>
        new()
        {
            // The stored spelling matches the MSSQL writer's column value, so the same reader logic
            // works whichever store a host uses. DeadLetterSource member names are a data contract.
            [Fields.Source] = DeadLetterStorage.FormatSource(entry.Source),
            [Fields.CommandTypeName] = entry.CommandTypeName,
            [Fields.Description] = entry.Description,
            [Fields.PayloadJson] = entry.PayloadJson,
            [Fields.Error] = entry.Error,
            [Fields.Attempts] = entry.Attempts,
            [Fields.TraceId] = entry.TraceId,
            [Fields.DeadLetteredAt] = Timestamp.FromDateTimeOffset(deadLetteredAt),
        };

    /// <summary>Reads one document straight off a query or lookup result.</summary>
    public static DeadLetter ToDeadLetter(DocumentSnapshot snapshot) =>
        // ToDictionary() is declared with non-null values while the map read below is declared to
        // tolerate nulls — the same type at runtime, so the annotation difference is suppressed here.
        ToDeadLetter(snapshot.Id, snapshot.ToDictionary()!);

    /// <summary>
    /// Reads a dead letter out of a document's fields. The document id carries the idempotency key —
    /// it is what the writer files the document under, so it is never duplicated into a field.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A required field is missing or holds the wrong type — a document not written by this library,
    /// or one left behind by an older shape.
    /// </exception>
    public static DeadLetter ToDeadLetter(string id, IReadOnlyDictionary<string, object?> fields) =>
        new()
        {
            IdempotencyKey = id,
            Source = DeadLetterStorage.ParseSource(ReadString(fields, Fields.Source, id)),
            CommandTypeName = ReadString(fields, Fields.CommandTypeName, id),
            Description = fields.GetValueOrDefault(Fields.Description) as string,
            PayloadJson = ReadString(fields, Fields.PayloadJson, id),
            Error = ReadString(fields, Fields.Error, id),
            Attempts = ReadInt(fields, Fields.Attempts, id),
            TraceId = fields.GetValueOrDefault(Fields.TraceId) as string,
            DeadLetteredAt = ReadTimestamp(fields, Fields.DeadLetteredAt, id),
        };

    private static string ReadString(IReadOnlyDictionary<string, object?> fields, string field, string id) =>
        fields.GetValueOrDefault(field) is string value
            ? value
            : throw Missing(field, id, "a string");

    private static int ReadInt(IReadOnlyDictionary<string, object?> fields, string field, string id) =>
        // Firestore stores every integer as a 64-bit value and hands it back as long, whatever width
        // was written — so both widths are accepted rather than only the int that From() writes.
        fields.GetValueOrDefault(field) switch
        {
            int value => value,
            long value => Convert.ToInt32(value),
            _ => throw Missing(field, id, "an integer"),
        };

    private static DateTimeOffset ReadTimestamp(
        IReadOnlyDictionary<string, object?> fields, string field, string id) =>
        fields.GetValueOrDefault(field) is Timestamp value
            ? value.ToDateTimeOffset()
            : throw Missing(field, id, "a timestamp");

    private static InvalidOperationException Missing(string field, string id, string expected) =>
        new(string.Create(
            CultureInfo.InvariantCulture,
            $"Dead-letter document '{id}' has no '{field}' field holding {expected}."));
}
