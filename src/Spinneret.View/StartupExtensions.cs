using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Spinneret.ViewModel;

namespace Spinneret.View;

public static class StartupExtensions
{
    public static IServiceCollection AddMvvm<TRenderContext>(
        this IServiceCollection services,
        bool autoRegisterViewModels,
        params Assembly[] assemblies) where TRenderContext : class, IRenderContext
    {
        assemblies = assemblies.Length > 0
            ? assemblies
            : [Assembly.GetEntryAssembly() ?? throw new Exception("Entry assembly could not be determined! Please specify assemblies explicitly.")];

        var viewModelViewPairs = FindDerivedTypes(typeof(ViewBase<>), assemblies).ToList();

        if (autoRegisterViewModels)
        {
            foreach (var viewModelType in FindImplementationsOfInterface(typeof(IViewModel), assemblies))
            {
                services.AddTransient(viewModelType);
            }
        }

        return services
            .AddScoped<IViewModelFactory, ViewModelFactory>()
            .AddScoped<IViewRefreshCoordinator, ViewRefreshCoordinator>()
            .AddSingleton<IViewResolver>(new ViewResolver(viewModelViewPairs))
            .AddSingleton<IRenderContext, TRenderContext>();
    }

    private static IEnumerable<(Type GenericArgument, Type DerivedType)> FindDerivedTypes(Type genericBaseType, IEnumerable<Assembly> assemblies)
    {
        return assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.BaseType != null &&
                        t.BaseType.IsGenericType &&
                        t.BaseType.GetGenericTypeDefinition() == genericBaseType)
            .Select(t => (t.BaseType!.GetGenericArguments()[0], t));
    }

    private static IEnumerable<Type> FindImplementationsOfInterface(Type interfaceType, IEnumerable<Assembly> assemblies)
    {
        return assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass && !t.IsAbstract && interfaceType.IsAssignableFrom(t));
    }
}