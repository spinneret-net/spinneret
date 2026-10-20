using System.Reflection;
using Spinneret.View;
using Spinneret.ViewModel;

// ReSharper disable once CheckNamespace — DI namespace for discoverability
namespace Microsoft.Extensions.DependencyInjection;

public static class MvvmServiceCollectionExtensions
{
    /// <summary>Registers the MVVM services, scanning the entry assembly and auto-registering view models.</summary>
    public static IServiceCollection AddMvvm(this IServiceCollection services)
        => services.AddMvvm(_ => { });

    /// <summary>Registers the MVVM services with explicit configuration.</summary>
    public static IServiceCollection AddMvvm(
        this IServiceCollection services,
        Action<MvvmOptions> configure)
    {
        var options = new MvvmOptions();
        configure(options);

        var assemblies = options.Assemblies.Count > 0
            ? options.Assemblies.ToArray()
            : [Assembly.GetEntryAssembly() ?? throw new InvalidOperationException(
                "Entry assembly could not be determined. Specify assemblies explicitly: AddMvvm(o => o.Assemblies.Add(typeof(Program).Assembly)).")];

        var viewModelViewPairs = FindDerivedTypes(typeof(ViewBase<>), assemblies).ToList();

        if (options.AutoRegisterViewModels)
        {
            foreach (var viewModelType in FindImplementationsOfInterface(typeof(IViewModel), assemblies))
            {
                services.AddTransient(viewModelType);
            }
        }

        return services
            .AddScoped<IViewModelFactory, ViewModelFactory>()
            .AddScoped<IViewRefreshCoordinator, ViewRefreshCoordinator>()
            .AddSingleton<IViewResolver>(new ViewResolver(viewModelViewPairs));
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
