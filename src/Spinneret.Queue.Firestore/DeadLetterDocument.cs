using Google.Cloud.Firestore;

namespace Spinneret.Queue.Firestore;

/// <summary>
/// The single source of truth for the dead-letter document shape. Field names are a data contract —
/// readers (an admin page, a resend command) bind to them — so they are declared once here rather
/// than spelled inline at the write site.
/// </summary>
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
        public const string DeadLetteredAt = "deadLetteredAt";
    }

    /// <summary>
    /// Field map for one dead letter. <paramref name="deadLetteredAt"/> is passed in rather than read
    /// from the clock here so the caller owns the time source.
    /// </summary>
    public static Dictionary<string, object?> From(DeadLetterEntry entry, DateTimeOffset deadLetteredAt) =>
        new()
        {
            // Source.ToString() matches the MSSQL writer's column value, so the same reader logic
            // works whichever store a host uses. DeadLetterSource member names are a data contract.
            [Fields.Source] = entry.Source.ToString(),
            [Fields.CommandTypeName] = entry.CommandTypeName,
            [Fields.Description] = entry.Description,
            [Fields.PayloadJson] = entry.PayloadJson,
            [Fields.Error] = entry.Error,
            [Fields.Attempts] = entry.Attempts,
            [Fields.DeadLetteredAt] = Timestamp.FromDateTimeOffset(deadLetteredAt),
        };
}
