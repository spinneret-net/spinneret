namespace Spinneret.Mediator;

/// <summary>
/// Wraps every <see cref="ISpinneretMediator.Send{TResponse}"/>. Implemented by consumers and
/// registered with <c>AddMediatorBehavior</c>; behaviors run in registration order, the first
/// registered outermost, and a behavior that does not call <c>next</c> short-circuits
/// the send.
/// </summary>
/// <remarks>
/// A behavior runs inside the send's span, so <see cref="System.Diagnostics.Activity.Current"/>
/// is the span of this very send (its parent: the enclosing request, or the outer send for a
/// nested one). It wraps the cache path as well as the handler, so a cached request still passes
/// through — inspect the request, not the response, to tell the two apart.
/// </remarks>
public interface IMediatorBehavior
{
    Task<TResponse> Handle<TResponse>(
        IRequest<TResponse> request,
        Func<Task<TResponse>> next,
        CancellationToken cancellationToken);
}
