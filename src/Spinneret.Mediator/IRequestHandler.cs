namespace Spinneret.Mediator;

/// <summary>Handles <typeparamref name="TRequest"/>; each request type must have exactly one handler.</summary>
public interface IRequestHandler<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}
