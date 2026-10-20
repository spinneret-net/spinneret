namespace Spinneret.ViewModel;

/// <summary>Owns an <see cref="IValidationState"/>; implement only for binding targets that aren't view models.</summary>
public interface IValidationStateProvider
{
    public IValidationState ValidationState { get; }
}
