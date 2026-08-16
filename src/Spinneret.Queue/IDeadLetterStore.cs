namespace Spinneret.Queue;

/// <summary>
/// Reads and removes stored dead letters — the admin side of <see cref="IDeadLetterWriter"/>,
/// backing a listing page and the resend and discard actions on it.
/// </summary>
/// <remarks>
/// <para>
/// Library-implemented: each store package ships one, and applications inject it. Kept separate
/// from <see cref="IDeadLetterWriter"/> on purpose — that one is consumer-implementable (a GCP host
/// must register its own), and folding listing into it would break every out-of-tree writer. A host
/// with a custom writer and no admin page simply never registers a store.
/// </para>
/// <para>
/// Because it is library-implemented, members may be added here without shipping them as default
/// interface members.
/// </para>
/// </remarks>
public interface IDeadLetterStore
{
    /// <summary>
    /// Returns one page of entries, newest first, continuing from
    /// <see cref="DeadLetterQuery.Cursor"/> when one is supplied.
    /// </summary>
    /// <exception cref="ArgumentException">The cursor is not one this library produced.</exception>
    Task<DeadLetterPage> ListAsync(DeadLetterQuery query, CancellationToken ct = default);

    /// <summary>Returns the entry filed under <paramref name="idempotencyKey"/>, or null.</summary>
    Task<DeadLetter?> GetAsync(string idempotencyKey, CancellationToken ct = default);

    /// <summary>
    /// Removes the entry, discarding the work it holds. Returns false when nothing was filed under
    /// the key — deleting an entry a colleague already handled is a no-op, not an error.
    /// </summary>
    Task<bool> DeleteAsync(string idempotencyKey, CancellationToken ct = default);
}
