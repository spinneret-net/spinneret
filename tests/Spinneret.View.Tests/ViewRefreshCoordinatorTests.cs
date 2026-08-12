using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.View.Tests;

public class ViewRefreshCoordinatorTests
{
    // ViewRefreshCoordinator is internal; obtain the real implementation through DI.
    private static IViewRefreshCoordinator CreateCoordinator() =>
        new ServiceCollection()
            .AddMvvm<ClientRenderContext>(autoRegisterViewModels: false, typeof(ViewRefreshCoordinatorTests).Assembly)
            .BuildServiceProvider()
            .GetRequiredService<IViewRefreshCoordinator>();

    [Test]
    public async Task RequestRefreshAsync_with_no_subscribers_completes()
    {
        var coordinator = CreateCoordinator();

        await coordinator.RequestRefreshAsync();
    }

    [Test]
    public async Task RequestRefreshAsync_invokes_subscribed_handler()
    {
        var coordinator = CreateCoordinator();
        var invocations = 0;
        coordinator.Subscribe(() =>
        {
            Interlocked.Increment(ref invocations);
            return Task.CompletedTask;
        });

        await coordinator.RequestRefreshAsync();

        await Assert.That(invocations).IsEqualTo(1);
    }

    [Test]
    public async Task RequestRefreshAsync_invokes_all_subscribers()
    {
        var coordinator = CreateCoordinator();
        var invoked = new List<string>();
        coordinator.Subscribe(() =>
        {
            lock (invoked) invoked.Add("first");
            return Task.CompletedTask;
        });
        coordinator.Subscribe(() =>
        {
            lock (invoked) invoked.Add("second");
            return Task.CompletedTask;
        });

        await coordinator.RequestRefreshAsync();

        await Assert.That(invoked).Contains("first");
        await Assert.That(invoked).Contains("second");
    }

    [Test]
    public async Task RequestRefreshAsync_invokes_handler_once_per_request()
    {
        var coordinator = CreateCoordinator();
        var invocations = 0;
        coordinator.Subscribe(() =>
        {
            Interlocked.Increment(ref invocations);
            return Task.CompletedTask;
        });

        await coordinator.RequestRefreshAsync();
        await coordinator.RequestRefreshAsync();

        await Assert.That(invocations).IsEqualTo(2);
    }

    [Test]
    public async Task RequestRefreshAsync_awaits_asynchronous_handlers_to_completion()
    {
        var coordinator = CreateCoordinator();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Subscribe(() => gate.Task);

        var refreshTask = coordinator.RequestRefreshAsync();

        await Assert.That(refreshTask.IsCompleted).IsFalse();

        gate.SetResult();
        await refreshTask;
    }

    [Test]
    public async Task Subscribe_disposed_subscription_no_longer_receives_refreshes()
    {
        var coordinator = CreateCoordinator();
        var invocations = 0;
        var subscription = coordinator.Subscribe(() =>
        {
            Interlocked.Increment(ref invocations);
            return Task.CompletedTask;
        });

        subscription.Dispose();
        await coordinator.RequestRefreshAsync();

        await Assert.That(invocations).IsEqualTo(0);
    }

    [Test]
    public async Task Subscribe_disposing_one_subscription_does_not_affect_others()
    {
        var coordinator = CreateCoordinator();
        var kept = 0;
        var removed = 0;
        var subscription = coordinator.Subscribe(() =>
        {
            Interlocked.Increment(ref removed);
            return Task.CompletedTask;
        });
        coordinator.Subscribe(() =>
        {
            Interlocked.Increment(ref kept);
            return Task.CompletedTask;
        });

        subscription.Dispose();
        subscription.Dispose(); // disposing twice is safe
        await coordinator.RequestRefreshAsync();

        await Assert.That(removed).IsEqualTo(0);
        await Assert.That(kept).IsEqualTo(1);
    }

    [Test]
    public async Task RequestRefreshAsync_failing_subscriber_does_not_abort_the_others()
    {
        var coordinator = CreateCoordinator();
        var invocations = 0;
        coordinator.Subscribe(() => throw new InvalidOperationException("synchronous failure"));
        coordinator.Subscribe(() => Task.FromException(new InvalidOperationException("asynchronous failure")));
        coordinator.Subscribe(() =>
        {
            Interlocked.Increment(ref invocations);
            return Task.CompletedTask;
        });

        await coordinator.RequestRefreshAsync();

        await Assert.That(invocations).IsEqualTo(1);
    }

    [Test]
    public async Task SubscribePreRefresh_handler_completes_before_refresh_subscribers_run()
    {
        var coordinator = CreateCoordinator();
        var preRefreshCompleted = false;
        var refreshSawPreRefreshCompleted = false;
        coordinator.SubscribePreRefresh(async () =>
        {
            await Task.Delay(50);
            preRefreshCompleted = true;
        });
        coordinator.Subscribe(() =>
        {
            refreshSawPreRefreshCompleted = preRefreshCompleted;
            return Task.CompletedTask;
        });

        await coordinator.RequestRefreshAsync();

        await Assert.That(refreshSawPreRefreshCompleted).IsTrue();
    }

    [Test]
    public async Task SubscribePreRefresh_handler_can_unsubscribe_a_refresh_subscriber_before_phase_two()
    {
        // This mirrors the documented route-guard scenario: a pre-refresh handler disposes
        // a page (and thereby its refresh subscription) so it never re-initializes.
        var coordinator = CreateCoordinator();
        var invocations = 0;
        var subscription = coordinator.Subscribe(() =>
        {
            Interlocked.Increment(ref invocations);
            return Task.CompletedTask;
        });
        coordinator.SubscribePreRefresh(() =>
        {
            subscription.Dispose();
            return Task.CompletedTask;
        });

        await coordinator.RequestRefreshAsync();

        await Assert.That(invocations).IsEqualTo(0);
    }

    [Test]
    public async Task SubscribePreRefresh_disposed_subscription_no_longer_receives_requests()
    {
        var coordinator = CreateCoordinator();
        var invocations = 0;
        var subscription = coordinator.SubscribePreRefresh(() =>
        {
            Interlocked.Increment(ref invocations);
            return Task.CompletedTask;
        });

        subscription.Dispose();
        await coordinator.RequestRefreshAsync();

        await Assert.That(invocations).IsEqualTo(0);
    }
}
