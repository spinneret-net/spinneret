namespace Spinneret.Mediator.Tests;

/// <summary>
/// Tag-based invalidation behavior of the internal TagIndexedCache, exercised
/// through the public mediator API (CacheAttribute + InvalidateCacheAttribute).
/// </summary>
public class TagIndexedCacheTests
{
    [Test]
    public async Task Send_invalidating_command_drops_cached_entry_with_matching_tag()
    {
        var handler = new CountingHandler();
        var mediator = TestServices.BuildMediator(handler);

        await mediator.Send(new CachedQuery(5));
        await mediator.Send(new CachedQuery(5));
        await Assert.That(handler.CallCount).IsEqualTo(1);

        await mediator.Send(new InvalidateAlphaCommand());
        var third = await mediator.Send(new CachedQuery(5));

        await Assert.That(third).IsEqualTo(5);
        await Assert.That(handler.CallCount).IsEqualTo(2);
    }

    [Test]
    public async Task Send_invalidating_command_keeps_entries_with_other_tags()
    {
        var handler = new CountingHandler();
        var mediator = TestServices.BuildMediator(handler);

        await mediator.Send(new CachedQuery(1));      // tagged Alpha
        await mediator.Send(new BetaTaggedQuery(2));  // tagged Beta
        await Assert.That(handler.CallCount).IsEqualTo(2);

        await mediator.Send(new InvalidateAlphaCommand());

        await mediator.Send(new BetaTaggedQuery(2));
        await Assert.That(handler.CallCount).IsEqualTo(2);

        await mediator.Send(new CachedQuery(1));
        await Assert.That(handler.CallCount).IsEqualTo(3);
    }

    [Test]
    public async Task Send_invalidation_by_any_one_tag_drops_multi_tag_entry()
    {
        var handler = new CountingHandler();
        var mediator = TestServices.BuildMediator(handler);

        await mediator.Send(new MultiTagQuery(9)); // tagged Alpha, Beta, Gamma
        await mediator.Send(new MultiTagQuery(9));
        await Assert.That(handler.CallCount).IsEqualTo(1);

        await mediator.Send(new InvalidateGammaCommand());
        await mediator.Send(new MultiTagQuery(9));

        await Assert.That(handler.CallCount).IsEqualTo(2);
    }

    [Test]
    public async Task Send_repeated_invalidation_of_multi_tag_entry_refetches_each_cycle()
    {
        var handler = new CountingHandler();
        var mediator = TestServices.BuildMediator(handler);

        await mediator.Send(new MultiTagQuery(9));
        await Assert.That(handler.CallCount).IsEqualTo(1);

        await mediator.Send(new InvalidateAlphaCommand());
        await mediator.Send(new MultiTagQuery(9));
        await Assert.That(handler.CallCount).IsEqualTo(2);

        await mediator.Send(new InvalidateAlphaCommand());
        await mediator.Send(new MultiTagQuery(9));
        await Assert.That(handler.CallCount).IsEqualTo(3);
    }

    [Test]
    public async Task Send_invalidate_and_refetch_returns_latest_data_each_cycle()
    {
        var handler = new CountingHandler();
        var mediator = TestServices.BuildMediator(handler);

        handler.OverrideResult = 100;
        await Assert.That(await mediator.Send(new MultiTagQuery(42))).IsEqualTo(100);

        handler.OverrideResult = 200;
        await mediator.Send(new InvalidateAlphaCommand());
        await Assert.That(await mediator.Send(new MultiTagQuery(42))).IsEqualTo(200);

        handler.OverrideResult = 300;
        await mediator.Send(new InvalidateAlphaCommand());
        await Assert.That(await mediator.Send(new MultiTagQuery(42))).IsEqualTo(300);

        await Assert.That(handler.CallCount).IsEqualTo(3);
    }

    [Test]
    public async Task Send_invalidating_request_with_response_returns_response_and_invalidates()
    {
        var handler = new CountingHandler();
        var mediator = TestServices.BuildMediator(handler);

        await mediator.Send(new CachedQuery(5));
        await Assert.That(handler.CallCount).IsEqualTo(1);

        var response = await mediator.Send(new InvalidateAlphaQuery());
        await Assert.That(response).IsEqualTo(123);

        await mediator.Send(new CachedQuery(5));
        await Assert.That(handler.CallCount).IsEqualTo(2);
    }

    [Test]
    public async Task Send_invalidation_with_no_matching_cached_entries_completes_without_error()
    {
        var mediator = TestServices.BuildMediator();

        await Assert.That(async () =>
        {
            await mediator.Send(new InvalidateAlphaCommand());
            await mediator.Send(new InvalidateAlphaCommand());
        }).ThrowsNothing();
    }

    [Test]
    public async Task Send_entry_cached_after_invalidation_is_cached_again_normally()
    {
        var handler = new CountingHandler();
        var mediator = TestServices.BuildMediator(handler);

        await mediator.Send(new CachedQuery(5));
        await mediator.Send(new InvalidateAlphaCommand());

        await mediator.Send(new CachedQuery(5));
        await mediator.Send(new CachedQuery(5));

        await Assert.That(handler.CallCount).IsEqualTo(2);
    }
}
