namespace Spinneret.Queue;

// Callback-shaped because SQL storage flows its transaction via AsyncLocal,
// which would not flow back out of an awaited BeginAsync().
internal interface IQueueUnitOfWorkFactory
{
    // Rolls back unless the work commits; joins an ambient caller transaction if there is one.
    Task<T> ExecuteAsync<T>(Func<IQueueUnitOfWork, CancellationToken, Task<T>> work, CancellationToken ct);
}

internal interface IQueueUnitOfWork
{
    // No-op when joined to a caller's transaction or when storage has none.
    Task CommitAsync(CancellationToken ct);
}

internal static class QueueUnitOfWorkFactoryExtensions
{
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
