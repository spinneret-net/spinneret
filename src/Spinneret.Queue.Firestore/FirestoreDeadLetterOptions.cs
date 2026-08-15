namespace Spinneret.Queue.Firestore;

public sealed class FirestoreDeadLetterOptions
{
    public static readonly string SectionName = "Queue:Firestore";

    /// <summary>
    /// Firestore collection dead letters are written to. Entries are keyed by
    /// <see cref="DeadLetterEntry.IdempotencyKey"/> as the document id, so a redelivered write
    /// lands on the document it already wrote rather than creating a duplicate.
    /// </summary>
    public string Collection { get; set; } = "dead_letters";
}
