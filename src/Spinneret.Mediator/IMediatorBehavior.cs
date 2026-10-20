namespace Spinneret.Mediator;

/// <summary>Wraps every <see cref="ISpinneretMediator.Send{TResponse}"/>; not calling <c>next</c> short-circuits the send.</summary>
/// <remarks>
/// Behaviors run in registration order, first registered outermost, inside the send's span.
/// They also wrap cache hits.
/// </remarks>
public interface IMediatorBehavior
{
    Task<TResponse> Handle<TResponse>(
        IRequest<TResponse> request,
        Func<Task<TResponse>> next,
        CancellationToken cancellationToken);
}
