using System.Reflection;
using Spinneret.Mediator;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Add* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection;

public static class MediatorServiceCollectionExtensions
{
    /// <summary>
    /// Registers the mediator, its response cache, and every <see cref="IRequestHandler{TRequest, TResponse}"/>
    /// found in <paramref name="mediatorAssemblies"/> (the entry assembly when none are given).
    /// Fails at startup on duplicate handlers or invalid <see cref="CacheAttribute"/> /
    /// <see cref="InvalidateCacheAttribute"/> declarations.
    /// </summary>
    public static IServiceCollection AddMediator(this IServiceCollection services, params Assembly[] mediatorAssemblies)
    {
        mediatorAssemblies = mediatorAssemblies.Length > 0
            ? mediatorAssemblies
            : [Assembly.GetEntryAssembly() ?? throw new InvalidOperationException(
                "Entry assembly could not be determined. Specify mediator assemblies explicitly: AddMediator(typeof(Program).Assembly).")];

        var handlersByRequest = new Dictionary<Type, Type>();
        foreach (var assembly in mediatorAssemblies)
        {
            var handlers = assembly.GetTypes()
                .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false })
                .SelectMany(t => t.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>))
                    .Select(i => (Impl: t, Iface: i)));

            foreach (var (impl, iface) in handlers)
            {
                if (!handlersByRequest.TryAdd(iface, impl))
                    throw new InvalidOperationException(
                        $"Duplicate handlers for {iface.GetGenericArguments()[0].Name}: " +
                        $"{handlersByRequest[iface].FullName} and {impl.FullName}. A request type must have exactly one handler.");

                ValidateCacheAttributes(iface.GetGenericArguments()[0]);
                services.AddTransient(iface, impl);
            }
        }

        services.AddMemoryCache();
        services.AddSingleton<ITagIndexedCache, TagIndexedCache>();
        services.AddSingleton<IMediatorCache, MediatorCache>();
        return services.AddScoped<ISpinneretMediator, SpinneretMediator>();
    }

    private static void ValidateCacheAttributes(Type requestType)
    {
        // Attribute constructors validate duration and tag types; instantiating them here
        // surfaces a bad declaration at startup instead of on the first Send.
        try
        {
            _ = requestType.GetCustomAttribute<CacheAttribute>();
            _ = requestType.GetCustomAttribute<InvalidateCacheAttribute>();
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException($"Invalid cache declaration on {requestType.FullName}: {e.Message}", e);
        }
        catch (TargetInvocationException e) when (e.InnerException is ArgumentException inner)
        {
            throw new InvalidOperationException($"Invalid cache declaration on {requestType.FullName}: {inner.Message}", inner);
        }
    }
}
