namespace Spinneret.Queue;

/// <summary>
/// Runs a group of queue operations as one unit of work where the storage can offer that: a
/// delivery (claim, handler writes, bookings, dead letter, acknowledge), or a resend (enqueue plus
/// delete). The pass-through default just runs the work; SQL storage opens a transaction that every
/// queue and dead-letter operation in the flow enlists in.
/// </summary>
/// <remarks>
/// <para>
/// Callback-shaped on purpose. SQL storage publishes its transaction to the async flow through an
/// <c>AsyncLocal</c>, and a value set inside an awaited <c>BeginAsync()</c> would not flow back out
/// to the caller — only work running inside the call can see it.
/// </para>
/// <para>
/// Internal: the seam storage and delivery packages meet at, not something hosts compose with.
/// </para>
/// </remarks>
internal interface IQueueUnitOfWorkFactory
{
    /// <summary>
    /// Runs <paramref name="work"/> inside a unit of work. Nothing it did is kept unless it calls
    /// <see cref="IQueueUnitOfWork.CommitAsync"/>; returning or throwing without committing rolls
    /// the unit back. When the flow is already inside a unit — a caller's own transaction — the work
    /// joins it instead, and committing is left to that caller.
    /// </summary>
    Task<T> ExecuteAsync<T>(Func<IQueueUnitOfWork, CancellationToken, Task<T>> work, CancellationToken ct);
}

/// <summary>One unit of work handed to the callback of <see cref="IQueueUnitOfWorkFactory.ExecuteAsync{T}"/>.</summary>
internal interface IQueueUnitOfWork
{
    /// <summary>
    /// Makes the work so far durable. A no-op for a unit that joined a caller's transaction, and for
    /// storage with no transaction to offer.
    /// </summary>
    Task CommitAsync(CancellationToken ct);
}

internal static class QueueUnitOfWorkFactoryExtensions
{
    /// <summary>Runs <paramref name="work"/> in a unit of work and commits it when the work completes.</summary>
    public static Task ExecuteAndCommitAsync(
        this IQueueUnitOfWorkFactory factory, Func<CancellationToken, Task> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        return factory.ExecuteAsync<object?>(async (unit, token) =>
        {
            await work(token);
            await unit.CommitAsync(token);
            return null;
        }, ct);
    }
}

/// <summary>
/// The default for storage with no transaction to offer — Cloud Tasks with no SQL store: the work
/// simply runs, and every write it makes is durable as soon as it is made.
/// </summary>
internal sealed class PassThroughQueueUnitOfWorkFactory : IQueueUnitOfWorkFactory
{
    public Task<T> ExecuteAsync<T>(Func<IQueueUnitOfWork, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        return work(PassThroughUnit.Instance, ct);
    }

    private sealed class PassThroughUnit : IQueueUnitOfWork
    {
        public static readonly PassThroughUnit Instance = new();

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
