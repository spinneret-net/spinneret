using Google.Cloud.Firestore;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace Spinneret.Queue.Firestore;

// Not atomic with the delivery (no shared transaction), so the write must stay idempotent: it is retried on failure.
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
            // Create, not Set: first write wins, so redeliveries don't move deadLetteredAt.
            await document.CreateAsync(fields, ct);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
            // Already dead-lettered; the first entry stands.
        }
    }
}
