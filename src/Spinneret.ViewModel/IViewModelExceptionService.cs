namespace Spinneret.ViewModel;

/// <summary>
/// Application-wide handler for exceptions thrown inside a view model's Run/RunIfNotBusy
/// blocks (e.g. show a toast, log). Implemented by consumers; return false to rethrow.
/// </summary>
public interface IViewModelExceptionService
{
    bool Handle(IViewModel vm, Exception e);
}
