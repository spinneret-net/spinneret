using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.Mediator.Tests;

/// <summary>
/// Behaviors wrap every send, in registration order, inside the send's own span.
/// </summary>
/// <remarks>
/// Request types are exclusive to this class for the same reason as in <see cref="MediatorTracingTests"/>:
/// the collecting listener is process-wide.
/// </remarks>
public class MediatorBehaviorTests
{
    [Test]
    public async Task Behaviors_run_in_registration_order_with_the_first_outermost()
    {
        var log = new ConcurrentQueue<string>();
        var mediator = TestServices.BuildMediator(s =>
        {
            s.AddSingleton<IMediatorBehavior>(new RecordingBehavior("A", log));
            s.AddSingleton<IMediatorBehavior>(new RecordingBehavior("B", log));
        });

        var response = await mediator.Send(new BehaviorEchoQuery(7));

        await Assert.That(response).IsEqualTo(7);
        await Assert.That(string.Join(",", log)).IsEqualTo("A:before,B:before,B:after,A:after");
    }

    [Test]
    public async Task AddMediatorBehavior_registers_a_scoped_behavior_that_the_mediator_runs()
    {
        var log = new ConcurrentQueue<string>();
        await using var provider = TestServices.BuildProvider(s =>
        {
            s.AddSingleton(log);
            s.AddMediatorBehavior<ScopedRecordingBehavior>();
        });

        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISpinneretMediator>().Send(new BehaviorEchoQuery(1));

        await Assert.That(string.Join(",", log)).IsEqualTo("scoped:before,scoped:after");
        var descriptor = provider.GetRequiredService<IServiceProviderIsService>();
        await Assert.That(descriptor.IsService(typeof(IMediatorBehavior))).IsTrue();
    }

    [Test]
    public async Task A_behavior_can_replace_the_response()
    {
        var mediator = TestServices.BuildMediator(s =>
            s.AddSingleton<IMediatorBehavior>(new ReplacingBehavior()));

        var response = await mediator.Send(new BehaviorEchoQuery(7));

        await Assert.That(response).IsEqualTo(700);
    }

    [Test]
    public async Task A_behavior_that_skips_next_short_circuits_the_handler()
    {
        var log = new ConcurrentQueue<string>();
        var mediator = TestServices.BuildMediator(s =>
        {
            s.AddSingleton<IMediatorBehavior>(new ShortCircuitBehavior());
            s.AddSingleton<IMediatorBehavior>(new RecordingBehavior("inner", log));
        });

        var response = await mediator.Send(new BehaviorEchoQuery(7));

        await Assert.That(response).IsEqualTo(-1);
        await Assert.That(log).IsEmpty();
    }

    [Test]
    public async Task A_handler_exception_passes_through_behaviors_to_the_caller()
    {
        var log = new ConcurrentQueue<string>();
        var mediator = TestServices.BuildMediator(s =>
            s.AddSingleton<IMediatorBehavior>(new RecordingBehavior("A", log)));

        await Assert.That(async () => await mediator.Send(new BehaviorThrowingQuery()))
            .Throws<InvalidOperationException>();

        await Assert.That(string.Join(",", log)).IsEqualTo("A:before,A:threw");
        var span = TracingTestListener.Collected.Single(a => a.DisplayName == $"Send {nameof(BehaviorThrowingQuery)}");
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Error);
    }

    [Test]
    public async Task A_cache_hit_still_passes_through_behaviors()
    {
        var log = new ConcurrentQueue<string>();
        var handler = new BehaviorCountingHandler();
        var mediator = TestServices.BuildMediator(s =>
        {
            s.AddSingleton<IRequestHandler<BehaviorCachedQuery, int>>(handler);
            s.AddSingleton<IMediatorBehavior>(new RecordingBehavior("A", log));
        });

        await mediator.Send(new BehaviorCachedQuery(1));
        await mediator.Send(new BehaviorCachedQuery(1));

        await Assert.That(handler.CallCount).IsEqualTo(1);
        await Assert.That(log.Count).IsEqualTo(4);
    }

    [Test]
    public async Task A_behavior_observes_the_span_of_its_own_send_and_nested_sends_chain_to_it()
    {
        var spans = new ConcurrentDictionary<Type, (ActivityTraceId Trace, ActivitySpanId Span, ActivitySpanId Parent)>();
        var mediator = TestServices.BuildMediator(s =>
            s.AddSingleton<IMediatorBehavior>(new SpanCapturingBehavior(spans)));

        await mediator.Send(new BehaviorOuterQuery());

        var outer = spans[typeof(BehaviorOuterQuery)];
        var inner = spans[typeof(BehaviorInnerQuery)];
        var outerSendSpan = TracingTestListener.Collected.Single(a => a.DisplayName == $"Send {nameof(BehaviorOuterQuery)}");
        await Assert.That(outer.Span).IsEqualTo(outerSendSpan.SpanId);
        await Assert.That(inner.Trace).IsEqualTo(outer.Trace);
        await Assert.That(inner.Parent).IsEqualTo(outer.Span);
    }

    [Test]
    public async Task The_cancellation_token_reaches_the_behavior()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken? seen = null;
        var mediator = TestServices.BuildMediator(s =>
            s.AddSingleton<IMediatorBehavior>(new TokenCapturingBehavior(t => seen = t)));

        await mediator.Send(new BehaviorEchoQuery(1), cts.Token);

        await Assert.That(seen).IsEqualTo(cts.Token);
    }
}

// --- Behaviors ---------------------------------------------------------------------

internal sealed class RecordingBehavior(string name, ConcurrentQueue<string> log) : IMediatorBehavior
{
    public async Task<TResponse> Handle<TResponse>(IRequest<TResponse> request, Func<Task<TResponse>> next, CancellationToken cancellationToken)
    {
        log.Enqueue($"{name}:before");
        try
        {
            var response = await next();
            log.Enqueue($"{name}:after");
            return response;
        }
        catch
        {
            log.Enqueue($"{name}:threw");
            throw;
        }
    }
}

internal sealed class ScopedRecordingBehavior(ConcurrentQueue<string> log) : IMediatorBehavior
{
    public async Task<TResponse> Handle<TResponse>(IRequest<TResponse> request, Func<Task<TResponse>> next, CancellationToken cancellationToken)
    {
        log.Enqueue("scoped:before");
        var response = await next();
        log.Enqueue("scoped:after");
        return response;
    }
}

internal sealed class ReplacingBehavior : IMediatorBehavior
{
    public async Task<TResponse> Handle<TResponse>(IRequest<TResponse> request, Func<Task<TResponse>> next, CancellationToken cancellationToken)
    {
        var response = await next();
        return response is int i ? (TResponse)(object)(i * 100) : response;
    }
}

internal sealed class ShortCircuitBehavior : IMediatorBehavior
{
    public Task<TResponse> Handle<TResponse>(IRequest<TResponse> request, Func<Task<TResponse>> next, CancellationToken cancellationToken)
        => Task.FromResult((TResponse)(object)-1);
}

internal sealed class SpanCapturingBehavior(
    ConcurrentDictionary<Type, (ActivityTraceId Trace, ActivitySpanId Span, ActivitySpanId Parent)> spans) : IMediatorBehavior
{
    public Task<TResponse> Handle<TResponse>(IRequest<TResponse> request, Func<Task<TResponse>> next, CancellationToken cancellationToken)
    {
        var activity = Activity.Current!;
        spans[request.GetType()] = (activity.TraceId, activity.SpanId, activity.ParentSpanId);
        return next();
    }
}

internal sealed class TokenCapturingBehavior(Action<CancellationToken> capture) : IMediatorBehavior
{
    public Task<TResponse> Handle<TResponse>(IRequest<TResponse> request, Func<Task<TResponse>> next, CancellationToken cancellationToken)
    {
        capture(cancellationToken);
        return next();
    }
}

// --- Requests exclusive to this class ---------------------------------------------

internal sealed record BehaviorEchoQuery(int Value) : IRequest<int>;

internal sealed class BehaviorEchoHandler : IRequestHandler<BehaviorEchoQuery, int>
{
    public Task<int> Handle(BehaviorEchoQuery request, CancellationToken cancellationToken)
        => Task.FromResult(request.Value);
}

internal sealed record BehaviorThrowingQuery : IRequest<int>;

internal sealed class BehaviorThrowingHandler : IRequestHandler<BehaviorThrowingQuery, int>
{
    public async Task<int> Handle(BehaviorThrowingQuery request, CancellationToken cancellationToken)
    {
        await Task.Yield();
        throw new InvalidOperationException("handler failure");
    }
}

[Cache(60)]
internal sealed record BehaviorCachedQuery(int Id) : IRequest<int>;

internal sealed class BehaviorCountingHandler : IRequestHandler<BehaviorCachedQuery, int>
{
    private int _callCount;
    public int CallCount => Volatile.Read(ref _callCount);

    public Task<int> Handle(BehaviorCachedQuery request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        return Task.FromResult(request.Id);
    }
}

internal sealed record BehaviorOuterQuery : IRequest<int>;

internal sealed class BehaviorOuterHandler(ISpinneretMediator mediator) : IRequestHandler<BehaviorOuterQuery, int>
{
    public Task<int> Handle(BehaviorOuterQuery request, CancellationToken cancellationToken)
        => mediator.Send(new BehaviorInnerQuery(), cancellationToken);
}

internal sealed record BehaviorInnerQuery : IRequest<int>;

internal sealed class BehaviorInnerHandler : IRequestHandler<BehaviorInnerQuery, int>
{
    public Task<int> Handle(BehaviorInnerQuery request, CancellationToken cancellationToken)
        => Task.FromResult(1);
}
