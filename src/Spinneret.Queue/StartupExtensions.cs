using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Spinneret.Queue;

public static class StartupExtensions
{
    /// <summary>
    /// Registers transport-agnostic queue infrastructure: the type registry, the
    /// dispatcher, and a default no-op serializer that callers (e.g. the GCP transport)
    /// should replace with one that uses the host's JsonSerializerOptions.
    /// </summary>
    public static IServiceCollection AddQueueCore(this IServiceCollection services, params Assembly[] requestAssemblies)
    {
        if (requestAssemblies.Length == 0)
            throw new ArgumentException("At least one assembly containing IRequest<> types must be provided.", nameof(requestAssemblies));

        return services.AddQueueCore(new QueueTypeRegistry(requestAssemblies));
    }

    /// <summary>
    /// Overload for transports that build the registry themselves so they can validate their
    /// configuration (e.g. channel mappings) against it at startup.
    /// </summary>
    public static IServiceCollection AddQueueCore(this IServiceCollection services, QueueTypeRegistry registry)
    {
        services.AddSingleton(registry);
        services.TryAddScoped<IQueueDispatcher, QueueDispatcher>();
        services.TryAddScoped<IQueueDeliveryProcessor, QueueDeliveryProcessor>();
        services.TryAddScoped<IQueueDispatchBoundary, DirectDispatchBoundary>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
