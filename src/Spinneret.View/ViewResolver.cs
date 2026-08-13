namespace Spinneret.View;

public interface IViewResolver
{
    Type ResolveViewType(Type viewModelType);
}

internal sealed class ViewResolver : IViewResolver
{
    private const string ViewModelSuffix = "ViewModel";
    private readonly Dictionary<Type, Type[]> _viewModelToViewMapper;

    public ViewResolver(IEnumerable<(Type ViewModel, Type View)> viewModelViewPairs)
    {
        _viewModelToViewMapper = viewModelViewPairs
            .GroupBy(pair => pair.ViewModel)
            .ToDictionary(g => g.Key, g => g.Select(p => p.View).ToArray());
    }

    public Type ResolveViewType(Type viewModel)
    {
        return _viewModelToViewMapper.TryGetValue(viewModel, out var viewMapper)
            ? ResolveFromMappedViews(viewModel, viewMapper)
            : throw new InvalidOperationException($"Failed to resolve view for view model: {viewModel.Name}");
    }

    private static Type ResolveFromMappedViews(Type viewModel, Type[] mappedViews)
    {
        if (mappedViews.Length == 1)
        {
            return mappedViews[0];
        }

        var preferredViewName = RemoveFromEnd(viewModel.Name, ViewModelSuffix);

        return mappedViews.FirstOrDefault(x => x.Name == preferredViewName) ?? mappedViews[0];
    }

    private static string RemoveFromEnd(string s, string suffix)
    {
        if (s.EndsWith(suffix))
        {
            return s[..^suffix.Length];
        }

        return s;
    }
}