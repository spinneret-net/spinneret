namespace Spinneret.View;

/// <summary>Asks every live view to re-resolve and re-initialize its owned view model, instead of a page reload.</summary>
public interface IViewRefreshCoordinator
{
    /// <summary>Registers a handler awaited before any view re-initializes, e.g. to redirect away first.</summary>
    IDisposable SubscribePreRefresh(Func<Task> onPreRefresh);

    /// <summary>Registers a handler invoked on every refresh; dispose the result to unsubscribe.</summary>
    IDisposable Subscribe(Func<Task> onRefresh);

    /// <summary>Refreshes all subscribers; one failing does not abort the others.</summary>
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
        // Awaited first so pages redirected away from are disposed before they'd re-initialize.
        await BroadcastAsync(_preRefreshSubscriptions);

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
            // One failing subscriber must not abort the rest; views log their own failures.
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
