namespace Spinneret.ViewModel;

/// <summary>Handles exceptions thrown in a view model's Run/RunIfNotBusy blocks; return false to rethrow.</summary>
public interface IViewModelExceptionService
{
    bool Handle(IViewModel vm, Exception e);
}
