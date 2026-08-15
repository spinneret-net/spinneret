using Google.Cloud.Firestore;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace Spinneret.Queue.Firestore;

/// <summary>
/// Dead letters as documents in Firestore, one per <see cref="DeadLetterEntry.IdempotencyKey"/>.
/// The key is the document id, so the transport's own redelivery of a task whose dead-letter write
/// already landed cannot produce a second entry.
/// </summary>
/// <remarks>
/// Unlike the MSSQL writer there is no ambient transaction to join — Firestore and the queue
/// transport are separate systems — so a dead letter is never atomic with the delivery that
/// produced it. The delivery processor accounts for that: a failed write is retried rather than
/// acknowledged, which is why the write must stay idempotent.
/// </remarks>
internal sealed class FirestoreDeadLetterWriter(
    FirestoreDb db,
    IOptions<FirestoreDeadLetterOptions> options,
    TimeProvider timeProvider)
    : IDeadLetterWriter
{
    public async Task WriteAsync(DeadLetterEntry entry, CancellationToken ct = default)
    {
        var document = db.Collection(options.Value.Collection).Document(entry.IdempotencyKey);
        var fields = DeadLetterDocument.From(entry, timeProvider.GetUtcNow());

        try
        {
            // Create, not Set: the first write wins, exactly as the MSSQL writer's insert swallows a
            // duplicate key. A Set would overwrite the original entry on every redelivery, moving
            // deadLetteredAt forward and losing when the failure actually happened.
            await document.CreateAsync(fields, ct);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
            // This task was already dead-lettered; the existing entry is the authoritative one.
        }
    }
}
