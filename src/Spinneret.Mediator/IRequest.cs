namespace Spinneret.Mediator;

/// <summary>
/// Marks a type as a mediator request producing <typeparamref name="TResponse"/>
/// (use <see cref="Spinneret.Functional.Unit"/> for requests with no response).
/// Implemented by consumers; it is a pure marker and will never gain members.
/// </summary>
public interface IRequest<TResponse>;
