namespace Spinneret.Queue;

/// <summary>Lists, reads and deletes stored dead letters, for an admin page.</summary>
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

    /// <summary>Removes the entry; returns false when nothing was filed under the key.</summary>
    Task<bool> DeleteAsync(string idempotencyKey, CancellationToken ct = default);
}
