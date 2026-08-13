namespace Spinneret.Mediator;

/// <summary>
/// Handles requests of <typeparamref name="TRequest"/>. Implemented by consumers;
/// a request type must have exactly one handler, registered via AddMediator.
/// </summary>
public interface IRequestHandler<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}
