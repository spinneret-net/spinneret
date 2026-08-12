using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.Mediator;

public interface ISpinneretMediator
{
    Task Send(IRequest<Unit> request, CancellationToken cancellationToken = default);
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
    void ClearCache();
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

    public void ClearCache() => cache.Clear();

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
