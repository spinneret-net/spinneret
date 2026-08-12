using System.ComponentModel;
using Spinneret.ViewModel;

namespace Spinneret.View.Tests;

/// <summary>
/// Hand-rolled <see cref="IViewModel"/> that records every lifecycle interaction so tests can
/// observe initialization, updates, event subscription and disposal.
/// </summary>
public sealed class FakeViewModel : IViewModel, IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<string> _updatedProperties = [];
    private int _initializeCallCount;
    private int _updateCallCount;
    private int _disposeCallCount;
    private int _asyncDisposeCallCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>When set, <see cref="InitializeAsync"/> throws after registering the call.</summary>
    public bool ThrowOnInitialize { get; set; }

    /// <summary>When set, <see cref="InitializeAsync"/> does not complete until the gate is released.</summary>
    public TaskCompletionSource? InitializeGate { get; set; }

    public int InitializeCallCount => Volatile.Read(ref _initializeCallCount);

    public int UpdateCallCount
    {
        get { lock (_gate) return _updateCallCount; }
    }

    public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

    public int AsyncDisposeCallCount => Volatile.Read(ref _asyncDisposeCallCount);

    /// <summary>All property names delivered to <see cref="UpdateAsync"/> so far, in order.</summary>
    public IReadOnlyList<string> UpdatedProperties
    {
        get { lock (_gate) return _updatedProperties.ToArray(); }
    }

    public int PropertyChangedHandlerCount => PropertyChanged?.GetInvocationList().Length ?? 0;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _initializeCallCount);

        if (InitializeGate != null)
        {
            await InitializeGate.Task;
        }

        if (ThrowOnInitialize)
        {
            throw new InvalidOperationException("Initialization failed");
        }
    }

    public Task UpdateAsync(ICollection<string> changedProperties)
    {
        lock (_gate)
        {
            _updateCallCount++;
            _updatedProperties.AddRange(changedProperties);
        }

        return Task.CompletedTask;
    }

    public void Raise(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose() => Interlocked.Increment(ref _disposeCallCount);

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _asyncDisposeCallCount);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A <see cref="ViewModelBase"/>-derived view model, used to observe behavior that
/// <see cref="ViewBase{T}"/> applies only to that base class (exception service wiring).
/// </summary>
public sealed class FakeViewModelBase : ViewModelBase;

public sealed class FakeExceptionService : IViewModelExceptionService
{
    public bool Handle(IViewModel vm, Exception e) => false;
}

public sealed class FakeRenderContext : IRenderContext
{
    public bool IsClient { get; init; } = true;
    public bool IsServer { get; init; }
    public bool IsPrerendering { get; init; }
}

/// <summary>
/// Hand-rolled <see cref="IViewRefreshCoordinator"/> that records subscriptions and lets a test
/// broadcast refreshes and inspect or replay the registered handlers.
/// </summary>
public sealed class FakeRefreshCoordinator : IViewRefreshCoordinator
{
    private readonly object _gate = new();
    private readonly List<Func<Task>> _refreshHandlers = [];
    private readonly List<Func<Task>> _preRefreshHandlers = [];
    private int _subscribeCallCount;

    /// <summary>Total number of <see cref="Subscribe"/> calls ever made.</summary>
    public int SubscribeCallCount => Volatile.Read(ref _subscribeCallCount);

    /// <summary>Number of refresh subscriptions that have not been disposed.</summary>
    public int ActiveRefreshSubscriptionCount
    {
        get { lock (_gate) return _refreshHandlers.Count; }
    }

    public IDisposable Subscribe(Func<Task> onRefresh)
    {
        lock (_gate)
        {
            _refreshHandlers.Add(onRefresh);
            _subscribeCallCount++;
        }

        return new Subscription(() =>
        {
            lock (_gate) _refreshHandlers.Remove(onRefresh);
        });
    }

    public IDisposable SubscribePreRefresh(Func<Task> onPreRefresh)
    {
        lock (_gate) _preRefreshHandlers.Add(onPreRefresh);

        return new Subscription(() =>
        {
            lock (_gate) _preRefreshHandlers.Remove(onPreRefresh);
        });
    }

    public Func<Task>[] SnapshotRefreshHandlers()
    {
        lock (_gate) return _refreshHandlers.ToArray();
    }

    public Task RequestRefreshAsync() =>
        Task.WhenAll(SnapshotRefreshHandlers().Select(handler => handler()));

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

public static class TestWait
{
    /// <summary>Polls until <paramref name="condition"/> is true or the timeout elapses.</summary>
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs)
            {
                throw new TimeoutException($"Condition not met within {timeoutMs} ms.");
            }

            await Task.Delay(10);
        }
    }
}
