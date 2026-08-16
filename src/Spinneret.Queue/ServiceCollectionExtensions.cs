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
    /// <see cref="IEnvelopeQueue"/>, <see cref="IDeadLetterWriter"/> or
    /// <see cref="IDeadLetterStore"/> — every one of those is a transport or host decision, and each
    /// transport supplies its own default.
    /// </remarks>
    /// <remarks>
    /// Takes a collection rather than a <c>params</c> array deliberately: a <c>params</c> parameter
    /// must come last, which would permanently block adding anything to this signature.
    /// </remarks>
    public static IServiceCollection AddQueueCore(
        this IServiceCollection services, IReadOnlyCollection<Assembly> requestAssemblies)
    {
        ArgumentNullException.ThrowIfNull(requestAssemblies);
        if (requestAssemblies.Count == 0)
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

        // Resend is transport-agnostic, so it belongs here beside the dispatcher rather than being
        // repeated by every store package. It resolves only once something asks for it, which keeps
        // a host that registered no IDeadLetterStore — because it has no admin page — working.
        services.TryAddSingleton<IQueueTransactionScope, PassThroughQueueTransactionScope>();
        services.TryAddSingleton<IDeadLetterResender, DeadLetterResender>();
        return services;
    }
}
