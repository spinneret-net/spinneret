using Google.Cloud.Firestore;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace Spinneret.Queue.Firestore;

internal sealed class FirestoreDeadLetterStore(
    FirestoreDb db,
    IOptions<FirestoreDeadLetterOptions> options)
    : IDeadLetterStore
{
    private CollectionReference Collection => db.Collection(options.Value.Collection);

    public async Task<DeadLetterPage> ListAsync(DeadLetterQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Field then document id, same direction: served by the automatic index, no composite index needed.
        // A where-filter would need one.
        var firestoreQuery = Collection
            .OrderByDescending(DeadLetterDocument.Fields.DeadLetteredAt)
            .OrderByDescending(FieldPath.DocumentId);

        if (query.Cursor is { } cursor)
        {
            var position = DeadLetterCursor.Decode(cursor);
            firestoreQuery = firestoreQuery.StartAfter(
                Timestamp.FromDateTimeOffset(position.DeadLetteredAt), position.IdempotencyKey);
        }

        // One extra row tells whether another page follows.
        var snapshot = await firestoreQuery.Limit(query.PageSize + 1).GetSnapshotAsync(ct);

        var hasMore = snapshot.Documents.Count > query.PageSize;
        var items = snapshot.Documents
            .Take(query.PageSize)
            .Select(DeadLetterDocument.ToDeadLetter)
            .ToArray();

        return new DeadLetterPage
        {
            Items = items,
            NextCursor = hasMore
                ? new DeadLetterCursor(items[^1].DeadLetteredAt, items[^1].IdempotencyKey).Encode()
                : null,
        };
    }

    public async Task<DeadLetter?> GetAsync(string idempotencyKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var snapshot = await Collection.Document(idempotencyKey).GetSnapshotAsync(ct);
        return snapshot.Exists ? DeadLetterDocument.ToDeadLetter(snapshot) : null;
    }

    public async Task<bool> DeleteAsync(string idempotencyKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        try
        {
            // MustExist: a plain delete succeeds on a missing document, hiding "already gone".
            await Collection.Document(idempotencyKey).DeleteAsync(Precondition.MustExist, ct);
            return true;
        }
        catch (RpcException ex)
            when (ex.StatusCode is StatusCode.NotFound or StatusCode.FailedPrecondition)
        {
            return false;
        }
    }
}
