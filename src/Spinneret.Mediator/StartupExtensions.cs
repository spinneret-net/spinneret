using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.Mediator;

public static class StartupExtensions
{
    public static IServiceCollection AddMediator(this IServiceCollection services, params Assembly[] mediatorAssemblies)
    {
        mediatorAssemblies = mediatorAssemblies.Length > 0
            ? mediatorAssemblies
            : [Assembly.GetEntryAssembly() ?? throw new Exception("Entry assembly could not be determined! Please specify mediator assemblies explicitly.")];

        foreach (var assembly in mediatorAssemblies)
        {
            var handlers = assembly.GetTypes()
                .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false })
                .SelectMany(t => t.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>))
                    .Select(i => (Impl: t, Iface: i)));

            foreach (var (impl, iface) in handlers)
                services.AddTransient(iface, impl);
        }

        services.AddMemoryCache();
        services.AddSingleton<ITagIndexedCache, TagIndexedCache>();
        return services.AddScoped<ISpinneretMediator, SpinneretMediator>();
    }
}
