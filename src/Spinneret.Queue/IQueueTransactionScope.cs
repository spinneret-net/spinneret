namespace Spinneret.Queue;

/// <summary>
/// Runs a group of queue operations as one unit of work where the transport can offer that. The
/// pass-through default just runs them; the SQL Server transport wraps them in a transaction its
/// <c>IQueue</c> and <c>IDeadLetterStore</c> both enlist in, which is what makes a resend's enqueue
/// and delete atomic there.
/// </summary>
/// <remarks>
/// Internal: this exists to give one caller — <see cref="DeadLetterResender"/> — the strongest
/// guarantee each transport can offer, not to invite hosts to compose queue operations. Widening it
/// later is a non-breaking change; publishing it now would be a commitment.
/// </remarks>
internal interface IQueueTransactionScope
{
    Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken ct);
}

/// <summary>
/// The default for transports with no transaction to offer: the work simply runs. Registered by
/// <c>AddQueueCore</c>; a transport that can do better replaces it.
/// </summary>
internal sealed class PassThroughQueueTransactionScope : IQueueTransactionScope
{
    public Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        return work(ct);
    }
}
