namespace Spinneret.ViewModel;

public interface IValidationStateProvider
{
    public IValidationState ValidationState { get; }
}