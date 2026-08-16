using Google.Cloud.Firestore;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace Spinneret.Queue.Firestore;

/// <summary>
/// Reads back what <see cref="FirestoreDeadLetterWriter"/> stored, over the same
/// <see cref="DeadLetterDocument"/> field names, so the two can never drift.
/// </summary>
internal sealed class FirestoreDeadLetterStore(
    FirestoreDb db,
    IOptions<FirestoreDeadLetterOptions> options)
    : IDeadLetterStore
{
    private CollectionReference Collection => db.Collection(options.Value.Collection);

    public async Task<DeadLetterPage> ListAsync(DeadLetterQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Ordering by a field and then by document id in the same direction is what Firestore does
        // implicitly anyway, and it is served by the automatic single-field index — so hosts create
        // no composite index for this page. Writing it out makes the second cursor value explicit.
        // Adding a where-filter here would change that, and would need an index per filtered field.
        var firestoreQuery = Collection
            .OrderByDescending(DeadLetterDocument.Fields.DeadLetteredAt)
            .OrderByDescending(FieldPath.DocumentId);

        if (query.Cursor is { } cursor)
        {
            var position = DeadLetterCursor.Decode(cursor);
            firestoreQuery = firestoreQuery.StartAfter(
                Timestamp.FromDateTimeOffset(position.DeadLetteredAt), position.IdempotencyKey);
        }

        // One more than asked for: whether that extra row came back is what distinguishes a full
        // last page from a full page with more behind it, without a second count query.
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
            // MustExist rather than an unconditional delete, which Firestore treats as success on a
            // document that was never there — the caller could not then tell "discarded" from
            // "someone else got there first".
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
