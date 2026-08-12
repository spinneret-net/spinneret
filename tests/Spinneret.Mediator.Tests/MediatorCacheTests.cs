using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.Mediator.Tests;

public class MediatorCacheTests
{
    [Test]
    public async Task Send_repeated_cached_request_invokes_handler_once()
    {
        var handler = new CountingHandler();
        var mediator = TestServices.BuildMediator(handler);

        var first = await mediator.Send(new CachedQuery(2));
        var second = await mediator.Send(new CachedQuery(2));

        await Assert.That(first).IsEqualTo(2);
        await Assert.That(second).IsEqualTo(2);
        await Assert.That(handler.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task Send_different_request_values_get_separate_cache_entries()
    {
        var handler = new CountingHandler();
        var mediator = TestServices.BuildMediator(handler);

        var first = await mediator.Send(new CachedQuery(1));
        var second = await mediator.Send(new CachedQuery(2));

        await Assert.That(first).IsEqualTo(1);
        await Assert.That(second).IsEqualTo(2);
        await Assert.That(handler.CallCount).IsEqualTo(2);
    }

    [Test]
    public async Task Send_requests_of_different_types_do_not_share_cache_entries()
    {
        var handler = new CountingHandler();
        var mediator = TestServices.BuildMediator(handler);

        await mediator.Send(new CachedQuery(1));
        await mediator.Send(new MultiTagQuery(1));

        await Assert.That(handler.CallCount).IsEqualTo(2);
    }

    [Test]
    public async Task Send_cache_key_uses_reference_equality_for_non_record_requests()
    {
        var handler = new ReferenceKeyHandler();
        var mediator = TestServices.BuildMediator(services =>
            services.AddSingleton<IRequestHandler<ReferenceKeyQuery, int>>(handler));

        var sameInstance = new ReferenceKeyQuery();
        await mediator.Send(sameInstance);
        await mediator.Send(sameInstance);

        await Assert.That(handler.CallCount).IsEqualTo(1);

        await mediator.Send(new ReferenceKeyQuery());

        await Assert.That(handler.CallCount).IsEqualTo(2);
    }

    [Test]
    public async Task Send_concurrent_callers_share_the_same_in_flight_task()
    {
        var handler = new CountingHandler();
        var mediator = TestServices.BuildMediator(handler);
        var gate = new TaskCompletionSource<int>();
        handler.SetPending(gate);

        var t1 = mediator.Send(new CachedQuery(1));
        var t2 = mediator.Send(new CachedQuery(1));
        var t3 = mediator.Send(new CachedQuery(1));

        await Assert.That(handler.CallCount).IsEqualTo(1);

        gate.SetResult(99);
        var results = await Task.WhenAll(t1, t2, t3);

        await Assert.That(results).IsEquivalentTo([99, 99, 99]);
        await Assert.That(handler.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task Send_many_parallel_callers_invoke_handler_once_and_get_same_result()
    {
        var handler = new CountingHandler();
        var mediator = TestServices.BuildMediator(handler);

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => mediator.Send(new CachedQuery(7))))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        await Assert.That(results.Distinct()).IsEquivalentTo([7]);
        await Assert.That(handler.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task Send_failed_request_is_not_cached_so_next_caller_retries()
    {
        var handler = new CountingHandler();
        var mediator = TestServices.BuildMediator(handler);
        handler.ThrowOnNextCall = true;

        await Assert.That(async () => await mediator.Send(new CachedQuery(3)))
            .Throws<InvalidOperationException>();

        var result = await mediator.Send(new CachedQuery(3));

        await Assert.That(result).IsEqualTo(3);
        await Assert.That(handler.CallCount).IsEqualTo(2);
    }

    [Test]
    public async Task Send_caller_cancellation_does_not_abort_shared_task()
    {
        var handler = new CountingHandler();
        var mediator = TestServices.BuildMediator(handler);
        var gate = new TaskCompletionSource<int>();
        handler.SetPending(gate);

        using var cts = new CancellationTokenSource();
        var cancellable = mediator.Send(new CachedQuery(4), cts.Token);
        var noncancellable = mediator.Send(new CachedQuery(4));

        cts.Cancel();

        OperationCanceledException? caught = null;
        try { await cancellable; }
        catch (OperationCanceledException ex) { caught = ex; }
        await Assert.That(caught).IsNotNull();

        gate.SetResult(77);
        var result = await noncancellable;

        await Assert.That(result).IsEqualTo(77);
        await Assert.That(handler.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task Send_handler_for_cached_request_receives_none_token()
    {
        var mediator = TestServices.BuildMediator();
        using var cts = new CancellationTokenSource();

        var receivedToken = await mediator.Send(new CachedTokenEchoQuery(), cts.Token);

        await Assert.That(receivedToken).IsEqualTo(CancellationToken.None);
    }

    [Test]
    public async Task Send_cache_attribute_on_unit_request_does_not_cache()
    {
        var handler = new CachedUnitCommandHandler();
        var mediator = TestServices.BuildMediator(services =>
            services.AddSingleton<IRequestHandler<CachedUnitCommand, Unit>>(handler));

        await mediator.Send(new CachedUnitCommand());
        await mediator.Send(new CachedUnitCommand());

        await Assert.That(handler.CallCount).IsEqualTo(2);
    }

    [Test]
    public async Task Send_cache_entry_expires_after_duration()
    {
        var handler = new CountingHandler();
        var mediator = TestServices.BuildMediator(handler);

        await mediator.Send(new ShortLivedQuery(8));
        await mediator.Send(new ShortLivedQuery(8));
        await Assert.That(handler.CallCount).IsEqualTo(1);

        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var result = await mediator.Send(new ShortLivedQuery(8));

        await Assert.That(result).IsEqualTo(8);
        await Assert.That(handler.CallCount).IsEqualTo(2);
    }

    [Test]
    public async Task ClearCache_forces_refetch_on_next_send()
    {
        var handler = new CountingHandler();
        var mediator = TestServices.BuildMediator(handler);

        await mediator.Send(new CachedQuery(5));
        await mediator.Send(new CachedQuery(5));
        await Assert.That(handler.CallCount).IsEqualTo(1);

        mediator.ClearCache();
        var result = await mediator.Send(new CachedQuery(5));

        await Assert.That(result).IsEqualTo(5);
        await Assert.That(handler.CallCount).IsEqualTo(2);
    }

    [Test]
    public async Task ClearCache_during_in_flight_request_does_not_evict_replacement_task()
    {
        var handler = new CountingHandler();
        var mediator = TestServices.BuildMediator(handler);

        var firstGate = new TaskCompletionSource<int>();
        handler.SetPending(firstGate);

        var firstCall = mediator.Send(new CachedQuery(6));
        mediator.ClearCache();
        firstGate.SetException(new InvalidOperationException("boom"));

        await Assert.That(async () => await firstCall).Throws<InvalidOperationException>();

        var secondGate = new TaskCompletionSource<int>();
        handler.SetPending(secondGate);
        var secondCall = mediator.Send(new CachedQuery(6));
        var joiner = mediator.Send(new CachedQuery(6));

        secondGate.SetResult(88);
        var second = await secondCall;
        var joined = await joiner;

        await Assert.That(second).IsEqualTo(88);
        await Assert.That(joined).IsEqualTo(88);
        await Assert.That(handler.CallCount).IsEqualTo(2);
    }
}
