using System.Diagnostics;

namespace Spinneret.Mediator.Tests;

/// <summary>
/// One span per <see cref="ISpinneretMediator.Send{TResponse}"/>, so a trace shows which request ran
/// and — when the response came from the cache — why no handler work appears beneath it.
/// </summary>
/// <remarks>
/// Spans are recorded by <see cref="TracingTestListener"/>; collection is keyed by span name, so
/// every test below uses a request type no other test sends.
/// </remarks>
public class MediatorTracingTests
{
    private static IReadOnlyList<Activity> SpansFor<TRequest>() =>
        [.. TracingTestListener.Collected.Where(a => a.DisplayName == $"Send {typeof(TRequest).Name}")];

    [Test]
    public async Task Send_emits_a_span_named_after_the_request()
    {
        var mediator = TestServices.BuildMediator();

        await mediator.Send(new TracedQuery("abc"));

        var span = SpansFor<TracedQuery>().Single();
        await Assert.That(span.Kind).IsEqualTo(ActivityKind.Internal);
        await Assert.That(span.GetTagItem("spinneret.request.type")).IsEqualTo(typeof(TracedQuery).FullName);
        await Assert.That(span.GetTagItem("spinneret.mediator.cache")).IsEqualTo("bypass");
    }

    [Test]
    public async Task A_cache_hit_still_gets_a_span_and_says_so()
    {
        var mediator = TestServices.BuildMediator();

        await mediator.Send(new TracedCachedQuery(1));
        await mediator.Send(new TracedCachedQuery(1));

        var caches = SpansFor<TracedCachedQuery>()
            .Select(a => a.GetTagItem("spinneret.mediator.cache"))
            .ToList();

        await Assert.That(caches).HasCount(2);
        await Assert.That(caches).Contains("miss");
        await Assert.That(caches).Contains("hit");
    }

    [Test]
    public async Task A_failing_handler_marks_the_span_as_an_error()
    {
        var mediator = TestServices.BuildMediator();

        await Assert.That(async () => await mediator.Send(new TracedThrowingQuery())).Throws<InvalidOperationException>();

        var span = SpansFor<TracedThrowingQuery>().Single();
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Error);
    }
}

// Request types exclusive to this class. The shared fixtures are sent by other tests in the
// assembly, and the collecting listener is process-wide, so reusing one would mix in their spans.
internal sealed record TracedQuery(string Text) : IRequest<string>;

internal sealed class TracedHandler : IRequestHandler<TracedQuery, string>
{
    public Task<string> Handle(TracedQuery request, CancellationToken cancellationToken)
        => Task.FromResult(request.Text);
}

[Cache(60)]
internal sealed record TracedCachedQuery(int Id) : IRequest<int>;

internal sealed class TracedCachedHandler : IRequestHandler<TracedCachedQuery, int>
{
    public Task<int> Handle(TracedCachedQuery request, CancellationToken cancellationToken)
        => Task.FromResult(request.Id);
}

internal sealed record TracedThrowingQuery : IRequest<int>;

internal sealed class TracedThrowingHandler : IRequestHandler<TracedThrowingQuery, int>
{
    public async Task<int> Handle(TracedThrowingQuery request, CancellationToken cancellationToken)
    {
        await Task.Yield();
        throw new InvalidOperationException("handler failure");
    }
}
