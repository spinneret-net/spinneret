using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Spinneret.Functional;

namespace Spinneret.Mediator;

/// <summary>Dispatches requests to their registered handler.</summary>
/// <remarks>For <see cref="CacheAttribute"/> requests the request object is the cache key, so they need value equality.</remarks>
public interface ISpinneretMediator
{
    /// <summary>Sends a request that produces no response.</summary>
    Task Send(IRequest<Unit> request, CancellationToken cancellationToken = default)
        => Send<Unit>(request, cancellationToken);

    /// <summary>Sends a request and returns its handler's response.</summary>
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}

/// <summary>Controls the mediator's response cache.</summary>
public interface IMediatorCache
{
    /// <summary>Removes every cached response.</summary>
    void Clear();
}

internal sealed class SpinneretMediator(
    IServiceProvider serviceProvider,
    ITagIndexedCache cache,
    IEnumerable<IMediatorBehavior> behaviors) : ISpinneretMediator
{
    private readonly IMediatorBehavior[] _behaviors = [.. behaviors];

    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        using var activity = MediatorTracing.StartSend(request.GetType());

        try
        {
            var pipeline = () => SendCore(request, activity, cancellationToken);
            for (var i = _behaviors.Length - 1; i >= 0; i--)
            {
                var behavior = _behaviors[i];
                var next = pipeline;
                pipeline = () => behavior.Handle(request, next, cancellationToken);
            }

            return await pipeline();
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            throw;
        }
    }

    private async Task<TResponse> SendCore<TResponse>(IRequest<TResponse> request, Activity? activity, CancellationToken cancellationToken)
    {
        var requestType = request.GetType();
        var cacheAttr = requestType.GetCustomAttribute<CacheAttribute>();
        var invalidateAttr = requestType.GetCustomAttribute<InvalidateCacheAttribute>();

        TResponse response;
        if (cacheAttr is not null && typeof(TResponse) != typeof(Unit))
        {
            // Coalesced: the shared handler runs under the first caller's activity, ignoring each caller's token.
            var dispatched = false;
            var sharedTask = cache.GetOrCreate(
                request,
                () =>
                {
                    dispatched = true;
                    return Dispatch(request, CancellationToken.None);
                },
                cacheAttr.Duration,
                cacheAttr.Tags);

            response = await sharedTask.WaitAsync(cancellationToken);
            activity?.SetTag(MediatorTags.Cache,
                dispatched ? MediatorCacheOutcome.Miss : MediatorCacheOutcome.Hit);
        }
        else
        {
            activity?.SetTag(MediatorTags.Cache, MediatorCacheOutcome.Bypass);
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
            // Surface synchronous handler throws like async ones, keeping the stack trace.
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw; // Unreachable.
        }
    }
}

internal sealed class MediatorCache(ITagIndexedCache cache) : IMediatorCache
{
    public void Clear() => cache.Clear();
}
