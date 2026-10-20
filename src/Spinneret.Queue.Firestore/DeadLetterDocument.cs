using System.Globalization;
using Google.Cloud.Firestore;

namespace Spinneret.Queue.Firestore;

// Field names are a data contract shared by writer and readers. Works over plain field maps so
// the round trip is testable without Firestore.
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

    public static Dictionary<string, object?> From(DeadLetterEntry entry, DateTimeOffset deadLetteredAt) =>
        new()
        {
            [Fields.Source] = DeadLetterStorage.FormatSource(entry.Source),
            [Fields.CommandTypeName] = entry.CommandTypeName,
            [Fields.Description] = entry.Description,
            [Fields.PayloadJson] = entry.PayloadJson,
            [Fields.Error] = entry.Error,
            [Fields.Attempts] = entry.Attempts,
            [Fields.TraceId] = entry.TraceId,
            [Fields.DeadLetteredAt] = Timestamp.FromDateTimeOffset(deadLetteredAt),
        };

    public static DeadLetter ToDeadLetter(DocumentSnapshot snapshot) =>
        // Same type at runtime; only the nullability annotations differ.
        ToDeadLetter(snapshot.Id, snapshot.ToDictionary()!);

    // The document id is the idempotency key; it is never duplicated into a field.
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
        // Firestore returns every integer as long, whatever width was written.
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
