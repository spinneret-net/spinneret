using System.Reflection;
using Spinneret.View;
using Spinneret.ViewModel;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Add* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection;

public static class MvvmServiceCollectionExtensions
{
    /// <summary>
    /// Registers the MVVM services — <see cref="IViewModelFactory"/>, the view-refresh
    /// coordinator, the view resolver built by scanning for <c>ViewBase&lt;T&gt;</c> subclasses,
    /// and the render context — scanning the entry assembly and auto-registering view models.
    /// </summary>
    /// <typeparam name="TRenderContext">
    /// The host's render context: <see cref="ClientRenderContext"/> for WebAssembly, or
    /// <c>ServerRenderContext</c> from Spinneret.View.Server for Blazor Server. That one reads
    /// <c>HttpContext</c>, which ships only in the shared framework, so it lives in its own
    /// package to keep this one restorable from WebAssembly.
    /// </typeparam>
    public static IServiceCollection AddMvvm<TRenderContext>(this IServiceCollection services)
        where TRenderContext : class, IRenderContext
        => services.AddMvvm<TRenderContext>(_ => { });

    /// <summary>
    /// Registers the MVVM services with explicit configuration — which assemblies to scan
    /// and whether to auto-register view models.
    /// </summary>
    public static IServiceCollection AddMvvm<TRenderContext>(
        this IServiceCollection services,
        Action<MvvmOptions> configure) where TRenderContext : class, IRenderContext
    {
        var options = new MvvmOptions();
        configure(options);

        var assemblies = options.Assemblies.Count > 0
            ? options.Assemblies.ToArray()
            : [Assembly.GetEntryAssembly() ?? throw new InvalidOperationException(
                "Entry assembly could not be determined. Specify assemblies explicitly: AddMvvm<T>(o => o.Assemblies.Add(typeof(Program).Assembly)).")];

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
