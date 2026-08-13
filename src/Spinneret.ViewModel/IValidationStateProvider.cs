namespace Spinneret.ViewModel;

/// <summary>
/// Anything that owns an <see cref="IValidationState"/> — the anchor type for bindings and
/// the view-model parser. <see cref="ViewModelBase"/> implements it; implement it yourself
/// only for binding targets that are not view models.
/// </summary>
public interface IValidationStateProvider
{
    public IValidationState ValidationState { get; }
}
