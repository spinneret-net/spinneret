namespace Spinneret.ViewModel;

/// <summary>
/// Resolves view models from the container. Implemented by the library — inject and call.
/// </summary>
public interface IViewModelFactory
{
    T Create<T>() where T : IViewModel;
}

public sealed class ViewModelFactory(IServiceProvider services) : IViewModelFactory
{
    public T Create<T>() where T : IViewModel
    {
        var instance = services.GetService(typeof(T));

        if (instance is null)
            throw new InvalidOperationException($"No service registered for model type {typeof(T).Name}");

        return (T)instance;
    }
}
