namespace Spinneret.Queue.Firestore;

public sealed class FirestoreDeadLetterOptions
{
    public static readonly string SectionName = "Queue:Firestore";

    /// <summary>Collection dead letters are written to, keyed by <see cref="DeadLetterEntry.IdempotencyKey"/>.</summary>
    public string Collection { get; set; } = "dead_letters";
}
