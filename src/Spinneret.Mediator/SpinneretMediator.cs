using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Spinneret.Functional;

namespace Spinneret.Mediator;

/// <summary>
/// Dispatches requests to their registered handler. Implemented by the library — inject and call.
/// <para>
/// Caching contract: when a request type carries <see cref="CacheAttribute"/>, the request
/// object itself is the cache and coalescing key, so cached request types need value equality
/// (records are the natural fit). A request type without value equality never hits the cache.
/// </para>
/// </summary>
public interface ISpinneretMediator
{
    /// <summary>Sends a request that produces no response.</summary>
    Task Send(IRequest<Unit> request, CancellationToken cancellationToken = default);

    /// <summary>Sends a request and returns its handler's response.</summary>
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Controls the mediator's response cache. Implemented by the library — inject and call.
/// </summary>
public interface IMediatorCache
{
    /// <summary>Removes every cached response.</summary>
    void Clear();
}

internal sealed class SpinneretMediator(IServiceProvider serviceProvider, ITagIndexedCache cache) : ISpinneretMediator
{
    public async Task Send(IRequest<Unit> request, CancellationToken cancellationToken = default)
    {
        await Send<Unit>(request, cancellationToken);
    }

    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        var requestType = request.GetType();
        var cacheAttr = requestType.GetCustomAttribute<CacheAttribute>();
        var invalidateAttr = requestType.GetCustomAttribute<InvalidateCacheAttribute>();

        TResponse response;
        if (cacheAttr is not null && typeof(TResponse) != typeof(Unit))
        {
            var sharedTask = cache.GetOrCreate(
                request,
                () => Dispatch(request, CancellationToken.None),
                cacheAttr.Duration,
                cacheAttr.Tags);

            response = await sharedTask.WaitAsync(cancellationToken);
        }
        else
        {
            response = await Dispatch(request, cancellationToken);
        }

        if (invalidateAttr is not null)
            foreach (var tag in invalidateAttr.Tags)
                cache.RemoveByTag(tag);

        return response;
    }

    private Task<TResponse> Dispatch<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken)
    {
        var handlerType = typeof(IRequestHandler<,>).MakeGenericType(request.GetType(), typeof(TResponse));
        var handler = serviceProvider.GetRequiredService(handlerType);
        var method = handlerType.GetMethod(nameof(IRequestHandler<,>.Handle))!;
        try
        {
            return (Task<TResponse>)method.Invoke(handler, [request, cancellationToken])!;
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            // Unwrap so synchronously thrown handler exceptions surface like async ones,
            // with the original stack trace preserved.
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw; // Unreachable.
        }
    }
}

internal sealed class MediatorCache(ITagIndexedCache cache) : IMediatorCache
{
    public void Clear() => cache.Clear();
}
