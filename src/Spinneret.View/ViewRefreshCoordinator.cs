namespace Spinneret.View;

/// <summary>
/// Broadcasts a request for every live view to re-initialize its owned view model
/// (re-resolved from DI) in place, as a smoother alternative to a full page reload.
/// </summary>
public interface IViewRefreshCoordinator
{
    /// <summary>
    /// Registers a handler invoked as a distinct first phase, before any view re-initializes and
    /// awaited to completion. Used by the route guard to redirect away from a page the user may
    /// no longer access, so that page is disposed before the main refresh would re-initialize it.
    /// </summary>
    IDisposable SubscribePreRefresh(Func<Task> onPreRefresh);

    /// <summary>
    /// Registers a handler invoked on every refresh request. Dispose the returned
    /// subscription to stop receiving requests (views do this when they are disposed).
    /// </summary>
    IDisposable Subscribe(Func<Task> onRefresh);

    /// <summary>
    /// Asks all subscribed views to refresh. The returned task completes once every
    /// subscriber has finished; a single subscriber failing does not abort the others.
    /// </summary>
    Task RequestRefreshAsync();
}

internal sealed class ViewRefreshCoordinator : IViewRefreshCoordinator
{
    private readonly HashSet<Subscription> _preRefreshSubscriptions = [];
    private readonly HashSet<Subscription> _subscriptions = [];
    private readonly object _gate = new();

    public IDisposable SubscribePreRefresh(Func<Task> onPreRefresh) => Add(_preRefreshSubscriptions, onPreRefresh);

    public IDisposable Subscribe(Func<Task> onRefresh) => Add(_subscriptions, onRefresh);

    private IDisposable Add(HashSet<Subscription> set, Func<Task> handler)
    {
        var subscription = new Subscription(this, set, handler);
        lock (_gate)
        {
            set.Add(subscription);
        }

        return subscription;
    }

    public async Task RequestRefreshAsync()
    {
        // Phase 1 (awaited to completion): route revalidation. The route guard redirects away
        // from a page the user may no longer access here, so that page is disposed — and thus
        // unsubscribed from phase 2 below — before it would otherwise re-initialize and issue a
        // request that the redirect immediately cancels.
        await BroadcastAsync(_preRefreshSubscriptions);

        // Phase 2: re-initialize the views that are still live.
        await BroadcastAsync(_subscriptions);
    }

    private Task BroadcastAsync(HashSet<Subscription> subscriptions)
    {
        Subscription[] snapshot;
        lock (_gate)
        {
            snapshot = subscriptions.ToArray();
        }

        return Task.WhenAll(snapshot.Select(InvokeSafely));
    }

    private static async Task InvokeSafely(Subscription subscription)
    {
        try
        {
            await subscription.Handler();
        }
        catch
        {
            // A subscriber torn down mid-broadcast (e.g. disposed between snapshot and
            // invocation) must not abort the refresh for the rest. Views log their own failures.
        }
    }

    private void Remove(HashSet<Subscription> set, Subscription subscription)
    {
        lock (_gate)
        {
            set.Remove(subscription);
        }
    }

    private sealed class Subscription(
        ViewRefreshCoordinator owner,
        HashSet<Subscription> set,
        Func<Task> handler) : IDisposable
    {
        public Func<Task> Handler { get; } = handler;

        public void Dispose() => owner.Remove(set, this);
    }
}
