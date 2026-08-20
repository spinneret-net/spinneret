using System.Reflection;
using Spinneret.Mediator;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Add* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection;

public static class MediatorServiceCollectionExtensions
{
    /// <summary>
    /// Registers the mediator, its response cache, and every <see cref="IRequestHandler{TRequest, TResponse}"/>
    /// found in the entry assembly. Fails at startup on duplicate handlers or invalid
    /// <see cref="CacheAttribute"/> / <see cref="InvalidateCacheAttribute"/> declarations.
    /// </summary>
    public static IServiceCollection AddMediator(this IServiceCollection services) =>
        services.AddMediator([
            Assembly.GetEntryAssembly() ?? throw new InvalidOperationException(
                "Entry assembly could not be determined. Specify mediator assemblies explicitly: "
                + "AddMediator([typeof(Program).Assembly]).")]);

    /// <summary>
    /// Registers the mediator, its response cache, and every <see cref="IRequestHandler{TRequest, TResponse}"/>
    /// found in <paramref name="mediatorAssemblies"/>. Fails at startup on duplicate handlers or
    /// invalid <see cref="CacheAttribute"/> / <see cref="InvalidateCacheAttribute"/> declarations.
    /// </summary>
    /// <remarks>
    /// Takes a collection rather than a <c>params</c> array deliberately: a <c>params</c> parameter
    /// must come last, which would permanently block adding anything to this signature.
    /// </remarks>
    public static IServiceCollection AddMediator(
        this IServiceCollection services, IReadOnlyCollection<Assembly> mediatorAssemblies)
    {
        ArgumentNullException.ThrowIfNull(mediatorAssemblies);
        if (mediatorAssemblies.Count == 0)
            throw new ArgumentException(
                "At least one assembly containing handlers must be provided.", nameof(mediatorAssemblies));

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

    /// <summary>
    /// Registers an <see cref="IMediatorBehavior"/> that wraps every send. Behaviors run in the
    /// order they are added, the first added outermost. Scoped, like the mediator itself.
    /// </summary>
    public static IServiceCollection AddMediatorBehavior<TBehavior>(this IServiceCollection services)
        where TBehavior : class, IMediatorBehavior =>
        services.AddScoped<IMediatorBehavior, TBehavior>();

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
