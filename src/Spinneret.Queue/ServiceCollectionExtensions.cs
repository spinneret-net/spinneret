using System.Reflection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spinneret.Queue;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Add* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection;

public static class QueueCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers transport-agnostic queue infrastructure: the type registry, the dispatcher and the
    /// delivery processor. Called by the transport packages' own registration, so applications wire
    /// up a queue by calling that one instead of this.
    /// </summary>
    /// <remarks>
    /// Deliberately registers no <see cref="IQueuePayloadSerializer"/>, <see cref="IQueue"/>,
    /// <see cref="IEnvelopeQueue"/> or <see cref="IDeadLetterWriter"/> — every one of those is a
    /// transport or host decision, and each transport supplies its own default.
    /// </remarks>
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
