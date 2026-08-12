using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.Mediator.Tests;

internal enum CacheTag { Alpha, Beta, Gamma }

// --- Plain (uncached) requests -------------------------------------------------

internal sealed record EchoQuery(int Value) : IRequest<int>;

internal sealed class EchoHandler : IRequestHandler<EchoQuery, int>
{
    public Task<int> Handle(EchoQuery request, CancellationToken cancellationToken)
        => Task.FromResult(request.Value);
}

internal abstract class AbstractEchoHandler : IRequestHandler<EchoQuery, int>
{
    public abstract Task<int> Handle(EchoQuery request, CancellationToken cancellationToken);
}

internal sealed record UppercaseQuery(string Text) : IRequest<string>;

internal sealed class UppercaseHandler : IRequestHandler<UppercaseQuery, string>
{
    public Task<string> Handle(UppercaseQuery request, CancellationToken cancellationToken)
        => Task.FromResult(request.Text.ToUpperInvariant());
}

internal sealed record VoidCommand : IRequest<Unit>;

internal sealed class VoidCommandHandler : IRequestHandler<VoidCommand, Unit>
{
    private int _callCount;
    public int CallCount => Volatile.Read(ref _callCount);

    public Task<Unit> Handle(VoidCommand request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        return Task.FromResult(Unit.Value);
    }
}

internal sealed record TokenEchoQuery : IRequest<CancellationToken>;

internal sealed class TokenEchoHandler : IRequestHandler<TokenEchoQuery, CancellationToken>
{
    public Task<CancellationToken> Handle(TokenEchoQuery request, CancellationToken cancellationToken)
        => Task.FromResult(cancellationToken);
}

internal sealed record ThrowingQuery : IRequest<int>;

internal sealed class ThrowingHandler : IRequestHandler<ThrowingQuery, int>
{
    public async Task<int> Handle(ThrowingQuery request, CancellationToken cancellationToken)
    {
        await Task.Yield();
        throw new InvalidOperationException("handler failure");
    }
}

internal sealed record SyncThrowingQuery : IRequest<int>;

internal sealed class SyncThrowingHandler : IRequestHandler<SyncThrowingQuery, int>
{
    // Deliberately non-async: throws synchronously instead of returning a faulted task.
    public Task<int> Handle(SyncThrowingQuery request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("sync handler failure");
}

internal sealed record NoHandlerQuery : IRequest<int>;

// Open-generic handler type definition: assembly scanning must skip it, since an
// open generic implementation cannot be registered against a closed service type.
internal sealed class OpenGenericEchoHandler<T> : IRequestHandler<EchoQuery, int>
{
    public Task<int> Handle(EchoQuery request, CancellationToken cancellationToken)
        => Task.FromResult(request.Value);
}

// --- Cached requests -----------------------------------------------------------

[Cache(60, CacheTag.Alpha)]
internal sealed record CachedQuery(int Id) : IRequest<int>;

[Cache(60, CacheTag.Alpha, CacheTag.Beta, CacheTag.Gamma)]
internal sealed record MultiTagQuery(int Id) : IRequest<int>;

[Cache(60, CacheTag.Beta)]
internal sealed record BetaTaggedQuery(int Id) : IRequest<int>;

[Cache(1, CacheTag.Alpha)]
internal sealed record ShortLivedQuery(int Id) : IRequest<int>;

[Cache(60)]
internal sealed record CachedTokenEchoQuery : IRequest<CancellationToken>;

internal sealed class CachedTokenEchoHandler : IRequestHandler<CachedTokenEchoQuery, CancellationToken>
{
    public Task<CancellationToken> Handle(CachedTokenEchoQuery request, CancellationToken cancellationToken)
        => Task.FromResult(cancellationToken);
}

[Cache(60)]
internal sealed record CachedUnitCommand : IRequest<Unit>;

internal sealed class CachedUnitCommandHandler : IRequestHandler<CachedUnitCommand, Unit>
{
    private int _callCount;
    public int CallCount => Volatile.Read(ref _callCount);

    public Task<Unit> Handle(CachedUnitCommand request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        return Task.FromResult(Unit.Value);
    }
}

// A class (not a record) so cache-key equality falls back to reference equality.
[Cache(60, CacheTag.Alpha)]
internal sealed class ReferenceKeyQuery : IRequest<int>;

internal sealed class ReferenceKeyHandler : IRequestHandler<ReferenceKeyQuery, int>
{
    private int _callCount;
    public int CallCount => Volatile.Read(ref _callCount);

    public Task<int> Handle(ReferenceKeyQuery request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        return Task.FromResult(42);
    }
}

/// <summary>
/// Handler for all cached int queries. Counts invocations, can be gated on a
/// TaskCompletionSource, can throw on demand, and can have its result overridden.
/// </summary>
internal sealed class CountingHandler :
    IRequestHandler<CachedQuery, int>,
    IRequestHandler<MultiTagQuery, int>,
    IRequestHandler<BetaTaggedQuery, int>,
    IRequestHandler<ShortLivedQuery, int>
{
    private int _callCount;
    private TaskCompletionSource<int>? _pending;

    public int CallCount => Volatile.Read(ref _callCount);
    public bool ThrowOnNextCall { get; set; }
    public int? OverrideResult { get; set; }

    public void SetPending(TaskCompletionSource<int> tcs) => Volatile.Write(ref _pending, tcs);

    private async Task<int> HandleCore(int id)
    {
        Interlocked.Increment(ref _callCount);

        if (ThrowOnNextCall)
        {
            ThrowOnNextCall = false;
            throw new InvalidOperationException("simulated failure");
        }

        var pending = Interlocked.Exchange(ref _pending, null);
        if (pending is not null)
            return await pending.Task;

        return OverrideResult ?? id;
    }

    public Task<int> Handle(CachedQuery request, CancellationToken cancellationToken) => HandleCore(request.Id);
    public Task<int> Handle(MultiTagQuery request, CancellationToken cancellationToken) => HandleCore(request.Id);
    public Task<int> Handle(BetaTaggedQuery request, CancellationToken cancellationToken) => HandleCore(request.Id);
    public Task<int> Handle(ShortLivedQuery request, CancellationToken cancellationToken) => HandleCore(request.Id);
}

// --- Invalidating requests -----------------------------------------------------

[InvalidateCache(CacheTag.Alpha)]
internal sealed record InvalidateAlphaCommand : IRequest<Unit>;

internal sealed class InvalidateAlphaCommandHandler : IRequestHandler<InvalidateAlphaCommand, Unit>
{
    public Task<Unit> Handle(InvalidateAlphaCommand request, CancellationToken cancellationToken)
        => Task.FromResult(Unit.Value);
}

[InvalidateCache(CacheTag.Gamma)]
internal sealed record InvalidateGammaCommand : IRequest<Unit>;

internal sealed class InvalidateGammaCommandHandler : IRequestHandler<InvalidateGammaCommand, Unit>
{
    public Task<Unit> Handle(InvalidateGammaCommand request, CancellationToken cancellationToken)
        => Task.FromResult(Unit.Value);
}

[InvalidateCache(CacheTag.Alpha)]
internal sealed record InvalidateAlphaQuery : IRequest<int>;

internal sealed class InvalidateAlphaQueryHandler : IRequestHandler<InvalidateAlphaQuery, int>
{
    public Task<int> Handle(InvalidateAlphaQuery request, CancellationToken cancellationToken)
        => Task.FromResult(123);
}

// --- Composition helpers -------------------------------------------------------

internal static class TestServices
{
    public static ServiceProvider BuildProvider(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddMediator(typeof(TestServices).Assembly);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    public static ISpinneretMediator BuildMediator(Action<IServiceCollection>? configure = null)
        => BuildProvider(configure).GetRequiredService<ISpinneretMediator>();

    public static ISpinneretMediator BuildMediator(CountingHandler handler)
        => BuildMediator(services =>
        {
            services.AddSingleton<IRequestHandler<CachedQuery, int>>(handler);
            services.AddSingleton<IRequestHandler<MultiTagQuery, int>>(handler);
            services.AddSingleton<IRequestHandler<BetaTaggedQuery, int>>(handler);
            services.AddSingleton<IRequestHandler<ShortLivedQuery, int>>(handler);
        });
}
