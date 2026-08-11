namespace Spinneret.ViewModel;

public interface IViewModelExceptionService
{
    bool Handle(IViewModel vm, Exception e);
}